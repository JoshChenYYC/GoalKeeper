using System.Text.Json;
using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Integration.Tests;

public sealed class RuntimePersistenceTests : IAsyncLifetime
{
    private readonly RuntimeClock _clock = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"goalkeeper-runtime-{Guid.NewGuid():N}");
    private GoalKeeperDbContextFactory _factory = null!;
    private EfGoalKeeperRepository _repository = null!;
    private SetupWorkflow _workflow = null!;
    private SessionArtifactStore _artifacts = null!;

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
        await _repository.InitializeAsync();
        await _workflow.SaveProfileAsync(
            "Default",
            [new("Phone", VisualObservability.Observable)]);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(FocusSessionState.Focusing)]
    [InlineData(FocusSessionState.ScheduledBreak)]
    [InlineData(FocusSessionState.RecoveryCheckIn)]
    [InlineData(FocusSessionState.RecoveryWindow)]
    [InlineData(FocusSessionState.AwaitingResponse)]
    [InlineData(FocusSessionState.MonitoringUnavailable)]
    [InlineData(FocusSessionState.Fulfilled)]
    [InlineData(FocusSessionState.EndedEarly)]
    public async Task Every_runtime_state_round_trips_through_sqlite(
        FocusSessionState state)
    {
        var prepared = await PrepareSessionAsync("Round trip");
        await _repository.StartSessionAsync(
            prepared.Setup.Id,
            prepared.Setup.Version,
            prepared.Session.CreateSnapshot());
        MoveTo(state, prepared.Session, prepared.Deviation);
        if (prepared.Session.Version > 1)
        {
            await _repository.UpdateSessionAsync(
                prepared.Session.Id,
                new RuntimeMutation(1, prepared.Session.CreateSnapshot(), []));
        }

        var stored = await _repository.GetSessionAsync(prepared.Session.Id);
        var restoredGoal = Goal.Rehydrate(
            prepared.Goal.Id,
            prepared.Goal.Title,
            prepared.Goal.Description,
            prepared.Goal.Status,
            prepared.Goal.CreatedAtUtc,
            prepared.Goal.CompletedAtUtc);
        var restored = FocusSession.Rehydrate(
            restoredGoal,
            prepared.Contract,
            stored!.Runtime,
            _clock);

        Assert.Equal(state, restored.State);
        Assert.Equal(prepared.Session.Version, restored.Version);
        Assert.Equal(
            JsonSerializer.Serialize(prepared.Session.CreateSnapshot()),
            JsonSerializer.Serialize(restored.CreateSnapshot()));
        if (state == FocusSessionState.Focusing)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(prepared.Session.ActiveFocusElapsed, restored.ActiveFocusElapsed);
        }
    }

    [Fact]
    public async Task Stale_evaluation_is_retained_without_business_state_mutation()
    {
        var prepared = await PrepareSessionAsync("Stale");
        var initial = prepared.Session.CreateSnapshot();
        await _repository.StartSessionAsync(prepared.Setup.Id, prepared.Setup.Version, initial);
        prepared.Session.ReportMonitoringUnavailable();
        var current = prepared.Session.CreateSnapshot();
        await _repository.UpdateSessionAsync(
            prepared.Session.Id,
            new RuntimeMutation(initial.Version, current, []));
        var staleProposal = initial with { Version = initial.Version + 1 };
        var evaluation = new ReasoningEvaluationWrite(
            Guid.NewGuid(),
            prepared.Session.Id,
            initial.Version,
            ReasoningDecision.ContinueObserving,
            _clock.UtcNow,
            1,
            "{}");

        var result = await _repository.CommitReasoningEvaluationAsync(
            new ReasoningCommitRequest(
                initial.Version,
                staleProposal,
                evaluation,
                null,
                null,
                []));

        Assert.False(result.Applied);
        Assert.Equal("stale_session_version", result.RejectionReason);
        var after = await _repository.GetSessionAsync(prepared.Session.Id);
        Assert.Equal(
            JsonSerializer.Serialize(current),
            JsonSerializer.Serialize(after!.Runtime));
        var retained = Assert.Single(
            await _repository.GetRecentReasoningEvaluationsAsync(prepared.Session.Id, 10));
        Assert.False(retained.Accepted);
        Assert.Equal("stale_session_version", retained.RejectionReason);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.EvidenceEpisodes.ToListAsync());
        Assert.Empty(await db.Interventions.ToListAsync());
    }

    [Fact]
    public async Task Storage_conflict_rolls_back_episode_intervention_and_runtime()
    {
        var first = await PrepareSessionAsync("First");
        await _repository.StartSessionAsync(
            first.Setup.Id,
            first.Setup.Version,
            first.Session.CreateSnapshot());
        var snapshot = await _repository.AddSnapshotAsync(new SnapshotWrite(
            Guid.NewGuid(),
            first.Session.Id,
            1,
            _clock.UtcNow,
            _clock.MonotonicNow,
            "first.jpg",
            10,
            SnapshotProcessingStatus.Captured,
            first.Session.Version));
        var observation = await _repository.AddObservationAsync(new ObservationWrite(
            Guid.NewGuid(),
            first.Session.Id,
            snapshot.Id,
            first.Session.Version,
            _clock.UtcNow,
            1,
            "{}"));
        first.Session.EndEarlyByUser();
        await _repository.UpdateSessionAsync(
            first.Session.Id,
            new RuntimeMutation(1, first.Session.CreateSnapshot(), []));

        var second = await PrepareSessionAsync("Second");
        var secondInitial = second.Session.CreateSnapshot();
        await _repository.StartSessionAsync(
            second.Setup.Id,
            second.Setup.Version,
            secondInitial);
        second.Session.AdmitIntervention(
            InterventionEvaluation(second.Session, second.Deviation));
        var evaluationId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var interventionId = Guid.NewGuid();
        var evaluation = new ReasoningEvaluationWrite(
            evaluationId,
            second.Session.Id,
            secondInitial.Version,
            ReasoningDecision.BeginRecoveryCheckIn,
            _clock.UtcNow,
            1,
            "{}");
        var episode = new EvidenceEpisodeWrite(
            episodeId,
            second.Session.Id,
            second.Deviation.Id,
            null,
            _clock.UtcNow,
            "{}",
            [new(observation.Id, 0)]);
        var intervention = new InterventionWrite(
            interventionId,
            second.Session.Id,
            evaluationId,
            episodeId,
            _clock.UtcNow,
            TimeSpan.Zero,
            "Active");

        var result = await _repository.CommitReasoningEvaluationAsync(
            new ReasoningCommitRequest(
                secondInitial.Version,
                second.Session.CreateSnapshot(),
                evaluation,
                episode,
                intervention,
                []));

        Assert.False(result.Applied);
        Assert.Equal("storage_conflict", result.RejectionReason);
        Assert.Equal(
            secondInitial.Version,
            (await _repository.GetSessionAsync(second.Session.Id))!.Version);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.EvidenceEpisodes.AnyAsync(x => x.Id == episodeId));
        Assert.False(await db.Interventions.AnyAsync(x => x.Id == interventionId));
        var rejected = await db.ReasoningEvaluations.SingleAsync(x => x.Id == evaluationId);
        Assert.False(rejected.Accepted);
    }

    [Fact]
    public async Task Database_and_repository_enforce_one_active_session_across_goals()
    {
        var first = await PrepareSessionAsync("First active");
        var second = await PrepareSessionAsync("Second active");
        await _repository.StartSessionAsync(
            first.Setup.Id,
            first.Setup.Version,
            first.Session.CreateSnapshot());
        var secondRepository = new EfGoalKeeperRepository(_factory, _artifacts);

        await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            secondRepository.StartSessionAsync(
                second.Setup.Id,
                second.Setup.Version,
                second.Session.CreateSnapshot()));

        var storedSetup = await _repository.GetSetupAsync(second.Setup.Id);
        Assert.Equal(SessionSetupStatus.Ready, storedSetup!.Status);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.FocusSessions.Where(x => x.ActiveSlot != null).ToListAsync());
    }

    [Fact]
    public async Task Session_deletion_preserves_goal_and_removes_dependents_and_owned_artifacts()
    {
        var prepared = await PrepareSessionAsync("Delete session");
        var path = _artifacts.Claim(prepared.Session.Id);
        await _repository.StartSessionAsync(
            prepared.Setup.Id,
            prepared.Setup.Version,
            prepared.Session.CreateSnapshot(),
            path);
        var snapshot = await _repository.AddSnapshotAsync(new SnapshotWrite(
            Guid.NewGuid(),
            prepared.Session.Id,
            1,
            _clock.UtcNow,
            _clock.MonotonicNow,
            "one.jpg",
            42,
            SnapshotProcessingStatus.Captured,
            prepared.Session.Version));
        await _repository.AddObservationAsync(new ObservationWrite(
            Guid.NewGuid(),
            prepared.Session.Id,
            snapshot.Id,
            prepared.Session.Version,
            _clock.UtcNow,
            1,
            "{}"));
        prepared.Session.EndEarlyByUser();
        await _repository.UpdateSessionAsync(
            prepared.Session.Id,
            new RuntimeMutation(1, prepared.Session.CreateSnapshot(), []));

        await _repository.DeleteSessionAsync(prepared.Session.Id);

        Assert.NotNull(await _repository.GetGoalAsync(prepared.Goal.Id));
        Assert.False(Directory.Exists(path));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.FocusSessions.AnyAsync(x => x.Id == prepared.Session.Id));
        Assert.False(await db.Snapshots.AnyAsync(x => x.SessionId == prepared.Session.Id));
        Assert.False(await db.Observations.AnyAsync(x => x.SessionId == prepared.Session.Id));
    }

    [Fact]
    public async Task Overrides_reviews_and_goal_completion_are_saved_with_runtime()
    {
        var prepared = await PrepareSessionAsync("Review");
        await _repository.StartSessionAsync(
            prepared.Setup.Id,
            prepared.Setup.Version,
            prepared.Session.CreateSnapshot());
        prepared.Session.AdmitIntervention(
            InterventionEvaluation(prepared.Session, prepared.Deviation));
        prepared.Session.ApplyRemainderDeviationOverride("Goal-consistent research.");
        prepared.Session.EndEarlyByUser();
        await _repository.UpdateSessionAsync(
            prepared.Session.Id,
            new RuntimeMutation(1, prepared.Session.CreateSnapshot(), []));
        var terminalVersion = prepared.Session.Version;
        prepared.Session.SubmitReview(
            true,
            InterventionHelpfulness.Helpful,
            "Made progress.",
            true);

        await _repository.UpdateSessionAsync(
            prepared.Session.Id,
            new RuntimeMutation(
                terminalVersion,
                prepared.Session.CreateSnapshot(),
                []));

        var goal = await _repository.GetGoalAsync(prepared.Goal.Id);
        Assert.Equal(GoalStatus.Completed, goal!.Status);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.DeviationOverrides.Where(
            x => x.SessionId == prepared.Session.Id).ToListAsync());
        Assert.NotNull(await db.SessionReviews.SingleOrDefaultAsync(
            x => x.SessionId == prepared.Session.Id));
    }

    [Fact]
    public async Task Recent_observations_are_bounded_session_scoped_and_chronological()
    {
        var prepared = await PrepareSessionAsync("Recent");
        await _repository.StartSessionAsync(
            prepared.Setup.Id,
            prepared.Setup.Version,
            prepared.Session.CreateSnapshot());
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var capturedAt = _clock.UtcNow + TimeSpan.FromSeconds(sequence);
            var snapshot = await _repository.AddSnapshotAsync(new SnapshotWrite(
                Guid.NewGuid(),
                prepared.Session.Id,
                sequence,
                capturedAt,
                TimeSpan.FromSeconds(sequence),
                $"{sequence}.jpg",
                sequence,
                SnapshotProcessingStatus.Captured,
                prepared.Session.Version));
            await _repository.AddObservationAsync(new ObservationWrite(
                Guid.NewGuid(),
                prepared.Session.Id,
                snapshot.Id,
                prepared.Session.Version,
                capturedAt + TimeSpan.FromMilliseconds(1),
                1,
                "{}"));
        }

        var recent = await _repository.GetRecentObservationsAsync(prepared.Session.Id, 2);

        Assert.Equal(2, recent.Count);
        Assert.Equal([2d, 3d], recent.Select(x => x.CapturedAtMonotonic.TotalSeconds));
        Assert.All(recent, x => Assert.Equal(prepared.Session.Id, x.SessionId));
    }

    [Fact]
    public async Task Existing_database_upgrades_forward_without_reset_or_data_loss()
    {
        var upgradeRoot = Path.Combine(Path.GetTempPath(), $"goalkeeper-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(upgradeRoot);
        var path = Path.Combine(upgradeRoot, "goalkeeper.db");
        var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        var factory = new GoalKeeperDbContextFactory(options);
        var goalId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        try
        {
            await using (var initial = await factory.CreateDbContextAsync())
            {
                await initial.Database.MigrateAsync("20260717040651_InitialPhaseTwo");
            }

            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO Goals
                        (Id, Title, Description, Status, CreatedAtUtc, CompletedAtUtc, Version)
                    VALUES ($goal, 'Preserved', NULL, 'Active', $now, NULL, 1);
                    INSERT INTO SessionContracts
                        (Id, GoalId, GoalTitle, GoalDescription, TargetFocusTicks,
                         DeviationProfileId, DeviationProfileName, ReasoningMode,
                         Sensitivity, ConfirmedAtUtc)
                    VALUES ($contract, $goal, 'Preserved', NULL, $target,
                            $profile, 'Default', 'ProfileOnly', 'Balanced', $now);
                    INSERT INTO FocusSessions
                        (Id, GoalId, ContractId, State, Version, StartedAtUtc,
                         EndedAtUtc, AccumulatedFocusTicks, EndReason, ArtifactDirectory)
                    VALUES ($session, $goal, $contract, 'EndedEarly', 2, $now,
                            $now, 5, 'UserRequest', NULL);
                    INSERT INTO Snapshots
                        (Id, SessionId, Sequence, CapturedAtUtc, ImagePath,
                         StoredBytes, ProcessingStatus, SessionVersion)
                    VALUES ($snapshot, $session, 1, $now, 'one.jpg', 12, 'Captured', 1);
                    """;
                command.Parameters.AddWithValue("$goal", goalId);
                command.Parameters.AddWithValue("$contract", contractId);
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$snapshot", snapshotId);
                command.Parameters.AddWithValue("$profile", Guid.NewGuid());
                command.Parameters.AddWithValue("$target", TimeSpan.FromMinutes(25).Ticks);
                command.Parameters.AddWithValue("$now", _clock.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var repository = new EfGoalKeeperRepository(factory);
            await repository.InitializeAsync();

            Assert.Equal("Preserved", (await repository.GetGoalAsync(goalId))!.Title);
            var session = await repository.GetSessionAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal(FocusSessionState.EndedEarly, session.State);
            Assert.Equal(12, (await repository.GetStorageUsageAsync(sessionId)).SnapshotBytes);
        }
        finally
        {
            if (Directory.Exists(upgradeRoot))
            {
                Directory.Delete(upgradeRoot, recursive: true);
            }
        }
    }

    private async Task<PreparedSession> PrepareSessionAsync(string title)
    {
        var goalView = await _workflow.CreateGoalAsync(title, null);
        var draft = await _workflow.PrepareAsync(goalView.Id);
        draft = draft with
        {
            ScheduledBreaks =
            [
                new ScheduledBreakInput(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1))
            ]
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
            setup.Contract.ScheduledBreaks.Select(x =>
                ScheduledBreak.Create(x.ActiveFocusOffset, x.Duration)),
            new DeviationProfileSnapshot(
                setup.Contract.DeviationProfileId,
                setup.Contract.DeviationProfileName,
                setup.Contract.Deviations.Select(x =>
                    new DeviationSnapshot(x.Id, x.Description, x.Observability)).ToArray()),
            setup.Contract.ReasoningMode,
            setup.Contract.Sensitivity,
            setup.Contract.ConfirmedAtUtc);
        var deviation = Deviation.Rehydrate(
            setup.Contract.Deviations[0].Id,
            setup.Contract.Deviations[0].Description,
            setup.Contract.Deviations[0].Observability);
        return new(
            goal,
            contract,
            FocusSession.Start(goal, contract, true, _clock),
            deviation,
            setup);
    }

    private void MoveTo(
        FocusSessionState state,
        FocusSession session,
        Deviation deviation)
    {
        switch (state)
        {
            case FocusSessionState.Focusing:
                return;
            case FocusSessionState.ScheduledBreak:
                _clock.Advance(TimeSpan.FromMinutes(1));
                session.Advance();
                return;
            case FocusSessionState.RecoveryCheckIn:
                session.AdmitIntervention(InterventionEvaluation(session, deviation));
                return;
            case FocusSessionState.RecoveryWindow:
                session.AdmitIntervention(InterventionEvaluation(session, deviation));
                session.Recommit();
                return;
            case FocusSessionState.AwaitingResponse:
                session.AdmitIntervention(InterventionEvaluation(session, deviation));
                session.ReportNoResponse();
                return;
            case FocusSessionState.MonitoringUnavailable:
                session.ReportMonitoringUnavailable();
                return;
            case FocusSessionState.Fulfilled:
                session.CompleteGoal();
                return;
            case FocusSessionState.EndedEarly:
                session.EndEarlyByUser();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private ReasoningEvaluation InterventionEvaluation(
        FocusSession session,
        Deviation deviation)
    {
        var observation = ObservationReference.Create(
            $"obs-{Guid.NewGuid():N}",
            session.Id,
            _clock.MonotonicNow);
        var episode = EvidenceEpisode.Create(
            session.Id,
            DeviationReference.Listed(deviation.Id),
            [observation]);
        return ReasoningEvaluation.ProposeIntervention(
            session.Id,
            session.Version,
            episode,
            "Visible evidence may conflict.",
            _clock);
    }

    private sealed record PreparedSession(
        Goal Goal,
        SessionContract Contract,
        FocusSession Session,
        Deviation Deviation,
        SessionSetupView Setup);

    private sealed class RuntimeClock : IClock
    {
        public TimeSpan MonotonicNow { get; private set; }

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan duration)
        {
            MonotonicNow += duration;
            UtcNow += duration;
        }
    }
}
