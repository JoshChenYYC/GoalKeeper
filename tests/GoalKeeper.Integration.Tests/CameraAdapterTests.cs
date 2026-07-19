using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;

namespace GoalKeeper.Integration.Tests;

public sealed class CameraAdapterTests
{
    private static readonly DateTimeOffset CapturedAtUtc =
        new(2026, 7, 16, 20, 30, 0, TimeSpan.Zero);

    private static readonly TimeSpan CapturedAtMonotonic = TimeSpan.FromSeconds(42);

    [Fact]
    public void Native_camera_prefers_DirectShow_on_Windows()
    {
        var expected = OperatingSystem.IsWindows()
            ? OpenCvSharp.VideoCaptureAPIs.DSHOW
            : OpenCvSharp.VideoCaptureAPIs.ANY;

        Assert.Equal(expected, OpenCvNativeCamera.PreferredBackend);
    }

    [Fact]
    public async Task Open_failure_reports_a_technical_event_and_releases_exactly_once()
    {
        var native = new FakeNativeCamera { OpenResult = false };
        var sink = new RecordingTechnicalEventSink();
        var adapter = CreateAdapter(native, sink);

        var exception = await Assert.ThrowsAsync<CameraOperationException>(
            () => adapter.OpenAsync(0).AsTask());
        await adapter.ReleaseAsync();
        await adapter.DisposeAsync();

        Assert.Equal(CameraFailureKind.Open, exception.TechnicalEvent.Kind);
        Assert.Equal([CameraFailureKind.Open], sink.Events.Select(x => x.Kind));
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal(CameraHealth.Faulted, adapter.Health);
    }

    [Fact]
    public async Task Warmup_failure_releases_exactly_once()
    {
        var warmedFrame = new FakeNativeFrame();
        var native = new FakeNativeCamera();
        native.Reads.Enqueue(() => warmedFrame);
        native.Reads.Enqueue(() => null);
        var sink = new RecordingTechnicalEventSink();
        var adapter = CreateAdapter(native, sink);

        await adapter.OpenAsync(0);
        var exception = await Assert.ThrowsAsync<CameraOperationException>(
            () => adapter.WarmUpAsync(2).AsTask());
        await adapter.ReleaseAsync();

        Assert.Equal(CameraFailureKind.Warmup, exception.TechnicalEvent.Kind);
        Assert.Equal(1, warmedFrame.DisposeCount);
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal([CameraFailureKind.Warmup], sink.Events.Select(x => x.Kind));
    }

    [Fact]
    public async Task Capture_failure_releases_exactly_once()
    {
        var native = new FakeNativeCamera();
        native.Reads.Enqueue(() => null);
        var sink = new RecordingTechnicalEventSink();
        var adapter = CreateAdapter(native, sink);

        await adapter.OpenAsync(0);
        await adapter.WarmUpAsync(0);
        var exception = await Assert.ThrowsAsync<CameraOperationException>(
            () => adapter.CaptureFrameAsync().AsTask());
        await adapter.ReleaseAsync();

        Assert.Equal(CameraFailureKind.Capture, exception.TechnicalEvent.Kind);
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal([CameraFailureKind.Capture], sink.Events.Select(x => x.Kind));
    }

    [Fact]
    public async Task Jpeg_failure_reports_a_technical_event_and_releases_exactly_once()
    {
        var nativeFrame = new FakeNativeFrame { EncodedJpeg = null };
        var native = new FakeNativeCamera();
        native.Reads.Enqueue(() => nativeFrame);
        var sink = new RecordingTechnicalEventSink();
        var adapter = CreateAdapter(native, sink);

        await adapter.OpenAsync(0);
        await adapter.WarmUpAsync(0);
        using var frame = await adapter.CaptureFrameAsync();
        var exception = await Assert.ThrowsAsync<CameraOperationException>(
            () => adapter.EncodeJpegAsync(frame, 85).AsTask());
        await adapter.ReleaseAsync();

        Assert.Equal(CameraFailureKind.JpegEncoding, exception.TechnicalEvent.Kind);
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal([CameraFailureKind.JpegEncoding], sink.Events.Select(x => x.Kind));
    }

