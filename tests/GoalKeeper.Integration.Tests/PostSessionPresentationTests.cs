using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using GoalKeeper.Web.Presentation;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Integration.Tests;

public sealed class PostSessionPresentationTests : IAsyncLifetime
{
    private readonly TestClock _clock = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"goalkeeper-post-session-{Guid.NewGuid():N}");
    private GoalKeeperDbContextFactory _factory = null!;
    private EfGoalKeeperRepository _repository = null!;
    private SetupWorkflow _workflow = null!;
    private SessionArtifactStore _artifacts = null!;
    private PostSessionPresentation _presentation = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dataRoot, "goalkeeper.db")};Pooling=False")
            .Options;
        _factory = new(options);
        _artifacts = new(_dataRoot);
        _repository = new(_factory, _artifacts);
        _workflow = new(_repository, _clock);
        _presentation = new(_repository, _factory, _clock);
        await _repository.InitializeAsync();
        await _workflow.SaveProfileAsync(
            "Original profile",
            [new("Original deviation", VisualObservability.Observable)]);
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
    public async Task History_uses_immutable_contract_and_exact_snapshot_storage()
    {
        var prepared = await CreateTerminalSessionAsync(
            "Original Goal",
            "Original description",
            snapshotBytes: [1536, 512]);
        await _workflow.UpdateGoalAsync(
            prepared.Goal.Id,
            prepared.Goal.Version,
            "Current Goal",
            "Current description");
        await _workflow.SaveProfileAsync(
            "Current profile",
            [new("Current deviation", VisualObservability.PartiallyObservable)]);

        var history = await _presentation.GetHistoryAsync(prepared.Goal.Id);

        Assert.NotNull(history);
        Assert.Equal("Current Goal", history.CurrentGoalTitle);
        Assert.Equal(2048, history.TotalStorage.SnapshotBytes);
        Assert.Equal(2, history.TotalStorage.SnapshotCount);
        var session = Assert.Single(history.Sessions);
        Assert.Equal("Original Goal", session.ContractGoalTitle);
        Assert.Equal("Original description", session.ContractGoalDescription);
        Assert.Equal("Original profile", session.DeviationProfileName);
        Assert.Equal("Original deviation", Assert.Single(session.Deviations).Description);
        Assert.Equal(2048, session.Storage.SnapshotBytes);
        Assert.Equal(2, session.Storage.SnapshotCount);
        Assert.Equal(FocusSessionState.EndedEarly, session.State);
        Assert.Equal(EndedEarlyReason.UserRequest, session.EndedEarlyReason);
        Assert.Null(session.Review);
    }

    [Fact]
    public async Task Optional_review_is_accepted_once_and_can_complete_the_goal()
    {
        var prepared = await CreateTerminalSessionAsync("Review", null, snapshotBytes: []);

        var submitted = await _presentation.SubmitReviewAsync(
            prepared.Session.Id,
            meaningfulProgress: true,
            InterventionHelpfulness.Mixed,
            "  Useful reflection.  ",
            markGoalComplete: true);

        Assert.True(submitted.MeaningfulProgress);
        Assert.Equal(InterventionHelpfulness.Mixed, submitted.Helpfulness);
        Assert.Equal("Useful reflection.", submitted.Note);
        Assert.True(submitted.MarkGoalComplete);
        Assert.Equal(GoalStatus.Completed, (await _repository.GetGoalAsync(prepared.Goal.Id))!.Status);
        var reviewPage = await _presentation.GetReviewAsync(prepared.Session.Id);
        Assert.NotNull(reviewPage!.Review);
        await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            _presentation.SubmitReviewAsync(
                prepared.Session.Id,
                false,
                InterventionHelpfulness.Unhelpful,
                null,
                false));
    }

    [Fact]
    public async Task Session_deletion_preserves_goal_and_removes_owned_artifacts()
    {
        var prepared = await CreateTerminalSessionAsync("Delete session", null, snapshotBytes: [42]);

        await _presentation.DeleteSessionAsync(prepared.Session.Id);

        Assert.NotNull(await _repository.GetGoalAsync(prepared.Goal.Id));
        Assert.Null(await _repository.GetSessionAsync(prepared.Session.Id));
        Assert.False(Directory.Exists(prepared.ArtifactDirectory));
    }

    [Fact]
    public async Task Mismatched_artifact_owner_blocks_deletion_and_preserves_metadata()
    {
        var prepared = await CreateTerminalSessionAsync("Protected", null, snapshotBytes: []);
        await File.WriteAllTextAsync(
            Path.Combine(prepared.ArtifactDirectory, ".goalkeeper-session"),
            Guid.NewGuid().ToString("D"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _presentation.DeleteSessionAsync(prepared.Session.Id));

        Assert.Contains("not owned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _repository.GetSessionAsync(prepared.Session.Id));
        Assert.True(Directory.Exists(prepared.ArtifactDirectory));
    }

    [Fact]
    public async Task Goal_deletion_cascades_sessions_and_owned_artifacts()
    {
        var prepared = await CreateTerminalSessionAsync("Delete Goal", null, snapshotBytes: [7]);

        await _presentation.DeleteGoalAsync(prepared.Goal.Id, prepared.Goal.Version);

        Assert.Null(await _repository.GetGoalAsync(prepared.Goal.Id));
        Assert.Null(await _repository.GetSessionAsync(prepared.Session.Id));
        Assert.False(Directory.Exists(prepared.ArtifactDirectory));
    }

    private async Task<PreparedSession> CreateTerminalSessionAsync(
        string title,
        string? description,
        IReadOnlyList<long> snapshotBytes)
    {
        var goalView = await _workflow.CreateGoalAsync(title, description);
        var draft = await _workflow.PrepareAsync(goalView.Id);
        draft = draft with
        {
            TargetFocusDuration = TimeSpan.FromMinutes(30),
            ScheduledBreaks = [new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(3))],
            ReasoningMode = ReasoningMode.Exploratory,
            Sensitivity = Sensitivity.Strict
        };
        var setup = await _workflow.ConfirmAsync(draft);
        var goal = Goal.Rehydrate(
            goalView.Id,
            goalView.Title,
            goalView.Description,
            goalView.Status,
            goalView.CreatedAtUtc,
            goalView.CompletedAtUtc);
        var contract = SessionContract.Rehydrate(
            setup.Contract.Id,
            new GoalSnapshot(
                setup.Contract.GoalId,
                setup.Contract.GoalTitle,
                setup.Contract.GoalDescription),
            setup.Contract.TargetFocusDuration,
            setup.Contract.ScheduledBreaks.Select(value =>
                ScheduledBreak.Create(value.ActiveFocusOffset, value.Duration)),
            new DeviationProfileSnapshot(
                setup.Contract.DeviationProfileId,
                setup.Contract.DeviationProfileName,
                setup.Contract.Deviations.Select(value => new DeviationSnapshot(
                    value.Id,
                    value.Description,
                    value.Observability)).ToArray()),
            setup.Contract.ReasoningMode,
            setup.Contract.Sensitivity,
            setup.Contract.ConfirmedAtUtc);
        var session = FocusSession.Start(goal, contract, preflightSuccessful: true, _clock);
        var artifactDirectory = _artifacts.Claim(session.Id);
        await _repository.StartSessionAsync(
            setup.Id,
            setup.Version,
            session.CreateSnapshot(),
            artifactDirectory);
        for (var index = 0; index < snapshotBytes.Count; index++)
        {
            await _repository.AddSnapshotAsync(new SnapshotWrite(
                Guid.NewGuid(),
                session.Id,
                index,
                _clock.UtcNow,
                _clock.MonotonicNow,
                Path.Combine(artifactDirectory, $"{index}.jpg"),
                snapshotBytes[index],
                SnapshotProcessingStatus.Captured,
                session.Version));
        }

        session.EndEarlyByUser();
        await _repository.UpdateSessionAsync(
            session.Id,
            new RuntimeMutation(1, session.CreateSnapshot(), []));
        return new(goalView, session, artifactDirectory);
    }

    private sealed record PreparedSession(
        GoalView Goal,
        FocusSession Session,
        string ArtifactDirectory);

    private sealed class TestClock : IClock
    {
        public TimeSpan MonotonicNow { get; private set; } = TimeSpan.FromMinutes(1);

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 18, 18, 0, 0, TimeSpan.Zero);
    }
}
