using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Integration.Tests;

public sealed class SessionRuntimeControllerTests
{
    [Fact]
    public async Task Preflight_retry_cancel_reject_confirm_and_single_worker_are_guarded()
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(LimitedObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Delayed(Success(AcceptableObservation()))
            ],
            [ReasoningFakeStep.Continue()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Controller.ConfirmAndStartAsync(true, Options()));

        var rejected = await harness.Controller.AcquirePreflightAsync(
            harness.Setup.Id,
            PreflightAcquisitionInput.Capture,
            CameraOptions());
        Assert.Equal(PreflightStatus.Rejected, rejected.Status);
        Assert.True(rejected.CanRetry);

        var retry = await harness.Controller.AcquirePreflightAsync(
            harness.Setup.Id,
            PreflightAcquisitionInput.Retry,
            CameraOptions());
        Assert.Equal(PreflightStatus.AwaitingConfirmation, retry.Status);
        Assert.Equal(
            PreflightStatus.Cancelled,
            (await harness.Controller.CancelPreflightAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Controller.ConfirmAndStartAsync(true, Options()));
        Assert.Equal(
            SessionSetupStatus.Cancelled,
            (await harness.Repository.GetSetupAsync(harness.Setup.Id))!.Status);
        var activeSetup = await harness.CreateSetupAsync(ReasoningMode.ProfileOnly);

        await harness.Controller.AcquirePreflightAsync(
            activeSetup.Id,
            PreflightAcquisitionInput.Capture,
            CameraOptions());
        var userRejected = await harness.Controller.ConfirmAndStartAsync(false, Options());
        Assert.Equal(PreflightRejection.UserRejected, userRejected.Rejection);
        Assert.Null(userRejected.Session);

        await harness.Controller.AcquirePreflightAsync(
            activeSetup.Id,
            PreflightAcquisitionInput.Retry,
            CameraOptions());
        var started = await harness.Controller.ConfirmAndStartAsync(true, Options());
        Assert.NotNull(started.Session);
        Assert.True((await harness.Controller.GetStatusAsync()).HasActiveWorker);

        var second = await harness.CreateSetupAsync(ReasoningMode.ProfileOnly);
        await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            harness.Controller.AcquirePreflightAsync(
                second.Id,
                PreflightAcquisitionInput.Capture,
                CameraOptions()));