    [Fact]
    public async Task Successful_lifecycle_warms_captures_encodes_and_releases_exactly_once()
    {
        var warmupOne = new FakeNativeFrame();
        var warmupTwo = new FakeNativeFrame();
        var encodedBytes = new byte[] { 1, 2, 3, 4 };
        var capturedNativeFrame = new FakeNativeFrame
        {
            PixelWidth = 1280,
            PixelHeight = 720,
            EncodedJpeg = encodedBytes
        };
        var native = new FakeNativeCamera();
        native.Reads.Enqueue(() => warmupOne);
        native.Reads.Enqueue(() => warmupTwo);
        native.Reads.Enqueue(() => capturedNativeFrame);
        var sink = new RecordingTechnicalEventSink();
        var adapter = CreateAdapter(native, sink);

        Assert.Equal(CameraHealth.Closed, adapter.Health);
        await adapter.OpenAsync(2);
        Assert.Equal(CameraHealth.Open, adapter.Health);
        await adapter.WarmUpAsync(2);
        Assert.Equal(CameraHealth.Ready, adapter.Health);

        using var rawFrame = await adapter.CaptureFrameAsync();
        var jpegFrame = await adapter.EncodeJpegAsync(rawFrame, 90);
        encodedBytes[0] = 99;

        await adapter.ReleaseAsync();
        await adapter.ReleaseAsync();
        await adapter.DisposeAsync();

        Assert.Equal(2, native.OpenedDeviceIndex);
        Assert.Equal(1, warmupOne.DisposeCount);
        Assert.Equal(1, warmupTwo.DisposeCount);
        Assert.Equal(90, capturedNativeFrame.LastJpegQuality);
        Assert.Equal(CapturedAtUtc, jpegFrame.CapturedAtUtc);
        Assert.Equal(CapturedAtMonotonic, jpegFrame.CapturedAtMonotonic);
        Assert.Equal(1280, jpegFrame.PixelWidth);
        Assert.Equal(720, jpegFrame.PixelHeight);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, jpegFrame.Jpeg.ToArray());
        Assert.NotEqual(Guid.Empty, jpegFrame.Id);
        Assert.Empty(sink.Events);
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal(CameraHealth.Closed, adapter.Health);
    }

    [Fact]
    public async Task Cancellation_during_warmup_disposes_the_frame_and_releases_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var returnedFrame = new FakeNativeFrame();
        var native = new FakeNativeCamera();
        native.Reads.Enqueue(() =>
        {
            cancellation.Cancel();
            return returnedFrame;
        });
        var adapter = CreateAdapter(native, new RecordingTechnicalEventSink());

        await adapter.OpenAsync(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.WarmUpAsync(2, cancellation.Token).AsTask());
        await adapter.ReleaseAsync();

        Assert.Equal(1, returnedFrame.DisposeCount);
        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal(CameraHealth.Faulted, adapter.Health);
    }

    [Fact]
    public async Task Retry_input_returns_a_fresh_candidate_and_releases_exactly_once()
    {
        var frame = StubFrame.Create();
        var camera = new StubCamera(frame);
        var acquirer = new PreflightFrameAcquirer(new StubCameraFactory(camera));

        var result = await acquirer.AcquireAsync(
            PreflightAcquisitionInput.Retry,
            new CameraAcquisitionOptions(3, 8, 87));

        Assert.Equal(PreflightAcquisitionStatus.Captured, result.Status);
        Assert.Equal(PreflightAcquisitionInput.Retry, result.Input);
        Assert.Same(camera.JpegFrame, result.Frame);
        Assert.Equal(3, camera.OpenedDeviceIndex);
        Assert.Equal(8, camera.WarmupFrameCount);
        Assert.Equal(87, camera.JpegQuality);
        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Cancel_input_has_no_frame_and_releases_exactly_once()
    {
        var camera = new StubCamera(StubFrame.Create());
        var acquirer = new PreflightFrameAcquirer(new StubCameraFactory(camera));

        var result = await acquirer.AcquireAsync(
            PreflightAcquisitionInput.Cancel,
            new CameraAcquisitionOptions());

        Assert.Equal(PreflightAcquisitionStatus.Cancelled, result.Status);
        Assert.Equal(PreflightAcquisitionInput.Cancel, result.Input);
        Assert.Null(result.Frame);
        Assert.Equal(0, camera.OpenCount);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Cancellation_before_capture_releases_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var camera = new StubCamera(StubFrame.Create());
        var acquirer = new PreflightFrameAcquirer(new StubCameraFactory(camera));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => acquirer.AcquireAsync(
                PreflightAcquisitionInput.Capture,
                new CameraAcquisitionOptions(),
                cancellation.Token));

        Assert.Equal(0, camera.OpenCount);
        Assert.Equal(1, camera.ReleaseCount);
    }

    [Fact]
    public async Task Unexpected_acquisition_exception_disposes_frame_and_releases_exactly_once()
    {
        var frame = StubFrame.Create();
        var camera = new StubCamera(frame)
        {
            EncodeException = new InvalidOperationException("simulated encode exception")
        };
        var acquirer = new PreflightFrameAcquirer(new StubCameraFactory(camera));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => acquirer.AcquireAsync(
                PreflightAcquisitionInput.Capture,
                new CameraAcquisitionOptions()));

        Assert.Equal("simulated encode exception", exception.Message);
        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(1, camera.ReleaseCount);
    }

    private static NativeCameraAdapter CreateAdapter(
        FakeNativeCamera native,
        RecordingTechnicalEventSink sink) =>
        new(new FakeNativeCameraFactory(native), new FixedClock(), sink);

    private sealed class FixedClock : IClock
    {
        public TimeSpan MonotonicNow => CapturedAtMonotonic;

        public DateTimeOffset UtcNow => CapturedAtUtc;
    }

    private sealed class RecordingTechnicalEventSink : ICameraTechnicalEventSink
    {
        public List<CameraTechnicalEvent> Events { get; } = [];

        public void Report(CameraTechnicalEvent technicalEvent) => Events.Add(technicalEvent);
    }

    private sealed class FakeNativeCameraFactory(FakeNativeCamera camera) : INativeCameraFactory
    {
        public INativeCamera Create() => camera;
    }

    private sealed class FakeNativeCamera : INativeCamera
    {
        public bool OpenResult { get; init; } = true;

        public int OpenedDeviceIndex { get; private set; } = -1;

        public int ReleaseCount { get; private set; }

        public Queue<Func<INativeCameraFrame?>> Reads { get; } = [];

        public bool Open(int deviceIndex)
        {
            OpenedDeviceIndex = deviceIndex;
            return OpenResult;
        }

        public INativeCameraFrame? Read() =>
            Reads.Count == 0 ? throw new InvalidOperationException("No fake frame was queued.") : Reads.Dequeue()();

        public void Release() => ReleaseCount++;
    }

    private sealed class FakeNativeFrame : INativeCameraFrame
    {
        public int PixelWidth { get; init; } = 640;

        public int PixelHeight { get; init; } = 480;

        public byte[]? EncodedJpeg { get; init; } = [10, 20, 30];

        public int? LastJpegQuality { get; private set; }

        public int DisposeCount { get; private set; }

        public byte[]? EncodeJpeg(int quality)
        {
            LastJpegQuality = quality;
            return EncodedJpeg;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class StubCameraFactory(StubCamera camera) : ICameraFactory
    {
        public ICamera Create() => camera;
    }

    private sealed class StubCamera(StubFrame frame) : ICamera
    {
        public CapturedJpegFrame JpegFrame { get; } = new(
            frame.Id,
            frame.CapturedAtUtc,
            frame.CapturedAtMonotonic,
            frame.PixelWidth,
            frame.PixelHeight,
            [5, 6, 7]);

        public Exception? EncodeException { get; init; }

        public CameraHealth Health { get; private set; } = CameraHealth.Closed;

        public int OpenCount { get; private set; }

        public int OpenedDeviceIndex { get; private set; } = -1;

        public int WarmupFrameCount { get; private set; } = -1;

        public int JpegQuality { get; private set; } = -1;

        public int ReleaseCount { get; private set; }

        public ValueTask OpenAsync(int deviceIndex, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            OpenedDeviceIndex = deviceIndex;
            Health = CameraHealth.Open;
            return ValueTask.CompletedTask;
        }

        public ValueTask WarmUpAsync(int frameCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WarmupFrameCount = frameCount;
            Health = CameraHealth.Ready;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICameraFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICameraFrame>(frame);
        }

        public ValueTask<CapturedJpegFrame> EncodeJpegAsync(
            ICameraFrame candidate,
            int quality,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(frame, candidate);
            JpegQuality = quality;
            return EncodeException is null
                ? ValueTask.FromResult(JpegFrame)
                : ValueTask.FromException<CapturedJpegFrame>(EncodeException);
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            Health = CameraHealth.Closed;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ReleaseAsync();
    }

    private sealed class StubFrame : ICameraFrame
    {
        private StubFrame()
        {
        }

        public Guid Id { get; } = Guid.NewGuid();

        public DateTimeOffset CapturedAtUtc => CameraAdapterTests.CapturedAtUtc;

        public TimeSpan CapturedAtMonotonic => CameraAdapterTests.CapturedAtMonotonic;

        public int PixelWidth => 320;

        public int PixelHeight => 240;

        public int DisposeCount { get; private set; }

        public static StubFrame Create() => new();

        public void Dispose() => DisposeCount++;
    }
}
