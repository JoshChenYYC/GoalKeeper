using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;

namespace GoalKeeper.Application.Tests;

public sealed class PreflightOrchestratorTests
{
    [Fact]
    public async Task Successful_acquisition_reports_camera_provider_and_total_timings()
    {
        var providerLatency = TimeSpan.FromSeconds(1.25);
        var preflight = new PreflightOrchestrator(
            new PreflightFrameAcquirer(new RecordingCameraFactory()),
            new DeterministicPerceptionFake(
            [
                PerceptionFakeStep.Return(new PerceptionSuccess(
                    AcceptableObservation(),
                    Metadata(providerLatency)))
            ]));

        var result = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Capture,
            new CameraAcquisitionOptions());

        Assert.NotNull(result.Timing);
        Assert.Equal(providerLatency, result.Timing.ProviderValidation);
        Assert.True(result.Timing.CameraAcquisition >= TimeSpan.Zero);
        Assert.True(result.Timing.Total >= result.Timing.CameraAcquisition);
    }

    [Fact]
    public async Task Acceptable_candidate_requires_explicit_confirmation_and_supports_retry()
    {
        var cameras = new RecordingCameraFactory();
        var perception = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Return(Success(AcceptableObservation())),
            PerceptionFakeStep.Return(Success(AcceptableObservation()))
        ]);
        var preflight = new PreflightOrchestrator(
            new PreflightFrameAcquirer(cameras),
            perception);

        Assert.Throws<InvalidOperationException>(() => preflight.Confirm(true));

        var first = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Capture,
            new CameraAcquisitionOptions());

        Assert.Equal(PreflightStatus.AwaitingConfirmation, first.Status);
        Assert.NotNull(first.Frame);
        Assert.NotNull(first.Observation);
        var rejected = preflight.Confirm(false);
        Assert.Equal(PreflightStatus.Rejected, rejected.Status);
        Assert.Equal(PreflightRejection.UserRejected, rejected.Rejection);
        Assert.True(rejected.CanRetry);

        var retry = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Retry,
            new CameraAcquisitionOptions());
        var passed = preflight.Confirm(true);

        Assert.Equal(PreflightStatus.AwaitingConfirmation, retry.Status);
        Assert.Equal(PreflightStatus.Passed, passed.Status);
        Assert.Equal(2, cameras.Cameras.Count);
        Assert.All(cameras.Cameras, camera => Assert.Equal(1, camera.ReleaseCount));
    }

    [Fact]
    public async Task Image_people_and_technical_failures_never_create_a_confirmable_candidate()
    {
        var limited = ObservationWith(
            new(ImageQualityValue.Limited, ["low light"]),
            new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []));
        var multiple = ObservationWith(
            new(ImageQualityValue.Adequate, []),
            new(PeopleCountStatus.Counted, 2, VisualSupport.Direct, []));
        var metadata = Metadata();
        var perception = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Return(Success(limited)),
            PerceptionFakeStep.Return(Success(multiple)),
            PerceptionFakeStep.Return(new PerceptionFailure(
                PerceptionFailureCategory.Network,
                metadata))
        ]);
        var cameras = new RecordingCameraFactory();
        var preflight = new PreflightOrchestrator(
            new PreflightFrameAcquirer(cameras),
            perception);

        var imageFailure = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Capture,
            new CameraAcquisitionOptions());
        var peopleFailure = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Retry,
            new CameraAcquisitionOptions());
        var technicalFailure = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Retry,
            new CameraAcquisitionOptions());

        Assert.Equal(PreflightRejection.ImageQuality, imageFailure.Rejection);
        Assert.Equal(PreflightRejection.PeopleCount, peopleFailure.Rejection);
        Assert.Equal(PreflightStatus.TechnicalFailure, technicalFailure.Status);
        Assert.Throws<InvalidOperationException>(() => preflight.Confirm(true));
        Assert.All(cameras.Cameras, camera => Assert.Equal(1, camera.ReleaseCount));
    }

    [Fact]
    public async Task Cancel_never_opens_a_camera_and_clears_an_existing_candidate()
    {
        var cameras = new RecordingCameraFactory();
        var preflight = new PreflightOrchestrator(
            new PreflightFrameAcquirer(cameras),
            new DeterministicPerceptionFake(
                [PerceptionFakeStep.Return(Success(AcceptableObservation()))]));

        await preflight.AcquireAsync(
            PreflightAcquisitionInput.Capture,
            new CameraAcquisitionOptions());
        var cancelled = preflight.Cancel();
        var acquisitionCancelled = await preflight.AcquireAsync(
            PreflightAcquisitionInput.Cancel,
            new CameraAcquisitionOptions());

        Assert.Equal(PreflightStatus.Cancelled, cancelled.Status);
        Assert.Equal(PreflightStatus.Cancelled, acquisitionCancelled.Status);
        Assert.Equal(2, cameras.Cameras.Count);
        Assert.Equal(0, cameras.Cameras[1].OpenCount);
        Assert.All(cameras.Cameras, camera => Assert.Equal(1, camera.ReleaseCount));
        Assert.Throws<InvalidOperationException>(() => preflight.Confirm(true));
    }

    private static PerceptionSuccess Success(Observation observation) =>
        new(observation, Metadata());

    private static PerceptionMetadata Metadata(TimeSpan? latency = null) =>
        new(
            "fake-provider",
            "fake-model",
            "perception-v1",
            ObservationSchemaVersions.V1,
            latency ?? TimeSpan.Zero,
            Guid.NewGuid().ToString("N"));

    private static Observation AcceptableObservation() =>
        ObservationWith(
            new ImageQuality(ImageQualityValue.Adequate, []),
            new PeopleCount(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []));

    private static Observation ObservationWith(
        ImageQuality imageQuality,
        PeopleCount peopleCount) =>
        new(
            ObservationSchemaVersions.V1,
            imageQuality,
            peopleCount,
            [],
            []);

    private sealed class RecordingCameraFactory : ICameraFactory
    {
        public List<RecordingCamera> Cameras { get; } = [];

        public ICamera Create()
        {
            var camera = new RecordingCamera();
            Cameras.Add(camera);
            return camera;
        }
    }

    private sealed class RecordingCamera : ICamera
    {
        private bool _released;

        public CameraHealth Health { get; private set; }

        public int OpenCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public ValueTask OpenAsync(
            int deviceIndex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
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
            return ValueTask.FromResult<ICameraFrame>(new Frame());
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

    private sealed class Frame : ICameraFrame
    {
        public Guid Id { get; } = Guid.NewGuid();

        public DateTimeOffset CapturedAtUtc { get; } =
            new(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);

        public TimeSpan CapturedAtMonotonic => TimeSpan.Zero;

        public int PixelWidth => 640;

        public int PixelHeight => 480;

        public void Dispose()
        {
        }
    }
}
