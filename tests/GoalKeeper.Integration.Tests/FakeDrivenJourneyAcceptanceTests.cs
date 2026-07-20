using System.Diagnostics;
using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Domain;
using GoalKeeper.Web.Presentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GoalKeeper.Integration.Tests;

public sealed class FakeDrivenJourneyAcceptanceTests
{
    [Fact]
    public async Task Fulfilled_journey_covers_every_route_through_review_history_and_deletion()
    {
        await using var journey = await AcceptanceJourney.StartAsync();

        Assert.Contains(">Home</h1>", await journey.HtmlAsync("/"));
        Assert.Contains(
            "<h1>Accountability rules</h1>",
            await journey.HtmlAsync("/profile"));
        Assert.Contains(
            "<h1>Set up this focus session</h1>",
            await journey.HtmlAsync($"/sessions/setup/{journey.GoalId}"));
        var setupHtml = await journey.HtmlAsync(
            $"/sessions/setup/{journey.GoalId}");
        Assert.Contains(
            "after 25 minutes of focus, take a 5-minute break",
            setupHtml);
        Assert.DoesNotContain("offset:duration", setupHtml);
        var readyHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SetupId}/ready");
        Assert.Contains("One check before focus.", readyHtml);
        Assert.Contains("5 min", readyHtml);
        Assert.Contains(
            "automatically opens one bounded microphone response",
            readyHtml);
        Assert.DoesNotContain(":g min", readyHtml);

