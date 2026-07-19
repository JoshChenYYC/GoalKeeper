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
using GoalKeeper.Web.Operations;
using GoalKeeper.Web.Presentation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class SessionRuntimePresentationTests
{
    [Fact]
    public async Task Timing_diagnostics_flow_from_preflight_through_session_start()
    {
        var providerLatency = TimeSpan.FromSeconds(1.25);
        await using var harness = await PresentationHarness.CreateAsync(
            PerceptionFakeStep.Return(Success(AcceptableObservation(), providerLatency)));

        var captured = await harness.Presentation.CaptureAsync(
            harness.Setup.Id,
            retry: false);

        Assert.NotNull(captured.Timing);
        Assert.Equal(providerLatency, captured.Timing.ProviderValidation);
        Assert.True(captured.Timing.Total >= captured.Timing.CameraAcquisition);

        var started = await harness.Presentation.ConfirmAndStartAsync(harness.Setup.Id);
        Assert.True(started.Started);
        Assert.True(started.Duration >= TimeSpan.Zero);

        var live = await harness.Presentation.GetLiveAsync(started.SessionId!.Value);
        Assert.NotNull(live);
        Assert.Equal(started.Duration, live.StartupDuration);
    }

    [Fact]
    public async Task Disabled_provider_explains_camera_only_preflight_before_and_after_capture()
    {
        await using var harness = await PresentationHarness.CreateAsync(
            firstStep: PerceptionFakeStep.Return(ProviderUnavailable()),
            providerMode: GoalKeeperProviderMode.Disabled);

        var initial = await harness.Presentation.GetPreflightAsync(harness.Setup.Id);

        Assert.True(initial.CanCapture);
        Assert.Contains("Hosted AI validation is disabled", initial.StatusMessage);
        Assert.Contains("cannot approve preflight", initial.StatusMessage);

        var captured = await harness.Presentation.CaptureAsync(
            harness.Setup.Id,
            retry: false);

        Assert.Equal(PreflightStatus.TechnicalFailure, captured.Status);
        Assert.NotNull(captured.PreviewDataUrl);
        Assert.True(captured.CanRetry);
        Assert.False(captured.CanConfirm);
        Assert.Contains("Image captured", captured.StatusMessage);
        Assert.Contains("enable Hosted mode", captured.StatusMessage);
        Assert.Contains("No behavioral judgment", captured.StatusMessage);
    }

    [Fact]
    public async Task Preflight_cannot_start_without_a_successful_candidate()
    {
        await using var harness = await PresentationHarness.CreateAsync(
            PerceptionFakeStep.Return(Success(LimitedObservation())));

        var withoutCapture = await harness.Presentation.ConfirmAndStartAsync(harness.Setup.Id);

        Assert.False(withoutCapture.Started);
        Assert.Null(withoutCapture.SessionId);
        Assert.Contains("Capture and validate", withoutCapture.Error);

        var rejected = await harness.Presentation.CaptureAsync(harness.Setup.Id, retry: false);
        Assert.Equal(PreflightStatus.Rejected, rejected.Status);
        Assert.False(rejected.CanConfirm);

        var afterRejectedCapture =
            await harness.Presentation.ConfirmAndStartAsync(harness.Setup.Id);
        Assert.False(afterRejectedCapture.Started);
        Assert.Null(afterRejectedCapture.SessionId);
        Assert.Empty(await harness.Repository.ListSessionHistoryAsync());
    }

    [Fact]
    public async Task Preflight_retry_can_recover_and_cancel_clears_confirmation_candidate()
    {
        await using var harness = await PresentationHarness.CreateAsync(
            preflightSteps:
            [
                PerceptionFakeStep.Return(Success(LimitedObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation()))
            ]);

        var rejected =
            await harness.Presentation.CaptureAsync(harness.Setup.Id, retry: false);
        var accepted =
            await harness.Presentation.CaptureAsync(harness.Setup.Id, retry: true);

        Assert.True(rejected.CanRetry);
        Assert.False(rejected.CanConfirm);
        Assert.True(accepted.CanConfirm);
        Assert.NotNull(accepted.PreviewDataUrl);

        await harness.Presentation.CancelPreflightAsync(harness.Setup.Id);
        var startAfterCancel =
            await harness.Presentation.ConfirmAndStartAsync(harness.Setup.Id);

        Assert.False(startAfterCancel.Started);
        Assert.Null(startAfterCancel.SessionId);
        Assert.Equal(
            SessionSetupStatus.Cancelled,
            (await harness.Repository.GetSetupAsync(harness.Setup.Id))!.Status);
        Assert.Empty(await harness.Repository.ListSessionHistoryAsync());
    }

    [Fact]
    public async Task Rejected_preflight_can_retry_then_cancel_without_starting()
    {
        await using var harness = await PresentationHarness.CreateAsync(
            preflightSteps:
            [
                PerceptionFakeStep.Return(Success(LimitedObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation()))
            ]);

        var rejected = await harness.Presentation.CaptureAsync(
            harness.Setup.Id,
            retry: false);
        Assert.Equal(PreflightStatus.Rejected, rejected.Status);
        Assert.True(rejected.CanRetry);
        Assert.False(rejected.CanConfirm);

        var retry = await harness.Presentation.CaptureAsync(
            harness.Setup.Id,
            retry: true);
        Assert.Equal(PreflightStatus.AwaitingConfirmation, retry.Status);
        Assert.False(retry.CanRetry);
        Assert.True(retry.CanConfirm);
        Assert.NotNull(retry.PreviewDataUrl);

        await harness.Presentation.CancelPreflightAsync(harness.Setup.Id);

        Assert.Equal(
            SessionSetupStatus.Cancelled,
            (await harness.Repository.GetSetupAsync(harness.Setup.Id))!.Status);
        Assert.Equal(
            SessionRuntimeControllerState.Idle,
            (await harness.Controller.GetStatusAsync()).ControllerState);
        Assert.Empty(await harness.Repository.ListSessionHistoryAsync());
    }

    [Fact]
    public async Task Parallel_preflight_circuits_can_confirm_only_the_authoritative_setup()
    {
        await using var harness = await PresentationHarness.CreateAsync(
            preflightSteps:
            [
                PerceptionFakeStep.Return(Success(AcceptableObservation())),
                PerceptionFakeStep.Return(Success(AcceptableObservation()))
            ]);
        var secondSetup = await harness.CreateSetupAsync();

        await Task.WhenAll(
            harness.Presentation.CaptureAsync(harness.Setup.Id, retry: false),
            harness.Presentation.CaptureAsync(secondSetup.Id, retry: false));
        var status = await harness.Controller.GetStatusAsync();
        Assert.Equal(SessionRuntimeControllerState.Preflight, status.ControllerState);
        Assert.NotNull(status.SetupId);
        var authoritativeSetupId = status.SetupId.Value;
        var staleSetupId = authoritativeSetupId == harness.Setup.Id
            ? secondSetup.Id
            : harness.Setup.Id;

        var staleAttempt =
            await harness.Presentation.ConfirmAndStartAsync(staleSetupId);
        var authoritativeAttempt =
            await harness.Presentation.ConfirmAndStartAsync(authoritativeSetupId);

        Assert.False(staleAttempt.Started);
        Assert.Null(staleAttempt.SessionId);
        Assert.True(authoritativeAttempt.Started);
        Assert.NotNull(authoritativeAttempt.SessionId);
        Assert.Single(await harness.Repository.ListSessionHistoryAsync());
    }

    [Fact]
    public async Task Mismatched_cancel_cannot_clear_another_setups_active_preflight()
    {
        await using var harness = await PresentationHarness.CreateAsync();
        var otherSetup = await harness.CreateSetupAsync();
        var active = await harness.Presentation.CaptureAsync(otherSetup.Id, retry: false);
        Assert.True(active.CanConfirm);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Presentation.CancelPreflightAsync(harness.Setup.Id));

        var status = await harness.Controller.GetStatusAsync();
        Assert.Equal(SessionRuntimeControllerState.Preflight, status.ControllerState);
        Assert.Equal(otherSetup.Id, status.SetupId);
        Assert.Equal(
            SessionSetupStatus.Ready,
            (await harness.Repository.GetSetupAsync(harness.Setup.Id))!.Status);
        Assert.Equal(
            SessionSetupStatus.Ready,
            (await harness.Repository.GetSetupAsync(otherSetup.Id))!.Status);

        var started = await harness.Presentation.ConfirmAndStartAsync(otherSetup.Id);
        Assert.True(started.Started);
    }

    [Fact]
    public async Task Live_status_uses_authoritative_clock_and_contract_for_focus_timing()
    {
        await using var harness = await PresentationHarness.CreateAsync();
        var sessionId = await harness.StartAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(17));

        var live = await harness.Controller.GetLiveStatusAsync(sessionId);

        Assert.NotNull(live);
        Assert.Equal(FocusSessionState.Focusing, live.State);
        Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(17), live.FocusElapsed);
        Assert.Equal(harness.Setup.Contract.TargetFocusDuration, live.FocusTarget);
        Assert.Equal(
            harness.Setup.Contract.TargetFocusDuration - live.FocusElapsed,
            live.FocusRemaining);
        Assert.Null(live.StateCountdown);
        Assert.True(live.MonitoringActive);
        Assert.True(live.CanCompleteGoal);
        Assert.True(live.CanEndEarly);
        Assert.False(live.CanSubmitRecovery);
        Assert.False(live.CanReturnToRecovery);
        Assert.False(live.IsTerminal);
    }

    [Fact]
    public async Task Scheduled_break_exposes_countdown_without_behavioral_action()
    {
        await using var harness = await PresentationHarness.CreateAsync(withScheduledBreak: true);
        var sessionId = await harness.StartAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        await harness.Controller.AdvanceAsync();
        var live = await harness.Controller.GetLiveStatusAsync(sessionId);

        Assert.NotNull(live);
        Assert.Equal(FocusSessionState.ScheduledBreak, live.State);
        Assert.Equal(TimeSpan.FromMinutes(2), live.FocusElapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), live.StateCountdown);
        Assert.Null(live.RecoveryPrompt);
        Assert.False(live.CanCompleteGoal);
        Assert.False(live.CanSubmitRecovery);
        Assert.False(live.CanReturnToRecovery);
        Assert.True(live.CanEndEarly);
    }

    [Fact]
    public async Task Monitoring_unavailable_exposes_technical_countdown_without_behavioral_action()
    {
        await using var harness = await PresentationHarness.CreateAsync();
        var sessionId = await harness.StartAsync();
        using var schedulerCancellation = new CancellationTokenSource();
        var scheduler = new ManualScheduler();
        var schedulerTask = harness.Controller.RunSchedulerAsync(
            scheduler,
            schedulerCancellation.Token);
        harness.Controller.Report(new MonitoringHealthEvent(
            sessionId,
            MonitoringHealthEventKind.TechnicalGraceExpired,
            MonitoringTechnicalSource.Camera,
            harness.Clock.UtcNow,
            harness.Clock.MonotonicNow,
            harness.Clock.UtcNow,
            harness.Clock.MonotonicNow,
            3));
        scheduler.Tick();
        await EventuallyAsync(async () =>
            (await harness.Controller.GetLiveStatusAsync(sessionId))?.State ==
            FocusSessionState.MonitoringUnavailable);

        var live = await harness.Controller.GetLiveStatusAsync(sessionId);

        Assert.NotNull(live);
        Assert.Equal(FocusSessionState.MonitoringUnavailable, live.State);
        Assert.NotNull(live.StateCountdown);
        Assert.Null(live.RecoveryPrompt);
        Assert.False(live.CanCompleteGoal);
        Assert.False(live.CanSubmitRecovery);
        Assert.False(live.CanReturnToRecovery);
        Assert.True(live.CanEndEarly);

        schedulerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => schedulerTask);
    }

    [Fact]
    public async Task Duplicate_terminal_attempts_are_idempotent_and_release_resources_once()
    {
        await using var harness = await PresentationHarness.CreateAsync();
        var sessionId = await harness.StartAsync();
        await EventuallyAsync(() => Task.FromResult(harness.Perception.PendingDelayCount == 1));

        var attempts = await Task.WhenAll(
            harness.Presentation.EndEarlyAsync(sessionId),
            harness.Presentation.EndEarlyAsync(sessionId));

        Assert.All(attempts, live =>
        {
            Assert.NotNull(live);
            Assert.Equal(FocusSessionState.EndedEarly, live.State);
            Assert.True(live.IsTerminal);
            Assert.False(live.CanEndEarly);
        });
        Assert.Equal(attempts[0]!.SessionId, attempts[1]!.SessionId);
        var persisted = await harness.Repository.GetSessionAsync(sessionId);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.Version);
        Assert.Equal(0, harness.Perception.PendingDelayCount);
        Assert.All(harness.Cameras.Cameras, camera => Assert.Equal(1, camera.ReleaseCount));

        var status = await harness.Controller.GetStatusAsync();
        Assert.Equal(SessionRuntimeControllerState.Idle, status.ControllerState);
        Assert.False(status.HasActiveWorker);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Presentation.EndEarlyAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Session_already_ended_outside_presentation_returns_terminal_state_without_mutation()
    {
        await using var harness = await PresentationHarness.CreateAsync();
        var sessionId = await harness.StartAsync();
        var ended = await harness.Controller.EndEarlyAsync();
        Assert.NotNull(ended);
        var terminalVersion = ended.Version;

        var repeated = await harness.Presentation.EndEarlyAsync(sessionId);

        Assert.NotNull(repeated);
        Assert.True(repeated.IsTerminal);
        Assert.Equal(FocusSessionState.EndedEarly, repeated.State);
        Assert.Equal(
            terminalVersion,
            (await harness.Repository.GetSessionAsync(sessionId))!.Version);
    }

    [Theory]
    [InlineData(RecoveryOutcome.Recommit)]
    [InlineData(RecoveryOutcome.NoResponse)]
    public async Task Recovery_states_expose_only_their_valid_actions_and_countdowns(
        RecoveryOutcome outcome)
    {
        await using var harness = await PresentationHarness.CreateAsync(
            recoveryOutcome: outcome);
        var sessionId = await harness.StartAsync();
        await harness.PublishBehaviorObservationAsync(sessionId);

        var checkIn = await harness.Controller.GetLiveStatusAsync(sessionId);

        Assert.NotNull(checkIn);
        Assert.Equal(FocusSessionState.RecoveryCheckIn, checkIn.State);
        Assert.True(checkIn.CanSubmitRecovery);
        Assert.False(checkIn.CanCompleteGoal);
        Assert.False(checkIn.CanReturnToRecovery);
        Assert.True(checkIn.CanEndEarly);
        Assert.Null(checkIn.StateCountdown);
        Assert.NotNull(checkIn.RecoveryPrompt);

        var next = outcome == RecoveryOutcome.NoResponse
            ? await SubmitNoResponseAsync(harness, sessionId)
            : await harness.Presentation.SubmitRecoveryAsync(
                sessionId,
                "I was checking information needed for the goal.");

        Assert.NotNull(next);
        Assert.False(next.CanSubmitRecovery);
        Assert.True(next.CanEndEarly);
        Assert.NotNull(next.StateCountdown);
        if (outcome == RecoveryOutcome.Recommit)
        {
            Assert.Equal(FocusSessionState.RecoveryWindow, next.State);
            Assert.True(next.CanCompleteGoal);
            Assert.False(next.CanReturnToRecovery);
            Assert.Equal(TimeSpan.FromMinutes(1), next.StateCountdown);
        }
        else
        {
            Assert.Equal(FocusSessionState.AwaitingResponse, next.State);
            Assert.False(next.CanCompleteGoal);
            Assert.True(next.CanReturnToRecovery);
            Assert.Equal(TimeSpan.FromMinutes(1), next.StateCountdown);
        }
    }

    private static async Task<LiveSessionPageView?> SubmitNoResponseAsync(
        PresentationHarness harness,
        Guid sessionId)
    {
        await harness.Controller.SubmitRecoveryAsync(transcript: null);
        return await harness.Presentation.GetLiveAsync(sessionId);
    }

    private static CameraAcquisitionOptions CameraOptions() => new(0, 0, 85);

    private static MonitoringOptions MonitoringOptions() =>
        new(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            CameraOptions());

    private static PerceptionSuccess Success(
        Observation observation,
        TimeSpan? latency = null) =>
        new(
            observation,
            new(
                "deterministic-fake",
                "perception-v1",
                "perception-v1",
                ObservationSchemaVersions.V1,
                latency ?? TimeSpan.Zero,
                Guid.NewGuid().ToString("N")));

    private static PerceptionFailure ProviderUnavailable() =>
        new(
            PerceptionFailureCategory.ProviderUnavailable,
            new(
                "unconfigured",
                "none",
                "perception-v1",
                ObservationSchemaVersions.V1,
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

    private sealed class PresentationHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SetupWorkflow _workflow;

        private PresentationHarness(
            string root,
            EfGoalKeeperRepository repository,
            SetupWorkflow workflow,
            TestClock clock,
            RecordingCameraFactory cameras,
            DeterministicPerceptionFake perception,
            SessionRuntimeController controller,
            SessionRuntimePresentation presentation,
            SessionSetupView setup)
        {
            _root = root;
            Repository = repository;
            _workflow = workflow;
            Clock = clock;
            Cameras = cameras;
            Perception = perception;
            Controller = controller;
            Presentation = presentation;
            Setup = setup;
        }

        public EfGoalKeeperRepository Repository { get; }

        public TestClock Clock { get; }

        public RecordingCameraFactory Cameras { get; }

        public DeterministicPerceptionFake Perception { get; }

        public SessionRuntimeController Controller { get; }

        public SessionRuntimePresentation Presentation { get; }

        public SessionSetupView Setup { get; }

        public static async Task<PresentationHarness> CreateAsync(
            PerceptionFakeStep? firstStep = null,
            bool withScheduledBreak = false,
            IReadOnlyList<PerceptionFakeStep>? preflightSteps = null,
            RecoveryOutcome? recoveryOutcome = null,
            GoalKeeperProviderMode providerMode = GoalKeeperProviderMode.Hosted)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"goalkeeper-runtime-presentation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databaseOptions = new DbContextOptionsBuilder<GoalKeeperDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "goalkeeper.db")};Pooling=False")
                .Options;
            var factory = new GoalKeeperDbContextFactory(databaseOptions);
            var artifacts = new SessionArtifactStore(root);
            var repository = new EfGoalKeeperRepository(factory, artifacts);
            var clock = new TestClock();
            var workflow = new SetupWorkflow(repository, clock);
            await repository.InitializeAsync();
            await workflow.SaveProfileAsync(
                "Default",
                [new("Phone", VisualObservability.Observable)]);
            var goal = await workflow.CreateGoalAsync($"Goal {Guid.NewGuid():N}", null);
            var draft = await workflow.PrepareAsync(goal.Id);
            if (withScheduledBreak)
            {
                draft = draft with
                {
                    ScheduledBreaks =
                    [
                        new(
                            TimeSpan.FromMinutes(2),
                            TimeSpan.FromMinutes(5))
                    ]
                };
            }

            var setup = await workflow.ConfirmAsync(draft);
            var cameras = new RecordingCameraFactory(clock);
            var scriptedPreflight = preflightSteps ??
            [
                firstStep ?? PerceptionFakeStep.Return(Success(AcceptableObservation()))
            ];
            var perception = new DeterministicPerceptionFake(
                scriptedPreflight.Concat(
                [
                    PerceptionFakeStep.Delayed(Success(AcceptableObservation()))
                ]));
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
                    new DeterministicReasoningFake(
                        recoveryOutcome is null
                            ? [ReasoningFakeStep.Continue()]
                            :
                            [
                                ReasoningFakeStep.ListedIntervention(
                                    setup.Contract.Deviations[0].Id)
                            ]),
                    clock,
                    TimeSpan.FromMinutes(1)),
                recoveryOutcome is { } outcome
                    ? new ProposalRecoveryPort(outcome)
                    : new DeterministicTextRecoveryFake([]),
                clock);
            observationSink.Target = controller;
            healthSink.Target = controller;
            var presentation = new SessionRuntimePresentation(
                controller,
                repository,
                Options.Create(new SessionRuntimeUiOptions
                {
                    CameraDeviceIndex = 0,
                    CameraWarmupFrameCount = 0,
                    CameraJpegQuality = 85,
                    CaptureCadence = TimeSpan.FromSeconds(10),
                    ObservationFreshnessLimit = TimeSpan.FromMinutes(1),
                    TechnicalGracePeriod = TimeSpan.FromSeconds(5)
                }),
                Options.Create(new GoalKeeperOperationalOptions
                {
                    DataRoot = root,
                    Providers = new GoalKeeperProviderOptions
                    {
                        Mode = providerMode
                    }
                }));
            return new(
                root,
                repository,
                workflow,
                clock,
                cameras,
                perception,
                controller,
                presentation,
                setup);
        }

        public async Task<SessionSetupView> CreateSetupAsync()
        {
            var goal = await _workflow.CreateGoalAsync($"Goal {Guid.NewGuid():N}", null);
            var draft = await _workflow.PrepareAsync(goal.Id);
            return await _workflow.ConfirmAsync(draft);
        }

        public async Task<Guid> StartAsync()
        {
            var preflight = await Presentation.CaptureAsync(Setup.Id, retry: false);
            Assert.True(preflight.CanConfirm);
            var started = await Presentation.ConfirmAndStartAsync(Setup.Id);
            Assert.True(started.Started);
            Assert.NotNull(started.SessionId);
            return started.SessionId.Value;
        }

        public async Task PublishBehaviorObservationAsync(Guid sessionId)
        {
            var session = await Repository.GetSessionAsync(sessionId)
                ?? throw new InvalidOperationException("Session not found.");
            var observation = BehaviorObservation();
            var snapshot = await Repository.AddSnapshotAsync(new(
                Guid.NewGuid(),
                sessionId,
                100,
                Clock.UtcNow,
                Clock.MonotonicNow,
                "behavior.jpg",
                10,
                SnapshotProcessingStatus.Captured,
                session.Version));
            var persisted = await Repository.AddObservationAsync(new(
                Guid.NewGuid(),
                sessionId,
                snapshot.Id,
                session.Version,
                Clock.UtcNow,
                ObservationSchemaVersions.V1,
                JsonSerializer.Serialize(observation)));
            await Controller.PublishAsync(new(persisted, observation));
        }

        public async ValueTask DisposeAsync()
        {
            Presentation.Dispose();
            await Controller.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
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
            return Task.FromResult<RecoveryPortResult>(
                new RecoveryProposalResponse(
                    new(
                        request.SessionId,
                        request.SessionVersion,
                        request.Intervention.InterventionId,
                        request.NextTurnNumber,
                        outcome,
                        transcript,
                        null,
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
