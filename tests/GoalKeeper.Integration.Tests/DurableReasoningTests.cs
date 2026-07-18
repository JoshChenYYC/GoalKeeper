using System.Text.Json;
using GoalKeeper.Application;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Integration.Tests;

public sealed class DurableReasoningTests : IAsyncLifetime
{
    private readonly ReasoningClock _clock = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"goalkeeper-reasoning-{Guid.NewGuid():N}");
    private GoalKeeperDbContextFactory _factory = null!;
    private EfGoalKeeperRepository _repository = null!;
    private SetupWorkflow _workflow = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dataRoot, "goalkeeper.db")};Pooling=False")
            .Options;
        _factory = new(options);
        _repository = new(_factory, new SessionArtifactStore(_dataRoot));
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

    [Fact]
    public async Task Listed_intervention_is_reconstructable_and_does_not_admit_or_pause_directly()
    {
        var prepared = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var observation = await AddObservationAsync(prepared.Session.Id, prepared.Session.Version, 1);
        var fake = new DeterministicReasoningFake(
            [ReasoningFakeStep.ListedIntervention(prepared.Setup.Contract.Deviations[0].Id)]);
        var service = Service(fake);

        var result = await service.EvaluateAsync(Input(prepared, observation));

        Assert.True(result.Accepted);
        Assert.NotNull(result.EvidenceEpisode);
        var storedSession = await _repository.GetSessionAsync(prepared.Session.Id);
        Assert.Equal(FocusSessionState.Focusing, storedSession!.State);
        Assert.Null(storedSession.Runtime.ActiveIntervention);
        Assert.Null(storedSession.Runtime.Timer.PendingDispute);
        await using var db = await _factory.CreateDbContextAsync();
        var episode = await db.EvidenceEpisodes
            .Include(value => value.ObservationReferences)
            .SingleAsync();
        Assert.Equal([observation.Id],
            episode.ObservationReferences.OrderBy(value => value.Sequence)
                .Select(value => value.ObservationId));
        Assert.Empty(await db.Interventions.ToListAsync());
        var evaluation = await db.ReasoningEvaluations.SingleAsync();
        Assert.True(evaluation.Accepted);
    }

    [Fact]
    public async Task Exploratory_mode_accepts_grounded_unlisted_evidence()
    {
        var prepared = await StartSessionAsync(ReasoningMode.Exploratory);
        var observation = await AddObservationAsync(prepared.Session.Id, prepared.Session.Version, 1);

        var result = await Service(new DeterministicReasoningFake(
                [ReasoningFakeStep.ExploratoryIntervention("Sustained unrelated room activity")]))
            .EvaluateAsync(Input(prepared, observation));

        Assert.True(result.Accepted);
        Assert.True(result.EvidenceEpisode!.Deviation.IsUnlisted);
        Assert.Equal(
            "Sustained unrelated room activity",
            result.EvidenceEpisode.Deviation.UnlistedDescription);
    }

    [Fact]
    public async Task Profile_only_unlisted_stale_missing_and_thrown_results_are_recorded_without_mutation()
    {
        var prepared = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var observation = await AddObservationAsync(prepared.Session.Id, prepared.Session.Version, 1);
        var initialJson = JsonSerializer.Serialize(
            (await _repository.GetSessionAsync(prepared.Session.Id))!.Runtime);
        ReasoningFakeStep[] steps =
        [
            ReasoningFakeStep.ExploratoryIntervention("Unlisted"),
            ReasoningFakeStep.StaleResult(),
            ReasoningFakeStep.InvalidReferences(Guid.NewGuid()),
            ReasoningFakeStep.Throw(new InvalidOperationException("scripted failure"))
        ];

        foreach (var step in steps)
        {
            var result = await Service(new DeterministicReasoningFake([step]))
                .EvaluateAsync(Input(prepared, observation));
            Assert.False(result.Accepted);
        }

        var evaluations = await _repository.GetRecentReasoningEvaluationsAsync(
            prepared.Session.Id,
            10);
        Assert.Equal(4, evaluations.Count);
        Assert.All(evaluations, value => Assert.False(value.Accepted));
        Assert.Contains(evaluations,
            value => value.RejectionReason == "profile_only_unlisted_deviation");
        Assert.Contains(evaluations,
            value => value.RejectionReason == "stale_or_mismatched_result");
        Assert.Contains(evaluations,
            value => value.RejectionReason == "unlisted_observation_reference");
        Assert.Contains(evaluations,
            value => value.RejectionReason == "technical_failure");
        Assert.Equal(
            initialJson,
            JsonSerializer.Serialize(
                (await _repository.GetSessionAsync(prepared.Session.Id))!.Runtime));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.EvidenceEpisodes.ToListAsync());
        Assert.Empty(await db.Interventions.ToListAsync());
    }

    [Fact]
    public async Task Cross_session_and_reordered_references_are_rejected_and_recorded()
    {
        var prior = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var priorObservation = await AddObservationAsync(prior.Session.Id, prior.Session.Version, 1);
        prior.Session.EndEarlyByUser();
        await _repository.UpdateSessionAsync(
            prior.Session.Id,
            new(1, prior.Session.CreateSnapshot(), []));

        var current = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var first = await AddObservationAsync(current.Session.Id, current.Session.Version, 1);
        var latest = await AddObservationAsync(current.Session.Id, current.Session.Version, 2);
        var deviationId = current.Setup.Contract.Deviations[0].Id;
        var crossSession = Port(request => Success(
            request,
            deviationId,
            priorObservation.Id,
            latest.Id,
            [latest.Id],
            []));
        var reordered = Port(request => Success(
            request,
            deviationId,
            first.Id,
            latest.Id,
            [latest.Id, first.Id],
            []));

        var crossResult = await Service(crossSession).EvaluateAsync(Input(current, latest));
        var reorderedResult = await Service(reordered).EvaluateAsync(Input(current, latest));

        Assert.Equal("unlisted_observation_reference", crossResult.RejectionReason);
        Assert.Equal("reordered_references", reorderedResult.RejectionReason);
        var records = await _repository.GetRecentReasoningEvaluationsAsync(current.Session.Id, 10);
        Assert.Equal(2, records.Count);
        Assert.All(records, value => Assert.False(value.Accepted));
    }

    [Fact]
    public async Task Superseded_result_is_append_only_and_preserves_the_newer_runtime()
    {
        var prepared = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var observation = await AddObservationAsync(prepared.Session.Id, prepared.Session.Version, 1);
        var port = Port(async request =>
        {
            var runtime = (await _repository.GetSessionAsync(request.SessionId))!.Runtime;
            await _repository.UpdateSessionAsync(
                request.SessionId,
                new(runtime.Version, runtime with { Version = runtime.Version + 1 }, []));
            return Continue(request);
        });

        var result = await Service(port).EvaluateAsync(Input(prepared, observation));

        Assert.False(result.Accepted);
        Assert.Equal("superseded_session", result.RejectionReason);
        Assert.Equal(2, (await _repository.GetSessionAsync(prepared.Session.Id))!.Version);
        var record = Assert.Single(
            await _repository.GetRecentReasoningEvaluationsAsync(prepared.Session.Id, 10));
        Assert.False(record.Accepted);
    }

    [Fact]
    public async Task Long_session_requests_keep_observations_and_serialized_size_bounded()
    {
        var prepared = await StartSessionAsync(ReasoningMode.ProfileOnly);
        ObservationView latest = null!;
        for (var sequence = 1; sequence <= 40; sequence++)
        {
            latest = await AddObservationAsync(
                prepared.Session.Id,
                prepared.Session.Version,
                sequence);
        }

        ReasoningRequest? firstRequest = null;
        var memoryPort = Port(request =>
        {
            firstRequest = request;
            var reference = new ReasoningEvidenceReference(
                request.NewObservation.Id,
                request.NewObservation.CapturedAtMonotonic);
            return new ReasoningSuccess(
                new(
                    request.SessionId,
                    request.SessionVersion,
                    request.CurrentState,
                    request.NewObservation.Id,
                    ReasoningDecision.ContinueObserving,
                    null,
                    [
                        new(
                            "phone-episode",
                            ReasoningEpisodeStatus.Active,
                            prepared.Setup.Contract.Deviations[0].Id,
                            null,
                            reference,
                            reference,
                            [reference],
                            [],
                            "A compact active episode summary.")
                    ]),
                Metadata());
        });
        var first = await Service(memoryPort).EvaluateAsync(Input(prepared, latest));
        Assert.True(first.Accepted);
        Assert.NotNull(firstRequest);
        var firstSize = JsonSerializer.SerializeToUtf8Bytes(firstRequest).Length;
        Assert.Equal(ReasoningLimits.RecentObservations, firstRequest.RecentObservations.Count);
        Assert.True(firstSize <= ReasoningLimits.MaximumSerializedRequestBytes);

        var currentRuntime = (await _repository.GetSessionAsync(prepared.Session.Id))!.Runtime;
        for (var sequence = 41; sequence <= 100; sequence++)
        {
            latest = await AddObservationAsync(
                prepared.Session.Id,
                currentRuntime.Version,
                sequence);
        }

        var secondFake = new DeterministicReasoningFake([ReasoningFakeStep.Continue()]);
        var secondInput = new ReasoningEvaluationInput(
            prepared.Setup.Contract,
            currentRuntime,
            latest,
            []);
        var second = await Service(secondFake).EvaluateAsync(secondInput);
        Assert.True(second.Accepted);
        var secondRequest = Assert.Single(secondFake.Requests);
        var secondSize = JsonSerializer.SerializeToUtf8Bytes(secondRequest).Length;
        Assert.Equal(ReasoningLimits.RecentObservations, secondRequest.RecentObservations.Count);
        var compacted = Assert.Single(secondRequest.ActiveEpisodes);
        Assert.Equal("phone-episode", compacted.Key);
        Assert.DoesNotContain(
            secondRequest.RecentObservations,
            value => value.Id == compacted.FirstObservation.ObservationId);
        Assert.True(secondSize <= ReasoningLimits.MaximumSerializedRequestBytes);
        Assert.InRange(
            Math.Abs(secondSize - firstSize),
            0,
            ReasoningLimits.MaximumTextLength * 4);
    }

    [Fact]
    public async Task Public_rejection_boundary_retains_reason_without_runtime_mutation()
    {
        var prepared = await StartSessionAsync(ReasoningMode.ProfileOnly);
        var before = (await _repository.GetSessionAsync(prepared.Session.Id))!.Runtime;
        var evaluation = new ReasoningEvaluationWrite(
            Guid.NewGuid(),
            prepared.Session.Id,
            before.Version,
            ReasoningDecision.ContinueObserving,
            _clock.UtcNow,
            ReasoningSchemaVersions.V1,
            """{"outcome":"rejected"}""");

        var result = await _repository.AppendRejectedReasoningEvaluationAsync(
            evaluation,
            "validation_reason");

        Assert.False(result.Applied);
        Assert.Equal("validation_reason", result.RejectionReason);
        Assert.Equal(
            JsonSerializer.Serialize(before),
            JsonSerializer.Serialize(
                (await _repository.GetSessionAsync(prepared.Session.Id))!.Runtime));
        var stored = Assert.Single(
            await _repository.GetRecentReasoningEvaluationsAsync(prepared.Session.Id, 10));
        Assert.Equal("validation_reason", stored.RejectionReason);
        await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            _repository.AppendRejectedReasoningEvaluationAsync(
                evaluation with { Id = Guid.NewGuid() },
                " "));
    }

    private DurableReasoningOrchestrator Service(IReasoningPort port) =>
        new(_repository, port, _clock, TimeSpan.FromMinutes(5));

    private static ReasoningEvaluationInput Input(
        PreparedSession prepared,
        ObservationView observation) =>
        new(
            prepared.Setup.Contract,
            prepared.Session.CreateSnapshot(),
            observation,
            []);

    private async Task<PreparedSession> StartSessionAsync(ReasoningMode mode)
    {
        var goalView = await _workflow.CreateGoalAsync($"Goal {Guid.NewGuid():N}", null);
        var draft = await _workflow.PrepareAsync(goalView.Id);
        draft = draft with { ReasoningMode = mode };
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
            new(
                setup.Contract.GoalId,
                setup.Contract.GoalTitle,
                setup.Contract.GoalDescription),
            setup.Contract.TargetFocusDuration,
            setup.Contract.ScheduledBreaks.Select(value =>
                ScheduledBreak.Create(value.ActiveFocusOffset, value.Duration)),
            new(
                setup.Contract.DeviationProfileId,
                setup.Contract.DeviationProfileName,
                setup.Contract.Deviations.Select(value =>
                    new DeviationSnapshot(value.Id, value.Description, value.Observability)).ToArray()),
            setup.Contract.ReasoningMode,
            setup.Contract.Sensitivity,
            setup.Contract.ConfirmedAtUtc);
        var session = FocusSession.Start(goal, contract, true, _clock);
        await _repository.StartSessionAsync(setup.Id, setup.Version, session.CreateSnapshot());
        return new(goal, contract, session, setup);
    }

    private async Task<ObservationView> AddObservationAsync(
        Guid sessionId,
        long sessionVersion,
        int sequence)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = await _repository.AddSnapshotAsync(new(
            Guid.NewGuid(),
            sessionId,
            sequence,
            _clock.UtcNow,
            _clock.MonotonicNow,
            $"{sequence}.jpg",
            10,
            SnapshotProcessingStatus.Captured,
            sessionVersion));
        return await _repository.AddObservationAsync(new(
            Guid.NewGuid(),
            sessionId,
            snapshot.Id,
            sessionVersion,
            _clock.UtcNow,
            ObservationSchemaVersions.V1,
            JsonSerializer.Serialize(Observation())));
    }

    private static Observation Observation() =>
        new(
            ObservationSchemaVersions.V1,
            new(ImageQualityValue.Adequate, []),
            new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
            ["phone", "laptop"],
            [
                new(
                    VisibleCueSubject.VisiblePerson,
                    VisibleCueKind.Gaze,
                    VisibleCueState.Observed,
                    VisualSupport.Partial,
                    "The visible person is looking toward the phone.",
                    "Head orientation and phone position align.",
                    [])
            ]);

    private static CallbackReasoningPort Port(
        Func<ReasoningRequest, ReasoningResult> evaluate) =>
        new CallbackReasoningPort(request => Task.FromResult(evaluate(request)));

    private static CallbackReasoningPort Port(
        Func<ReasoningRequest, Task<ReasoningResult>> evaluate) =>
        new CallbackReasoningPort(evaluate);

    private static ReasoningSuccess Continue(ReasoningRequest request) =>
        new(
            new(
                request.SessionId,
                request.SessionVersion,
                request.CurrentState,
                request.NewObservation.Id,
                ReasoningDecision.ContinueObserving,
                null,
                []),
            Metadata());

    private static ReasoningSuccess Success(
        ReasoningRequest request,
        Guid deviationId,
        Guid first,
        Guid latest,
        IReadOnlyList<Guid> keys,
        IReadOnlyList<Guid> contradictions) =>
        new(
            new(
                request.SessionId,
                request.SessionVersion,
                request.CurrentState,
                request.NewObservation.Id,
                ReasoningDecision.BeginRecoveryCheckIn,
                new(
                    deviationId,
                    null,
                    first,
                    latest,
                    keys,
                    contradictions,
                    "Visible evidence may warrant a check-in."),
                []),
            Metadata());

    private static ReasoningMetadata Metadata() =>
        new(
            "test",
            "deterministic",
            "reasoning-v1",
            ReasoningSchemaVersions.V1,
            TimeSpan.Zero,
            Guid.NewGuid().ToString("D"));

    private sealed class CallbackReasoningPort(
        Func<ReasoningRequest, Task<ReasoningResult>> evaluate) : IReasoningPort
    {
        public Task<ReasoningResult> EvaluateAsync(
            ReasoningRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return evaluate(request);
        }
    }

    private sealed record PreparedSession(
        Goal Goal,
        SessionContract Contract,
        FocusSession Session,
        SessionSetupView Setup);

    private sealed class ReasoningClock : IClock
    {
        public TimeSpan MonotonicNow { get; private set; }

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan duration)
        {
            MonotonicNow += duration;
            UtcNow += duration;
        }
    }
}
