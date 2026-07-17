using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Integration.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly TestClock _clock = new();
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"goalkeeper-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_dataRoot, "goalkeeper.db");
    private GoalKeeperDbContextFactory _factory = null!;
    private EfGoalKeeperRepository _repository = null!;
    private SetupWorkflow _workflow = null!;
    private SessionArtifactStore _artifacts = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .Options;
        _factory = new(options);
        _artifacts = new(_dataRoot);
        _repository = new(_factory, _artifacts);
        _workflow = new(_repository, _clock);
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ready_setup_preserves_snapshots_and_prefills_next_contract()
    {
        var goal = await _workflow.CreateGoalAsync("Write", "Draft intro");
        await _workflow.SaveProfileAsync("Default",
            [new("Phone", VisualObservability.Observable)]);
        var draft = await _workflow.PrepareAsync(goal.Id);
        draft = draft with
        {
            TargetFocusDuration = TimeSpan.FromMinutes(40),
            ScheduledBreaks = [new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5))],
            ReasoningMode = ReasoningMode.Exploratory,
            Sensitivity = Sensitivity.Strict
        };
        var setup = await _workflow.ConfirmAsync(draft);

        await _workflow.UpdateGoalAsync(goal.Id, goal.Version, "Revised", null);
        await _workflow.SaveProfileAsync("Current",
            [new("Leave view", VisualObservability.PartiallyObservable)]);
        var stored = await _repository.GetSetupAsync(setup.Id);
        var repeat = await _workflow.PrepareAsync(goal.Id);

        Assert.Equal(SessionSetupStatus.Ready, stored!.Status);
        Assert.Equal("Write", stored.Contract.GoalTitle);
        Assert.Equal("Phone", stored.Contract.Deviations.Single().Description);
        Assert.Equal(TimeSpan.FromMinutes(40), repeat.TargetFocusDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), repeat.ScheduledBreaks.Single().Duration);
        Assert.Equal(ReasoningMode.Exploratory, repeat.ReasoningMode);
    }

    [Fact]
    public async Task Stale_goal_update_is_rejected()
    {
        var goal = await _workflow.CreateGoalAsync("Read", null);
        await _workflow.UpdateGoalAsync(goal.Id, goal.Version, "Read more", null);

        await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            _workflow.UpdateGoalAsync(goal.Id, goal.Version, "Stale", null));
    }

    [Fact]
    public async Task Migrations_create_all_phase_two_record_tables_and_defaults()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
        var settings = await _repository.GetSettingsAsync();

        Assert.Contains("Goals", tables);
        Assert.Contains("SessionContracts", tables);
        Assert.Contains("EvidenceEpisodes", tables);
        Assert.Contains("SessionReviews", tables);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.TechnicalOutageGrace);
    }

    [Fact]
    public async Task Storage_usage_aggregates_snapshot_bytes()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var goal = new GoalEntity
        {
            Id = Guid.NewGuid(),
            Title = "Store",
            Status = "Active",
            CreatedAtUtc = _clock.UtcNow,
            Version = 1
        };
        db.Goals.Add(goal);
        var contract = ContractEntity.CreateForTest(goal.Id, _clock.UtcNow);
        db.SessionContracts.Add(contract);
        var session = FocusSessionEntity.CreateForTest(goal.Id, contract.Id, _clock.UtcNow);
        db.FocusSessions.Add(session);
        db.Snapshots.AddRange(
            SnapshotEntity.CreateForTest(session.Id, 1, 120),
            SnapshotEntity.CreateForTest(session.Id, 2, 80));
        await db.SaveChangesAsync();

        var usage = await _repository.GetStorageUsageAsync(session.Id);

        Assert.Equal(200, usage.SnapshotBytes);
        Assert.Equal(2, usage.SnapshotCount);
    }

    [Fact]
    public async Task Goal_deletion_removes_only_marker_owned_session_artifacts()
    {
        var goal = await _workflow.CreateGoalAsync("Delete", null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var contract = ContractEntity.CreateForTest(goal.Id, _clock.UtcNow);
            db.SessionContracts.Add(contract);
            var session = FocusSessionEntity.CreateForTest(goal.Id, contract.Id, _clock.UtcNow);
            session.State = "EndedEarly";
            session.ArtifactDirectory = _artifacts.Claim(session.Id);
            db.FocusSessions.Add(session);
            await db.SaveChangesAsync();
        }
        var ownedPath = (await GetOnlySessionAsync()).ArtifactDirectory!;

        await _workflow.DeleteGoalAsync(goal.Id, goal.Version);

        Assert.False(Directory.Exists(ownedPath));
        Assert.Null(await _repository.GetGoalAsync(goal.Id));
    }

    [Fact]
    public async Task Unowned_artifact_directory_blocks_metadata_deletion()
    {
        var goal = await _workflow.CreateGoalAsync("Protect", null);
        var unowned = Path.Combine(_dataRoot, "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var contract = ContractEntity.CreateForTest(goal.Id, _clock.UtcNow);
            db.SessionContracts.Add(contract);
            var session = FocusSessionEntity.CreateForTest(goal.Id, contract.Id, _clock.UtcNow);
            session.State = "Fulfilled";
            session.ArtifactDirectory = unowned;
            db.FocusSessions.Add(session);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.DeleteGoalAsync(goal.Id, goal.Version));

        Assert.NotNull(await _repository.GetGoalAsync(goal.Id));
    }

    private async Task<FocusSessionEntity> GetOnlySessionAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.FocusSessions.AsNoTracking().SingleAsync();
    }

    private sealed class TestClock : IClock
    {
        public TimeSpan MonotonicNow { get; private set; }
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);
    }
}
