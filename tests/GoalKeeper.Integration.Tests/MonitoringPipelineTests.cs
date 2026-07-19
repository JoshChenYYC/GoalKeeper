using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using System.Diagnostics;
using System.Text;

namespace GoalKeeper.Integration.Tests;

public sealed class MonitoringPipelineTests
{
    [Fact]
    public async Task Fixed_grid_skips_missed_slots_without_burst_catch_up()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var delay = new AutoCancelDelay(clock, cancellation, completedDelays: 2);
        var camera = new RecordingCamera(clock)
        {
            AfterCapture = count =>
            {
                if (count == 1)
                {
                    clock.Advance(TimeSpan.FromSeconds(25));
                }
            }
        };
        var repository = new RecordingRepository();
        var pipeline = CreatePipeline(
            camera,
            new ReturningPerception(_ => Success()),
            repository,
            new RecordingArtifactStore(),
            delay,
            clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(
                new SessionState(),
                Options(cadence: TimeSpan.FromSeconds(10)),
                cancellation.Token));

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(40)],
            camera.CaptureTimes);
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            delay.Completed);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Slow_perception_keeps_only_in_flight_and_newest_pending_frame()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var delay = new ManualDelay(clock);
        var camera = new RecordingCamera(clock);
        var repository = new RecordingRepository();
        var perception = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Delayed(Success()),
            PerceptionFakeStep.Return(Success())
        ]);
        var artifacts = new RecordingArtifactStore();
        var observations = new RecordingObservationSink();
        var pipeline = CreatePipeline(
            camera,
            perception,
            repository,
            artifacts,
            delay,
            clock,
            observations);

        var run = pipeline.RunAsync(
            new SessionState(),
            Options(),
            cancellation.Token);
        await EventuallyAsync(() => camera.CaptureTimes.Count == 1 &&
                                    perception.PendingDelayCount == 1);
        await delay.AdvanceAsync();
        await EventuallyAsync(() => camera.CaptureTimes.Count == 2);
        await delay.AdvanceAsync();
        await EventuallyAsync(() => camera.CaptureTimes.Count == 3);
        await delay.AdvanceAsync();
        await EventuallyAsync(() => camera.CaptureTimes.Count == 4);

        Assert.Equal(SnapshotProcessingStatus.Captured, repository.Snapshots[0].Status);
        Assert.Equal(SnapshotProcessingStatus.Superseded, repository.Snapshots[1].Status);
        Assert.Equal(SnapshotProcessingStatus.Superseded, repository.Snapshots[2].Status);
        Assert.Equal(SnapshotProcessingStatus.Captured, repository.Snapshots[3].Status);

        perception.ReleaseNextDelay();
        await EventuallyAsync(() => observations.Observations.Count == 2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(
            [
                SnapshotProcessingStatus.Observed,
                SnapshotProcessingStatus.Superseded,
                SnapshotProcessingStatus.Superseded,
                SnapshotProcessingStatus.Observed
            ],
            repository.Snapshots.Select(snapshot => snapshot.Status));
        Assert.Equal(4, artifacts.Retained.Count);
        Assert.Equal(2, perception.Requests.Count);
        Assert.Equal(0, perception.PendingDelayCount);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Accelerated_soak_keeps_only_in_flight_and_newest_pending_frame()
    {
        const int captureCount = 250;
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var delay = new ManualDelay(clock);
        var camera = new RecordingCamera(clock);
        var repository = new RecordingRepository();
        var perception = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Delayed(Success()),
            PerceptionFakeStep.Return(Success())
        ]);
        var artifacts = new RecordingArtifactStore();
        var observations = new RecordingObservationSink();
        var pipeline = CreatePipeline(
            camera,
            perception,
            repository,
            artifacts,
            delay,
            clock,
            observations);

        var run = pipeline.RunAsync(
            new SessionState(),
            Options(),
            cancellation.Token);
        await EventuallyAsync(() => camera.CaptureTimes.Count == 1 &&
                                    perception.PendingDelayCount == 1);
        for (var capture = 1; capture < captureCount; capture++)
        {
            await delay.AdvanceAsync();
            await EventuallyAsync(() => camera.CaptureTimes.Count == capture + 1);
        }

        Assert.Equal(captureCount, repository.Snapshots.Length);
        Assert.Equal(
            2,
            repository.Snapshots.Count(snapshot =>
                snapshot.Status == SnapshotProcessingStatus.Captured));
        Assert.Equal(
            captureCount - 2,
            repository.Snapshots.Count(snapshot =>
                snapshot.Status == SnapshotProcessingStatus.Superseded));
        Assert.Single(perception.Requests);
        Assert.Equal(captureCount, artifacts.Retained.Count);
        Assert.Equal(captureCount * 5, repository.Snapshots.Sum(snapshot => snapshot.StoredBytes));

        perception.ReleaseNextDelay();
        await EventuallyAsync(() =>
            observations.Observations.Count == 1 &&
            perception.Requests.Count == 2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(2, perception.Requests.Count);
        Assert.Equal(0, perception.PendingDelayCount);
        Assert.Equal(
            1,
            repository.Snapshots.Count(snapshot =>
                snapshot.Status == SnapshotProcessingStatus.Observed));
        Assert.Equal(
            1,
            repository.Snapshots.Count(snapshot =>
                snapshot.Status == SnapshotProcessingStatus.Stale));
        Assert.Equal(
            captureCount - 2,
            repository.Snapshots.Count(snapshot =>
                snapshot.Status == SnapshotProcessingStatus.Superseded));
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Only_fresh_valid_non_break_results_are_exposed_to_reasoning()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var delay = new AutoCancelDelay(clock, cancellation, completedDelays: 4);
        var camera = new RecordingCamera(clock);
        var session = new SessionState
        {
            IsBreak = () => camera.CaptureTimes.Count == 4
        };
        var repository = new RecordingRepository();
        var calls = 0;
        var perception = new ReturningPerception(_ =>
        {
            calls++;
            return calls switch
            {
                1 => Success(),
                2 => AdvanceAndReturn(clock, TimeSpan.FromSeconds(6), Success()),
                3 => new PerceptionInvalid(
                    new ObservationValidationFailure(
                        [new("$", ObservationValidationErrorCode.MalformedJson, "invalid")]),
                    Metadata()),
                4 => new PerceptionInvalid(
                    new ObservationValidationFailure(
                        [new("$", ObservationValidationErrorCode.MalformedJson, "invalid again")]),
                    Metadata()),
                5 => new PerceptionFailure(
                    PerceptionFailureCategory.Network,
                    Metadata()),
                _ => throw new InvalidOperationException("Unexpected Perception call.")
            };
        });
        var observations = new RecordingObservationSink();
        var pipeline = CreatePipeline(
            camera,
            perception,
            repository,
            new RecordingArtifactStore(),
            delay,
            clock,
            observations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(
                session,
                Options(freshness: TimeSpan.FromSeconds(5)),
                cancellation.Token));

        Assert.Equal(
            [
                SnapshotProcessingStatus.Observed,
                SnapshotProcessingStatus.Stale,
                SnapshotProcessingStatus.AgentError,
                SnapshotProcessingStatus.Superseded,
                SnapshotProcessingStatus.AgentError
            ],
            repository.Snapshots.Select(snapshot => snapshot.Status));
        Assert.Single(repository.Observations);
        Assert.Single(observations.Observations);
        Assert.Equal(repository.Observations[0].Id, observations.Observations[0].Persisted.Id);
        Assert.IsType<ValidatedObservation>(
            ObservationValidator.Validate(
                Encoding.UTF8.GetBytes(repository.Observations[0].DocumentJson)));
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Sustained_pipeline_failures_emit_expiry_then_recovery_from_first_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var delay = new AutoCancelDelay(clock, cancellation, completedDelays: 3);
        var camera = new RecordingCamera(clock);
        var calls = 0;
        var health = new RecordingHealthSink();
        var pipeline = CreatePipeline(
            camera,
            new ReturningPerception(_ =>
            {
                calls++;
                return calls < 4
                    ? new PerceptionFailure(
                        PerceptionFailureCategory.ProviderUnavailable,
                        Metadata())
                    : Success();
            }),
            new RecordingRepository(),
            new RecordingArtifactStore(),
            delay,
            clock,
            healthSink: health);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(
                new SessionState(),
                Options(
                    cadence: TimeSpan.FromSeconds(5),
                    grace: TimeSpan.FromSeconds(10)),
                cancellation.Token));

        Assert.Collection(
            health.Events,
            expired =>
            {
                Assert.Equal(MonitoringHealthEventKind.TechnicalGraceExpired, expired.Kind);
                Assert.Equal(TimeSpan.Zero, expired.FirstFailureAtMonotonic);
                Assert.Equal(TimeSpan.FromSeconds(10), expired.OccurredAtMonotonic);
                Assert.Equal(3, expired.ConsecutiveFailures);
            },
            recovered =>
            {
                Assert.Equal(MonitoringHealthEventKind.Recovered, recovered.Kind);
                Assert.Equal(TimeSpan.Zero, recovered.FirstFailureAtMonotonic);
                Assert.Equal(TimeSpan.FromSeconds(15), recovered.OccurredAtMonotonic);
            });
    }

    [Fact]
    public async Task Open_failure_and_cancellation_both_release_camera_exactly_once()
    {
        var clock = new MutableClock();
        var openFailure = new RecordingCamera(clock)
        {
            OpenException = new InvalidOperationException("camera unavailable")
        };
        var failedPipeline = CreatePipeline(
            openFailure,
            new ReturningPerception(_ => Success()),
            new RecordingRepository(),
            new RecordingArtifactStore(),
            new ManualDelay(clock),
            clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedPipeline.RunAsync(new SessionState(), Options()));
        Assert.Equal(1, openFailure.ReleaseCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCamera = new RecordingCamera(clock);
        var cancelledPipeline = CreatePipeline(
            cancelledCamera,
            new ReturningPerception(_ => Success()),
            new RecordingRepository(),
            new RecordingArtifactStore(),
            new ManualDelay(clock),
            clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledPipeline.RunAsync(
                new SessionState(),
                Options(),
                cancellation.Token));
        Assert.Equal(1, cancelledCamera.ReleaseCount);
    }

    [Fact]
    public async Task Cancellation_stops_in_flight_perception_before_releasing_camera()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var camera = new RecordingCamera(clock);
        var perception = new CancellationBlockingPerception();
        var pipeline = CreatePipeline(
            camera,
            perception,
            new RecordingRepository(),
            new RecordingArtifactStore(),
            new ManualDelay(clock),
            clock);

        var run = pipeline.RunAsync(new SessionState(), Options(), cancellation.Token);
        await perception.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await perception.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(perception.CancellationObserved);
        Assert.Equal(0, perception.ActiveCalls);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Cancellation_stops_in_flight_observation_publication_before_releasing_camera()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MutableClock();
        var camera = new RecordingCamera(clock);
        var observations = new CancellationBlockingObservationSink();
        var pipeline = CreatePipeline(
            camera,
            new ReturningPerception(_ => Success()),
            new RecordingRepository(),
            new RecordingArtifactStore(),
            new ManualDelay(clock),
            clock,
            observations);

        var run = pipeline.RunAsync(new SessionState(), Options(), cancellation.Token);
        await observations.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await observations.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observations.CancellationObserved);
        Assert.Equal(0, observations.ActiveCalls);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Session_artifact_store_retains_controller_named_jpeg_and_deletes_only_owned_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"goalkeeper-gk007-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionArtifactStore(root);
            var sessionId = Guid.NewGuid();
            var frame = new CapturedJpegFrame(
                Guid.NewGuid(),
                new(2026, 7, 17, 18, 0, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(2),
                640,
                480,
                [0xff, 0xd8, 0x01, 0xff, 0xd9]);

            var retained = await store.RetainAsync(sessionId, 7, frame);

            Assert.EndsWith(
                $"00000007-{frame.Id:N}.jpg",
                retained.Path,
                StringComparison.Ordinal);
            Assert.Equal(frame.Jpeg.Length, retained.StoredBytes);
            Assert.Equal(frame.Jpeg.ToArray(), await File.ReadAllBytesAsync(retained.Path));

            await store.DeleteAsync(sessionId, retained.Path);
            Assert.False(File.Exists(retained.Path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MonitoringPipeline CreatePipeline(
        RecordingCamera camera,
        IPerceptionPort perception,
        RecordingRepository repository,
        ISnapshotArtifactStore artifacts,
        IMonitoringDelay delay,
        IClock clock,
        IMonitoringObservationSink? observations = null,
        RecordingHealthSink? healthSink = null) =>
        new(
            new SingleCameraFactory(camera),
            perception,
            repository,
            artifacts,
            delay,
            clock,
            observations ?? new RecordingObservationSink(),
            healthSink ?? new RecordingHealthSink());

    private static MonitoringOptions Options(
        TimeSpan? cadence = null,
        TimeSpan? freshness = null,
        TimeSpan? grace = null) =>
        new(
            cadence ?? TimeSpan.FromSeconds(10),
            freshness ?? TimeSpan.FromMinutes(1),
            grace ?? TimeSpan.FromMinutes(1),
            new CameraAcquisitionOptions(0, 0, 85));

    private static PerceptionResult AdvanceAndReturn(
        MutableClock clock,
        TimeSpan elapsed,
        PerceptionResult result)
    {
        clock.Advance(elapsed);
        return result;
    }

    private static PerceptionSuccess Success() =>
        new(
            new Observation(
                ObservationSchemaVersions.V1,
                new ImageQuality(ImageQualityValue.Adequate, []),
                new PeopleCount(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
                ["laptop"],
                []),
            Metadata());

    private static PerceptionMetadata Metadata() =>
        new(
            "fake-provider",
            "fake-model",
            "perception-v1",
            ObservationSchemaVersions.V1,
            TimeSpan.Zero,
            Guid.NewGuid().ToString("N"));

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(5))
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.Fail("The asynchronous condition was not reached within five seconds.");
    }

    private sealed class MutableClock : IClock
    {
        private readonly object _sync = new();
        private TimeSpan _monotonic;
        private DateTimeOffset _utc =
            new(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);

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

        public void Advance(TimeSpan duration)
        {
            lock (_sync)
            {
                _monotonic += duration;
                _utc += duration;
            }
        }
    }

    private sealed class SessionState : IMonitoringSessionState
    {
        public Guid SessionId { get; } = Guid.NewGuid();

        public long SessionVersion => 1;

        public Func<bool> IsBreak { get; init; } = () => false;

        public bool IsScheduledBreak => IsBreak();
    }

    private sealed class SingleCameraFactory(RecordingCamera camera) : ICameraFactory
    {
        public ICamera Create() => camera;
    }

    private sealed class RecordingCamera(MutableClock clock) : ICamera
    {
        private bool _released;

        public Exception? OpenException { get; init; }

        public Action<int>? AfterCapture { get; init; }

        public CameraHealth Health { get; private set; }

        public List<TimeSpan> CaptureTimes { get; } = [];

        public int ReleaseCount { get; private set; }

        public ValueTask OpenAsync(
            int deviceIndex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenException is not null)
            {
                return ValueTask.FromException(OpenException);
            }

            Health = CameraHealth.Open;
            return ValueTask.CompletedTask;
        }

        public ValueTask WarmUpAsync(
            int frameCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Health = CameraHealth.Ready;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICameraFrame> CaptureFrameAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = new Frame(clock.UtcNow, clock.MonotonicNow);
            CaptureTimes.Add(frame.CapturedAtMonotonic);
            AfterCapture?.Invoke(CaptureTimes.Count);
            return ValueTask.FromResult<ICameraFrame>(frame);
        }

        public ValueTask<CapturedJpegFrame> EncodeJpegAsync(
            ICameraFrame frame,
            int quality,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CapturedJpegFrame(
                frame.Id,
                frame.CapturedAtUtc,
                frame.CapturedAtMonotonic,
                frame.PixelWidth,
                frame.PixelHeight,
                [0xff, 0xd8, 0x01, 0xff, 0xd9]));
        }

        public ValueTask ReleaseAsync(
            CancellationToken cancellationToken = default)
        {
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

    private sealed class AutoCancelDelay(
        MutableClock clock,
        CancellationTokenSource cancellation,
        int completedDelays) : IMonitoringDelay
    {
        public List<TimeSpan> Completed { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (Completed.Count == completedDelays)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            clock.Advance(delay);
            Completed.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualDelay(MutableClock clock) : IMonitoringDelay
    {
        private readonly object _sync = new();
        private PendingDelay? _pending;

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            var signal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending = new(delay, signal);
            }

            await signal.Task.WaitAsync(cancellationToken);
        }

        public async Task AdvanceAsync()
        {
            PendingDelay? pending = null;
            await EventuallyAsync(() =>
            {
                lock (_sync)
                {
                    pending = _pending;
                    return pending is not null;
                }
            });

            lock (_sync)
            {
                _pending = null;
            }

            clock.Advance(pending!.Delay);
            pending.Signal.SetResult();
        }

        private sealed record PendingDelay(
            TimeSpan Delay,
            TaskCompletionSource Signal);
    }

    private sealed class ReturningPerception(
        Func<PerceptionRequest, PerceptionResult> response) : IPerceptionPort
    {
        public Task<PerceptionResult> ObserveAsync(
            PerceptionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class CancellationBlockingPerception : IPerceptionPort
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveCalls { get; private set; }

        public bool CancellationObserved { get; private set; }

        public async Task<PerceptionResult> ObserveAsync(
            PerceptionRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            ActiveCalls++;
            Entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The scripted Perception call should be cancelled.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            finally
            {
                ActiveCalls--;
                Exited.SetResult();
            }
        }
    }

    private sealed class RecordingArtifactStore : ISnapshotArtifactStore
    {
        public List<(Guid SessionId, int Sequence, Guid FrameId)> Retained { get; } = [];

        public Task<RetainedSnapshotArtifact> RetainAsync(
            Guid sessionId,
            int sequence,
            CapturedJpegFrame frame,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Retained.Add((sessionId, sequence, frame.Id));
            return Task.FromResult(
                new RetainedSnapshotArtifact(
                    $"{sessionId:N}/{sequence:D8}-{frame.Id:N}.jpg",
                    frame.Jpeg.Length));
        }

        public Task DeleteAsync(
            Guid sessionId,
            string path,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingObservationSink : IMonitoringObservationSink
    {
        public List<ReasoningEligibleObservation> Observations { get; } = [];

        public Task PublishAsync(
            ReasoningEligibleObservation observation,
            CancellationToken cancellationToken = default)
        {
            Observations.Add(observation);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationBlockingObservationSink : IMonitoringObservationSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveCalls { get; private set; }

        public bool CancellationObserved { get; private set; }

        public async Task PublishAsync(
            ReasoningEligibleObservation observation,
            CancellationToken cancellationToken = default)
        {
            _ = observation;
            ActiveCalls++;
            Entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            finally
            {
                ActiveCalls--;
                Exited.SetResult();
            }
        }
    }

    private sealed class RecordingHealthSink : IMonitoringHealthEventSink
    {
        public List<MonitoringHealthEvent> Events { get; } = [];

        public void Report(MonitoringHealthEvent healthEvent) =>
            Events.Add(healthEvent);
    }

    private sealed class RecordingRepository : IGoalKeeperRepository
    {
        private readonly object _sync = new();
        private readonly List<SnapshotView> _snapshots = [];
        private readonly List<ObservationView> _observations = [];

        public SnapshotView[] Snapshots
        {
            get
            {
                lock (_sync)
                {
                    return _snapshots.ToArray();
                }
            }
        }

        public ObservationView[] Observations
        {
            get
            {
                lock (_sync)
                {
                    return _observations.ToArray();
                }
            }
        }

        public Task<SnapshotView> AddSnapshotAsync(
            SnapshotWrite snapshot,
            CancellationToken cancellationToken = default)
        {
            var view = new SnapshotView(
                snapshot.Id,
                snapshot.SessionId,
                snapshot.Sequence,
                snapshot.CapturedAtUtc,
                snapshot.CapturedAtMonotonic,
                snapshot.ImagePath,
                snapshot.StoredBytes,
                snapshot.Status,
                snapshot.SessionVersion);
            lock (_sync)
            {
                _snapshots.Add(view);
            }

            return Task.FromResult(view);
        }

        public Task<SnapshotView> UpdateSnapshotStatusAsync(
            Guid sessionId,
            Guid snapshotId,
            SnapshotProcessingStatus status,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var index = _snapshots.FindIndex(
                    snapshot => snapshot.SessionId == sessionId &&
                                snapshot.Id == snapshotId);
                if (index < 0)
                {
                    throw new KeyNotFoundException("Snapshot not found.");
                }

                if (_snapshots[index].Status != SnapshotProcessingStatus.Captured)
                {
                    throw new InvalidOperationException("Snapshot status is final.");
                }

                _snapshots[index] = _snapshots[index] with { Status = status };
                return Task.FromResult(_snapshots[index]);
            }
        }

        public Task<ObservationView> AddObservationAsync(
            ObservationWrite observation,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var index = _snapshots.FindIndex(
                    snapshot => snapshot.SessionId == observation.SessionId &&
                                snapshot.Id == observation.SnapshotId);
                if (index < 0 ||
                    _snapshots[index].Status != SnapshotProcessingStatus.Captured)
                {
                    throw new InvalidOperationException(
                        "Only a captured Snapshot can produce an Observation.");
                }

                _snapshots[index] = _snapshots[index] with
                {
                    Status = SnapshotProcessingStatus.Observed
                };
                var snapshot = _snapshots[index];
                var view = new ObservationView(
                    observation.Id,
                    observation.SessionId,
                    observation.SnapshotId,
                    observation.SessionVersion,
                    snapshot.CapturedAtUtc,
                    snapshot.CapturedAtMonotonic,
                    observation.ProcessedAtUtc,
                    observation.SchemaVersion,
                    observation.DocumentJson);
                _observations.Add(view);
                return Task.FromResult(view);
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GoalView>> ListGoalsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoalView?> GetGoalAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoalView> CreateGoalAsync(string title, string? description, DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoalView> UpdateGoalAsync(Guid id, long expectedVersion, string title, string? description,
            DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteGoalAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeviationProfileView?> GetProfileAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeviationProfileView> SaveProfileAsync(
            string name,
            IReadOnlyList<DeviationInput> deviations,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionContractView?> GetLatestContractAsync(
            Guid goalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionSetupView> CreateReadySetupAsync(
            SessionContractDraft draft,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionSetupView?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionSetupView> TransitionSetupAsync(
            Guid id,
            long expectedVersion,
            SessionSetupStatus targetStatus,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FocusSessionRuntimeView> StartSessionAsync(
            Guid setupId,
            long expectedSetupVersion,
            FocusSessionRuntimeSnapshot initialRuntime,
            string? artifactDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FocusSessionRuntimeView?> GetSessionAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FocusSessionRuntimeView> UpdateSessionAsync(
            Guid id,
            RuntimeMutation mutation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionHistoryItem>> ListSessionHistoryAsync(
            Guid? goalId = null,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ObservationView>> GetRecentObservationsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReasoningCommitResult> CommitReasoningEvaluationAsync(
            ReasoningCommitRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReasoningCommitResult> AppendRejectedReasoningEvaluationAsync(
            ReasoningEvaluationWrite evaluation,
            string rejectionReason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReasoningEvaluationView>> GetRecentReasoningEvaluationsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AddRecoveryTurnAsync(
            RecoveryTurnWrite turn,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RecoveryTurnView>> GetRecoveryTurnsAsync(
            Guid sessionId,
            Guid interventionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecoveryCommitResult> CommitRecoveryTurnAsync(
            RecoveryCommitRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StorageUsageView> GetStorageUsageAsync(
            Guid? sessionId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApplicationSettingsView> GetSettingsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