        var preflight = await journey.Presentation.CaptureAsync(
            journey.SetupId,
            retry: false);
        Assert.True(preflight.CanConfirm);
        var preflightHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SetupId}/preflight");
        Assert.Contains(
            "One person is visible and the image is usable.",
            preflightHtml);
        Assert.Contains("5 min focus", preflightHtml);
        Assert.DoesNotContain(":g min focus", preflightHtml);
        Assert.Contains("data:image/jpeg;base64", preflightHtml);

        await journey.StartSessionAsync();
        await journey.WaitForStateAsync(FocusSessionState.RecoveryCheckIn);

        var recoveryView = await journey.Presentation.GetLiveAsync(
            journey.SessionId);
        var recoveryHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/live");
        Assert.Contains("Recovery check-in", recoveryHtml);
        Assert.Contains("Reality check", recoveryHtml);
        Assert.Contains("Why GoalKeeper interrupted", recoveryHtml);
        Assert.Contains(
            System.Text.Encodings.Web.HtmlEncoder.Default.Encode(
                recoveryView!.RecoveryAccountabilityMessage!),
            recoveryHtml);
        Assert.Contains("Respond in writing", recoveryHtml);
        Assert.Contains("MIC off", recoveryHtml);
        Assert.DoesNotContain("Answer by voice", recoveryHtml);

        var recovery = await journey.Presentation.SubmitRecoveryAsync(
            journey.SessionId,
            "I checked the phone to approve the report upload, and I will return to the report.");
        Assert.Equal(FocusSessionState.RecoveryWindow, recovery!.State);
        var recoveryWindowHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/live");
        Assert.Contains("Back in focus", recoveryWindowHtml);
        Assert.Contains("Complete Goal", recoveryWindowHtml);

        var fulfilled = await journey.Presentation.CompleteGoalAsync(
            journey.SessionId);
        Assert.Equal(FocusSessionState.Fulfilled, fulfilled!.State);
        var completedHome = await journey.HtmlAsync("/");
        Assert.Contains("View history", completedHome);
        Assert.DoesNotContain(
            $"/sessions/setup/{journey.GoalId}",
            completedHome);
        var terminalHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/live");
        Assert.Contains("Session fulfilled", terminalHtml);
        Assert.Contains("Optional session review", terminalHtml);
        await journey.WaitForReleasedCamerasAsync();

        var reviewHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/review");
        Assert.Contains("How did this session go?", reviewHtml);
        Assert.Contains("Save review", reviewHtml);
        await journey.PostSession.SubmitReviewAsync(
            journey.SessionId,
            meaningfulProgress: true,
            InterventionHelpfulness.Helpful,
            "The recovery prompt helped me return to the report.",
            markGoalComplete: false);
        var completedReviewHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/review");
        Assert.Contains("This Session Review is complete.", completedReviewHtml);

        var historyHtml = await journey.HtmlAsync(
            $"/goals/{journey.GoalId}/history");
        Assert.Contains("1 Focus Session retained locally.", historyHtml);
        Assert.Contains("Fulfilled", historyHtml);
        Assert.Contains("Immutable Session Contract", historyHtml);
        Assert.Contains("Acceptance report", historyHtml);
        Assert.Contains("Default", historyHtml);
        Assert.Contains(
            "The recovery prompt helped me return to the report.",
            historyHtml);
        Assert.DoesNotContain("Not submitted", historyHtml);

        await journey.PostSession.DeleteSessionAsync(journey.SessionId);
        Assert.NotNull(await journey.Repository.GetGoalAsync(journey.GoalId));
        Assert.Null(await journey.Repository.GetSessionAsync(journey.SessionId));
        var emptyHistoryHtml = await journey.HtmlAsync(
            $"/goals/{journey.GoalId}/history");
        Assert.Contains("No Focus Sessions yet", emptyHistoryHtml);

        var goal = await journey.Repository.GetGoalAsync(journey.GoalId);
        await journey.PostSession.DeleteGoalAsync(
            journey.GoalId,
            goal!.Version);
        Assert.Null(await journey.Repository.GetGoalAsync(journey.GoalId));
        Assert.Contains("No Goals yet.", await journey.HtmlAsync("/"));
    }

    [Fact]
    public async Task End_early_terminal_route_offers_optional_review_and_allows_skip()
    {
        await using var journey = await AcceptanceJourney.StartAsync();
        var preflight = await journey.Presentation.CaptureAsync(
            journey.SetupId,
            retry: false);
        Assert.True(preflight.CanConfirm);
        await journey.StartSessionAsync();
        await journey.WaitForStateAsync(FocusSessionState.RecoveryCheckIn);

        var ended = await journey.Presentation.EndEarlyAsync(
            journey.SessionId);

        Assert.Equal(FocusSessionState.EndedEarly, ended!.State);
        var terminalHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/live");
        Assert.Contains("Session ended", terminalHtml);
        Assert.Contains("Optional session review", terminalHtml);
        await journey.WaitForReleasedCamerasAsync();

        var optionalReviewHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/review");
        Assert.Contains("How did this session go?", optionalReviewHtml);
        Assert.Contains("Skip and return Home", optionalReviewHtml);
        Assert.Contains(">Home</h1>", await journey.HtmlAsync("/"));

        var historyHtml = await journey.HtmlAsync(
            $"/goals/{journey.GoalId}/history");
        Assert.Contains("Ended Early", historyHtml);
        Assert.Contains("Not submitted", historyHtml);
    }

    [Fact]
    public async Task Configured_voice_starts_automatically_once_in_recovery()
    {
        await using var journey = await AcceptanceJourney.StartAsync(
            voiceEnabled: true);
        var preflight = await journey.Presentation.CaptureAsync(
            journey.SetupId,
            retry: false);
        Assert.True(preflight.CanConfirm);
        await journey.StartSessionAsync();
        await journey.WaitForStateAsync(FocusSessionState.RecoveryCheckIn);

        var starting = await journey.Presentation.GetLiveAsync(
            journey.SessionId);
        await journey.Voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovery = await journey.Presentation.GetLiveAsync(
            journey.SessionId);
        var recoveryHtml = await journey.HtmlAsync(
            $"/sessions/{journey.SessionId}/live");

        Assert.NotNull(starting);
        Assert.NotNull(recovery);
        Assert.True(starting.AutomaticRecoveryVoiceInProgress);
        Assert.True(recovery.AutomaticRecoveryVoiceInProgress);
        Assert.True(recovery.AutomaticRecoveryMicrophoneActive);
        Assert.False(recovery.CanSubmitVoiceRecovery);
        Assert.True(recovery.CanReplayRecoveryOpening);
        Assert.Contains("Listening now", recoveryHtml);
        Assert.Contains("MIC active", recoveryHtml);
        Assert.Contains("Prefer to type instead?", recoveryHtml);
        Assert.Contains("Missed it? Read the check-in", recoveryHtml);
        Assert.Contains(
            System.Text.Encodings.Web.HtmlEncoder.Default.Encode(
                RecoveryOpeningPrompt.CheckInQuestion),
            recoveryHtml);
        Assert.Equal(1, journey.Voice.CallCount);
        Assert.Single(journey.Speech.Spoken);
        Assert.Equal(
            RecoveryOpeningPrompt.Create(
                recovery.RecoveryAccountabilityMessage!),
            journey.Speech.Spoken[0]);
        Assert.Equal(0, journey.Speech.ListeningCueCount);

        journey.Voice.Complete();
        await journey.WaitForStateAsync(FocusSessionState.RecoveryWindow);
        await journey.WaitForAutomaticVoiceAsync(inProgress: false);
        var recommitted = await journey.Presentation.GetLiveAsync(
            journey.SessionId);

        Assert.Equal(1, journey.Voice.CallCount);
        Assert.Equal(FocusSessionState.RecoveryWindow, recommitted!.State);
        Assert.False(recommitted.CanSubmitVoiceRecovery);

        var ended = await journey.Presentation.EndEarlyAsync(
            journey.SessionId);
        Assert.True(ended!.IsTerminal);
        await journey.WaitForReleasedCamerasAsync();
    }

    private sealed class AcceptanceJourney : IAsyncDisposable
    {
        private readonly AcceptanceWebApplicationFactory _factory;
        private readonly HttpClient _client;
        private readonly AsyncServiceScope _scope;

        private AcceptanceJourney(
            AcceptanceWebApplicationFactory factory,
            HttpClient client,
            AsyncServiceScope scope,
            Guid goalId,
            Guid setupId)
        {
            _factory = factory;
            _client = client;
            _scope = scope;
            GoalId = goalId;
            SetupId = setupId;
            Presentation = factory.Services.GetRequiredService<
                ISessionRuntimePresentation>();
            Controller = factory.Services.GetRequiredService<
                SessionRuntimeController>();
            Repository = factory.Services.GetRequiredService<
                IGoalKeeperRepository>();
            PostSession = scope.ServiceProvider.GetRequiredService<
                PostSessionPresentation>();
        }

        public Guid GoalId { get; }

        public Guid SetupId { get; }

        public Guid SessionId { get; private set; }

        public ISessionRuntimePresentation Presentation { get; }

        public SessionRuntimeController Controller { get; }

        public IGoalKeeperRepository Repository { get; }

        public PostSessionPresentation PostSession { get; }

        public AcceptanceVoiceRecoveryPort Voice => _factory.Voice;

        public AcceptanceSpeechOutput Speech => _factory.Speech;

        public static async Task<AcceptanceJourney> StartAsync(
            bool voiceEnabled = false)
        {
            var factory = new AcceptanceWebApplicationFactory(voiceEnabled);
            var client = factory.CreateClient();
            var scope = factory.Services.CreateAsyncScope();
            var workflow = scope.ServiceProvider.GetRequiredService<
                SetupWorkflow>();
            await workflow.SaveProfileAsync(
                "Default",
                [new(
                    "Sustained attention to a phone",
                    VisualObservability.Observable)]);
            var goal = await workflow.CreateGoalAsync(
                "Acceptance report",
                "Finish and verify the acceptance report.");
            var draft = await workflow.PrepareAsync(goal.Id);
            var setup = await workflow.ConfirmAsync(draft with
            {
                TargetFocusDuration = TimeSpan.FromMinutes(5),
                ScheduledBreaks = []
            });
            return new(
                factory,
                client,
                scope,
                goal.Id,
                setup.Id);
        }

        public async Task StartSessionAsync()
        {
            var started = await Presentation.ConfirmAndStartAsync(SetupId);
            Assert.True(started.Started);
            Assert.NotNull(started.SessionId);
            SessionId = started.SessionId.Value;
        }

        public async Task<string> HtmlAsync(string path)
        {
            var response = await _client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, html);
            return html;
        }

        public async Task WaitForStateAsync(FocusSessionState expected)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                var status = await Controller.GetStatusAsync();
                if (status.State == expected)
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail($"The runtime did not reach {expected}.");
        }

        public async Task WaitForAutomaticVoiceAsync(bool inProgress)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                var live = await Presentation.GetLiveAsync(SessionId);
                if (live?.AutomaticRecoveryVoiceInProgress == inProgress)
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail(
                $"Automatic Recovery voice did not reach in-progress={inProgress}.");
        }

        public async Task WaitForReleasedCamerasAsync()
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                var cameras = _factory.Cameras.Cameras;
                if (cameras.Count >= 2 &&
                    cameras.All(camera => camera.ReleaseCount == 1))
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail("Camera resources were not released exactly once.");
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            _client.Dispose();
            _factory.Dispose();
            if (Directory.Exists(_factory.DataRoot))
            {
                Directory.Delete(_factory.DataRoot, recursive: true);
            }
        }
    }

    private sealed class AcceptanceWebApplicationFactory :
        WebApplicationFactory<Program>
    {
        private readonly bool _voiceEnabled;

        public AcceptanceWebApplicationFactory(bool voiceEnabled)
        {
            _voiceEnabled = voiceEnabled;
            DataRoot = Path.Combine(
                Path.GetTempPath(),
                $"goalkeeper-route-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataRoot);
        }

        public string DataRoot { get; }

        public AcceptanceCameraFactory Cameras { get; } = new();

        public AcceptanceVoiceRecoveryPort Voice { get; } = new();

        public AcceptanceSpeechOutput Speech { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GoalKeeper:DataRoot", DataRoot);
            builder.UseSetting(
                "GoalKeeper:SessionUi:CaptureCadence",
                "00:00:00.050");
            builder.UseSetting(
                "GoalKeeper:SessionUi:ObservationFreshnessLimit",
                "00:00:10");
            builder.UseSetting(
                "GoalKeeper:SessionUi:TechnicalGracePeriod",
                "00:00:01");
            builder.UseSetting(
                "GoalKeeper:Runtime:TickInterval",
                "00:00:00.050");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICameraFactory>();
                services.RemoveAll<IPerceptionPort>();
                services.RemoveAll<IReasoningPort>();
                services.RemoveAll<IRecoveryPort>();
                services.AddSingleton<ICameraFactory>(provider =>
                    Cameras.WithClock(provider.GetRequiredService<IClock>()));
                services.AddSingleton<IPerceptionPort, AcceptancePerceptionPort>();
                services.AddSingleton<IReasoningPort, AcceptanceReasoningPort>();
                services.AddSingleton<IRecoveryPort, AcceptanceRecoveryPort>();
                if (_voiceEnabled)
                {
                    services.RemoveAll<IVoiceRecoveryPort>();
                    services.AddSingleton<IVoiceRecoveryPort>(Voice);
                    services.RemoveAll<ISpeechOutputPort>();
                    services.AddSingleton<ISpeechOutputPort>(Speech);
                }
            });
        }
    }

    private sealed class AcceptancePerceptionPort : IPerceptionPort
    {
        private int _callCount;

        public async Task<PerceptionResult> ObserveAsync(
            PerceptionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) > 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new PerceptionSuccess(
                BehaviorObservation(),
                new(
                    "acceptance-fake",
                    "perception-v1",
                    "perception-v1",
                    ObservationSchemaVersions.V1,
                    TimeSpan.Zero,
                    $"perception-{Guid.NewGuid():N}"));
        }
    }

    private sealed class AcceptanceReasoningPort : IReasoningPort
    {
        private int _callCount;

        public Task<ReasoningResult> EvaluateAsync(
            ReasoningRequest request,
            CancellationToken cancellationToken = default)
        {
            var step = Interlocked.Increment(ref _callCount) == 1
                ? ReasoningFakeStep.ListedIntervention(
                    request.Contract.Deviations[0].Id)
                : ReasoningFakeStep.Continue();
            return new DeterministicReasoningFake([step])
                .EvaluateAsync(request, cancellationToken);
        }
    }

    private sealed class AcceptanceRecoveryPort : IRecoveryPort
    {
        public Task<RecoveryPortResult> ProposeAsync(
            RecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RecoveryPortResult>(
                new RecoveryProposalResponse(
                    new(
                        request.SessionId,
                        request.SessionVersion,
                        request.Intervention.InterventionId,
                        request.NextTurnNumber,
                        RecoveryOutcome.Recommit,
                        request.CurrentTranscript,
                        null,
                        null,
                        false,
                        new(request.RequestedAtUtc, request.RequestedAtUtc),
                        new(
                            "acceptance-fake",
                            "recovery-v1",
                            "recovery-v1",
                            RecoverySchemaVersions.V1,
                            TimeSpan.Zero,
                            $"recovery-{Guid.NewGuid():N}"))));
        }
    }

    public sealed class AcceptanceVoiceRecoveryPort : IVoiceRecoveryPort
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _release.TrySetResult();

        public async Task<VoiceRecoveryPortResult> ProposeAsync(
            RecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            const string transcript = "I will return to the report.";
            return new VoiceRecoveryProposalResponse(
                    transcript,
                    new(
                        request.SessionId,
                        request.SessionVersion,
                        request.Intervention.InterventionId,
                        request.NextTurnNumber,
                        RecoveryOutcome.Recommit,
                        transcript,
                        null,
                        null,
                        false,
                        new(request.RequestedAtUtc, request.RequestedAtUtc),
                        new(
                            "acceptance-fake",
                            "voice-recovery-v1",
                            "voice-recovery-v1",
                            RecoverySchemaVersions.V1,
                            TimeSpan.Zero,
                            $"voice-recovery-{Guid.NewGuid():N}")));
        }
    }

    public sealed class AcceptanceSpeechOutput : ISpeechOutputPort
    {
        public List<string> Spoken { get; } = [];

        public int ListeningCueCount { get; private set; }

        public Task SpeakAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public Task PlayListeningCueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListeningCueCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptanceCameraFactory : ICameraFactory
    {
        private readonly object _sync = new();
        private readonly List<AcceptanceCamera> _cameras = [];
        private IClock? _clock;

        public IReadOnlyList<AcceptanceCamera> Cameras
        {
            get
            {
                lock (_sync)
                {
                    return _cameras.ToArray();
                }
            }
        }

        public AcceptanceCameraFactory WithClock(IClock clock)
        {
            lock (_sync)
            {
                _clock ??= clock;
            }

            return this;
        }

        public ICamera Create()
        {
            AcceptanceCamera camera;
            lock (_sync)
            {
                camera = new AcceptanceCamera(
                    _clock ?? throw new InvalidOperationException(
                        "The acceptance camera clock was not configured."));
                _cameras.Add(camera);
            }

            return camera;
        }
    }

    private sealed class AcceptanceCamera(IClock clock) : ICamera
    {
        private int _released;

        public CameraHealth Health { get; private set; }

        public int ReleaseCount => Volatile.Read(ref _released);

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
                new AcceptanceFrame(clock.UtcNow, clock.MonotonicNow));
        }

        public ValueTask<CapturedJpegFrame> EncodeJpegAsync(
            ICameraFrame frame,
            int quality,
            CancellationToken cancellationToken = default)
        {
            _ = quality;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new CapturedJpegFrame(
                    frame.Id,
                    frame.CapturedAtUtc,
                    frame.CapturedAtMonotonic,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    [0xff, 0xd8, 0xff, 0xd9]));
        }

        public ValueTask ReleaseAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Health = CameraHealth.Closed;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ReleaseAsync();
    }

    private sealed class AcceptanceFrame(
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
}