        await harness.Controller.EndEarlyAsync();
        Assert.All(harness.Cameras.Cameras, camera => Assert.Equal(1, camera.ReleaseCount));
    }

    [Theory]
    [InlineData(ReasoningMode.ProfileOnly)]
    [InlineData(ReasoningMode.Exploratory)]
    public async Task Listed_and_exploratory_reasoning_are_admitted_atomically(
        ReasoningMode mode)
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            mode,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(BehaviorObservation()))
            ],
            [],
            reasoningFactory: setup => new DeterministicReasoningFake(
            [
                mode == ReasoningMode.ProfileOnly
                    ? ReasoningFakeStep.ListedIntervention(setup.Contract.Deviations[0].Id)
                    : ReasoningFakeStep.ExploratoryIntervention("Unlisted room activity")
            ]));

        await harness.StartAsync();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).State ==
            FocusSessionState.RecoveryCheckIn);

        var status = await harness.Controller.GetStatusAsync();
        var session = await harness.Repository.GetSessionAsync(status.SessionId!.Value);
        Assert.NotNull(session!.Runtime.ActiveIntervention);
        Assert.NotNull(session.Runtime.Timer.PendingDispute);

        await using var db = await harness.Factory.CreateDbContextAsync();
        Assert.True((await db.ReasoningEvaluations.SingleAsync()).Accepted);
        Assert.Single(await db.EvidenceEpisodes.ToListAsync());
        Assert.Single(await db.Interventions.ToListAsync());
    }

    [Fact]
    public async Task Technical_health_transitions_to_unavailable_and_restores_without_evidence()
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation()))
            ],
            [ReasoningFakeStep.Continue()]);
        await harness.StartAsync();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).Version == 2);

        using var schedulerCancellation = new CancellationTokenSource();
        var scheduler = new ManualScheduler();
        var schedulerTask = harness.Controller.RunSchedulerAsync(
            scheduler,
            schedulerCancellation.Token);
        var status = await harness.Controller.GetStatusAsync();
        harness.Controller.Report(Health(
            status.SessionId!.Value,
            MonitoringHealthEventKind.TechnicalGraceExpired,
            harness.Clock));
        scheduler.Tick();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).State ==
            FocusSessionState.MonitoringUnavailable);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        harness.Controller.Report(Health(
            status.SessionId.Value,
            MonitoringHealthEventKind.Recovered,
            harness.Clock));
        scheduler.Tick();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).State ==
            FocusSessionState.Focusing);

        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.EvidenceEpisodes.ToListAsync());
            Assert.Empty(await db.Interventions.ToListAsync());
        }

        schedulerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => schedulerTask);
    }

    [Fact]
    public async Task Typed_reasoning_failures_flow_through_grace_and_recovery_without_evidence()
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Delayed(Success(AcceptableObservation()))
            ],
            [
                ReasoningFakeStep.Return(ReasoningFailure(
                    ReasoningFailureCategory.ProviderUnavailable)),
                ReasoningFakeStep.Return(ReasoningFailure(
                    ReasoningFailureCategory.ProviderUnavailable)),
                ReasoningFakeStep.Continue()
            ]);
        await harness.StartAsync();
        await EventuallyAsync(() => Task.FromResult(harness.Perception.PendingDelayCount == 1));
        var status = await harness.Controller.GetStatusAsync();

        await harness.PublishObservationAsync(status.SessionId!.Value, status.Version!.Value, 20);
        var firstFailure = await harness.Controller.GetStatusAsync();
        Assert.Equal(FocusSessionState.Focusing, firstFailure.State);
        Assert.Equal("reasoning_providerunavailable", firstFailure.TechnicalFailure);

        harness.Clock.Advance(TimeSpan.FromSeconds(6));
        await harness.PublishObservationAsync(status.SessionId.Value, status.Version.Value, 21);
        await harness.PublishObservationAsync(status.SessionId.Value, status.Version.Value, 22);

        using var schedulerCancellation = new CancellationTokenSource();
        var scheduler = new ManualScheduler();
        var schedulerTask = harness.Controller.RunSchedulerAsync(
            scheduler,
            schedulerCancellation.Token);
        scheduler.Tick();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).Version == status.Version + 3);

        var recovered = await harness.Controller.GetStatusAsync();
        Assert.Equal(FocusSessionState.Focusing, recovered.State);
        Assert.Null(recovered.TechnicalFailure);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.EvidenceEpisodes.ToListAsync());
            Assert.Empty(await db.Interventions.ToListAsync());
        }

        schedulerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => schedulerTask);
    }

    [Fact]
    public async Task Terminal_command_cancels_worker_and_releases_camera_before_returning()
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Delayed(Success(AcceptableObservation()))
            ],
            [ReasoningFakeStep.Continue()]);
        await harness.StartAsync();
        await EventuallyAsync(() => Task.FromResult(harness.Perception.PendingDelayCount == 1));

        var ended = await harness.Controller.EndEarlyAsync();

        Assert.Equal(FocusSessionState.EndedEarly, ended!.State);
        Assert.Equal(0, harness.Perception.PendingDelayCount);
        Assert.Equal(1, harness.Cameras.Cameras[^1].ReleaseCount);
        var status = await harness.Controller.GetStatusAsync();
        Assert.False(status.HasActiveWorker);
        Assert.Equal(SessionRuntimeControllerState.Idle, status.ControllerState);
    }

    [Fact]
    public async Task Stale_reasoning_is_retained_and_does_not_admit_an_intervention()
    {
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(BehaviorObservation()))
            ],
            [ReasoningFakeStep.StaleResult()]);
        await harness.StartAsync();
        await EventuallyAsync(async () =>
            (await harness.Repository.GetRecentReasoningEvaluationsAsync(
                (await harness.Controller.GetStatusAsync()).SessionId!.Value,
                10)).Count == 1);

        var status = await harness.Controller.GetStatusAsync();
        var session = await harness.Repository.GetSessionAsync(status.SessionId!.Value);
        Assert.Equal(FocusSessionState.Focusing, session!.State);
        Assert.Null(session.Runtime.ActiveIntervention);
        var evaluation = Assert.Single(
            await harness.Repository.GetRecentReasoningEvaluationsAsync(session.Id, 10));
        Assert.False(evaluation.Accepted);
        Assert.Equal("stale_or_mismatched_result", evaluation.RejectionReason);
    }

    [Theory]
    [InlineData(RecoveryOutcome.BehaviorClarification, FocusSessionState.Focusing)]
    [InlineData(RecoveryOutcome.Recommit, FocusSessionState.RecoveryWindow)]
    [InlineData(RecoveryOutcome.NoResponse, FocusSessionState.AwaitingResponse)]
    public async Task Recovery_outcomes_commit_the_turn_and_runtime_atomically(
        RecoveryOutcome outcome,
        FocusSessionState expectedState)
    {
        var recovery = new ProposalRecoveryPort(outcome);
        await using var harness = await RuntimeHarness.CreateAsync(
            ReasoningMode.ProfileOnly,
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(BehaviorObservation()))
            ],
            [],
            reasoningFactory: setup => new DeterministicReasoningFake(
            [
                ReasoningFakeStep.ListedIntervention(setup.Contract.Deviations[0].Id)
            ]),
            recovery);
        await harness.StartAsync();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetStatusAsync()).State ==
            FocusSessionState.RecoveryCheckIn);

        var before = await harness.Controller.GetStatusAsync();
        var interventionId = (await harness.Repository.GetSessionAsync(
            before.SessionId!.Value))!.Runtime.ActiveIntervention!.Id;
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        var persisted = await harness.Controller.SubmitRecoveryAsync(
            outcome == RecoveryOutcome.NoResponse ? null : "I am working on the goal.");

        Assert.Equal(expectedState, persisted!.State);
        Assert.True(persisted.Version > before.Version);
        var turns = await harness.Repository.GetRecoveryTurnsAsync(
            persisted.Id,
            interventionId);
        var turn = Assert.Single(turns);
        Assert.Equal(1, turn.TurnNumber);
        Assert.Equal(outcome, RecoveryTurnPersistence.FromView(turn).Outcome);
        if (outcome == RecoveryOutcome.BehaviorClarification)
        {
            Assert.Null(persisted.Runtime.Timer.PendingDispute);
            Assert.NotNull(persisted.Runtime.Timer.RunningSince);
        }
        else if (outcome == RecoveryOutcome.Recommit)
        {
            Assert.Null(persisted.Runtime.Timer.PendingDispute);
            Assert.NotNull(persisted.Runtime.CurrentRecoveryWindow);
            Assert.True(persisted.Runtime.ProjectedEndUtc > before.ProjectedEndUtc);
        }
        else
        {
            Assert.NotNull(persisted.Runtime.ResponseDeadline);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            var timedOut = await harness.Controller.AdvanceAsync();
            Assert.Equal(FocusSessionState.EndedEarly, timedOut!.State);
            Assert.Equal(EndedEarlyReason.NoResponse, timedOut.Runtime.EndedEarlyReason);
        }
    }

    private static MonitoringHealthEvent Health(
        Guid sessionId,
        MonitoringHealthEventKind kind,
        TestClock clock) =>
        new(
            sessionId,
            kind,
            MonitoringTechnicalSource.Perception,
            clock.UtcNow,
            clock.MonotonicNow,
            clock.UtcNow,
            clock.MonotonicNow,
            3);

    private static CameraAcquisitionOptions CameraOptions() => new(0, 0, 85);

    private static MonitoringOptions Options() =>
        new(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            CameraOptions());

    private static PerceptionSuccess Success(Observation observation) =>
        new(
            observation,
            new(
                "deterministic-fake",
                "perception-v1",
                "perception-v1",
                ObservationSchemaVersions.V1,
                TimeSpan.Zero,
                Guid.NewGuid().ToString("N")));

    private static ReasoningFailure ReasoningFailure(
        ReasoningFailureCategory category) =>
        new(
            category,
            new(
                "deterministic-fake",
                "reasoning-v1",
                "reasoning-v1",
                ReasoningSchemaVersions.V1,
                TimeSpan.Zero,
                Guid.NewGuid().ToString("N")));

    private static Observation AcceptableObservation() =>
        new(
            ObservationSchemaVersions.V1,
            new(ImageQualityValue.Adequate, []),
            new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
            ["laptop"],
            []);

    private static Observation LimitedObservation() =>
        new(
            ObservationSchemaVersions.V1,
            new(ImageQualityValue.Limited, ["low light"]),
            new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
            ["laptop"],
            []);

    private static Observation BehaviorObservation() =>
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
                    "Looking toward a phone.",
                    "Head orientation aligns with the phone.",
                    [])
            ]);

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.Fail("The asynchronous condition was not reached within five seconds.");
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SetupWorkflow _workflow;

        private RuntimeHarness(
            string root,
            GoalKeeperDbContextFactory factory,
            EfGoalKeeperRepository repository,
            SetupWorkflow workflow,
            TestClock clock,
            RecordingCameraFactory cameras,
            DeterministicPerceptionFake perception,
            SessionRuntimeController controller,
            SessionSetupView setup)
        {
            _root = root;
            Factory = factory;
            Repository = repository;
            _workflow = workflow;
            Clock = clock;
            Cameras = cameras;
            Perception = perception;
            Controller = controller;
            Setup = setup;
        }

        public GoalKeeperDbContextFactory Factory { get; }

        public EfGoalKeeperRepository Repository { get; }

        public TestClock Clock { get; }

        public RecordingCameraFactory Cameras { get; }

        public DeterministicPerceptionFake Perception { get; }

        public SessionRuntimeController Controller { get; }

        public SessionSetupView Setup { get; }

        public static async Task<RuntimeHarness> CreateAsync(
            ReasoningMode mode,
            IReadOnlyList<PerceptionFakeStep> perceptionSteps,
            IReadOnlyList<ReasoningFakeStep> reasoningSteps,
            Func<SessionSetupView, IReasoningPort>? reasoningFactory = null,
            IRecoveryPort? recovery = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"goalkeeper-runtime-controller-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "goalkeeper.db")};Pooling=False")
                .Options;
            var factory = new GoalKeeperDbContextFactory(options);
            var artifacts = new SessionArtifactStore(root);
            var repository = new EfGoalKeeperRepository(factory, artifacts);
            var clock = new TestClock();
            var workflow = new SetupWorkflow(repository, clock);
            await repository.InitializeAsync();
            await workflow.SaveProfileAsync(
                "Default",
                [new("Phone", VisualObservability.Observable)]);
            var setup = await CreateSetupAsync(workflow, mode);
            var cameras = new RecordingCameraFactory(clock);
            var perception = new DeterministicPerceptionFake(perceptionSteps);
            var observationSink = new ForwardingObservationSink();
            var healthSink = new ForwardingHealthSink();
            var monitoring = new MonitoringPipeline(
                cameras,
                perception,
                repository,
                artifacts,
                new BlockingMonitoringDelay(),
                clock,
                observationSink,
                healthSink);
            var controller = new SessionRuntimeController(
                repository,
                new(new(cameras), perception),
                monitoring,
                new(
                    repository,
                    reasoningFactory?.Invoke(setup) ??
                    new DeterministicReasoningFake(reasoningSteps),
                    clock,
                    TimeSpan.FromMinutes(1)),
                recovery ?? new DeterministicTextRecoveryFake([]),
                clock);
            observationSink.Target = controller;
            healthSink.Target = controller;
            return new(
                root,
                factory,
                repository,
                workflow,
                clock,
                cameras,
                perception,
                controller,
                setup);
        }

        public Task<SessionSetupView> CreateSetupAsync(ReasoningMode mode) =>
            CreateSetupAsync(_workflow, mode);

        public async Task StartAsync()
        {
            await Controller.AcquirePreflightAsync(
                Setup.Id,
                PreflightAcquisitionInput.Capture,
                CameraOptions());
            var started = await Controller.ConfirmAndStartAsync(true, Options());
            Assert.NotNull(started.Session);
        }

        public async Task PublishObservationAsync(
            Guid sessionId,
            long sessionVersion,
            int sequence)
        {
            var observation = BehaviorObservation();
            var snapshot = await Repository.AddSnapshotAsync(new(
                Guid.NewGuid(),
                sessionId,
                sequence,
                Clock.UtcNow,
                Clock.MonotonicNow,
                $"{sequence}.jpg",
                10,
                SnapshotProcessingStatus.Captured,
                sessionVersion));
            var persisted = await Repository.AddObservationAsync(new(
                Guid.NewGuid(),
                sessionId,
                snapshot.Id,
                sessionVersion,
                Clock.UtcNow,
                ObservationSchemaVersions.V1,
                JsonSerializer.Serialize(observation)));
            await Controller.PublishAsync(new(persisted, observation));
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static async Task<SessionSetupView> CreateSetupAsync(
            SetupWorkflow workflow,
            ReasoningMode mode)
        {
            var goal = await workflow.CreateGoalAsync($"Goal {Guid.NewGuid():N}", null);
            var draft = await workflow.PrepareAsync(goal.Id);
            return await workflow.ConfirmAsync(draft with { ReasoningMode = mode });
        }
    }

    private sealed class ForwardingObservationSink : IMonitoringObservationSink
    {
        public SessionRuntimeController? Target { get; set; }

        public Task PublishAsync(
            ReasoningEligibleObservation observation,
            CancellationToken cancellationToken = default) =>
            Target!.PublishAsync(observation, cancellationToken);
    }

    private sealed class ForwardingHealthSink : IMonitoringHealthEventSink
    {
        public SessionRuntimeController? Target { get; set; }

        public void Report(MonitoringHealthEvent healthEvent) => Target!.Report(healthEvent);
    }

    private sealed class BlockingMonitoringDelay : IMonitoringDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            _ = delay;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RecordingCameraFactory(TestClock clock) : ICameraFactory
    {
        public List<RecordingCamera> Cameras { get; } = [];

        public ICamera Create()
        {
            var camera = new RecordingCamera(clock);
            Cameras.Add(camera);
            return camera;
        }
    }

    private sealed class RecordingCamera(TestClock clock) : ICamera
    {
        private bool _released;

        public CameraHealth Health { get; private set; }

        public int ReleaseCount { get; private set; }

        public ValueTask OpenAsync(
            int deviceIndex,
            CancellationToken cancellationToken = default)
        {
            _ = deviceIndex;
            cancellationToken.ThrowIfCancellationRequested();
            Health = CameraHealth.Open;
            return ValueTask.CompletedTask;
        }

        public ValueTask WarmUpAsync(
            int frameCount,
            CancellationToken cancellationToken = default)
        {
            _ = frameCount;
            cancellationToken.ThrowIfCancellationRequested();
            Health = CameraHealth.Ready;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICameraFrame> CaptureFrameAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICameraFrame>(
                new Frame(clock.UtcNow, clock.MonotonicNow));
        }

        public ValueTask<CapturedJpegFrame> EncodeJpegAsync(
            ICameraFrame frame,
            int quality,
            CancellationToken cancellationToken = default)
        {
            _ = quality;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CapturedJpegFrame(
                frame.Id,
                frame.CapturedAtUtc,
                frame.CapturedAtMonotonic,
                frame.PixelWidth,
                frame.PixelHeight,
                [0xff, 0xd8, 0x01, 0xff, 0xd9]));
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (!_released)
            {
                _released = true;
                ReleaseCount++;
            }

            Health = CameraHealth.Closed;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ReleaseAsync();
    }

    private sealed class Frame(
        DateTimeOffset capturedAtUtc,
        TimeSpan capturedAtMonotonic) : ICameraFrame
    {
        public Guid Id { get; } = Guid.NewGuid();

        public DateTimeOffset CapturedAtUtc { get; } = capturedAtUtc;

        public TimeSpan CapturedAtMonotonic { get; } = capturedAtMonotonic;

        public int PixelWidth => 640;

        public int PixelHeight => 480;

        public void Dispose()
        {
        }
    }

    public sealed class TestClock : IClock
    {
        private readonly object _sync = new();
        private DateTimeOffset _utc =
            new(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);
        private TimeSpan _monotonic;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utc;
                }
            }
        }

        public TimeSpan MonotonicNow
        {
            get
            {
                lock (_sync)
                {
                    return _monotonic;
                }
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_sync)
            {
                _utc += duration;
                _monotonic += duration;
            }
        }
    }

    private sealed class ManualScheduler : ISessionRuntimeScheduler
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();

        public void Tick() => _ticks.Writer.TryWrite(true);

        public async Task WaitForNextTickAsync(
            CancellationToken cancellationToken = default) =>
            _ = await _ticks.Reader.ReadAsync(cancellationToken);
    }

    private sealed class ProposalRecoveryPort(RecoveryOutcome outcome) : IRecoveryPort
    {
        public Task<RecoveryPortResult> ProposeAsync(
            RecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transcript = outcome == RecoveryOutcome.NoResponse
                ? null
                : request.CurrentTranscript;
            var clarification = outcome == RecoveryOutcome.BehaviorClarification
                ? "The visible behavior was part of the goal."
                : null;
            return Task.FromResult<RecoveryPortResult>(
                new RecoveryProposalResponse(
                    new(
                        request.SessionId,
                        request.SessionVersion,
                        request.Intervention.InterventionId,
                        request.NextTurnNumber,
                        outcome,
                        transcript,
                        clarification,
                        null,
                        false,
                        new(request.RequestedAtUtc, request.RequestedAtUtc),
                        new(
                            "deterministic-fake",
                            "recovery-v1",
                            "recovery-v1",
                            RecoverySchemaVersions.V1,
                            TimeSpan.Zero,
                            Guid.NewGuid().ToString("N")))));
        }
    }
}
