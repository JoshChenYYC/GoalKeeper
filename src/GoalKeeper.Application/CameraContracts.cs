using System.Collections.Immutable;

namespace GoalKeeper.Application;

public enum CameraHealth
{
    Closed,
    Opening,
    Open,
    WarmingUp,
    Ready,
    Faulted
}

public enum CameraFailureKind
{
    Open,
    Warmup,
    Capture,
    JpegEncoding,
    Release
}

public sealed record CameraTechnicalEvent(
    CameraFailureKind Kind,
    DateTimeOffset OccurredAtUtc,
    TimeSpan OccurredAtMonotonic,
    string Message);

public interface ICameraTechnicalEventSink
{
    void Report(CameraTechnicalEvent technicalEvent);
}

public sealed class CameraOperationException : Exception
{
    public CameraOperationException(CameraTechnicalEvent technicalEvent, Exception? innerException = null)
        : base(technicalEvent.Message, innerException)
    {
        TechnicalEvent = technicalEvent;
    }

    public CameraTechnicalEvent TechnicalEvent { get; }
}

public interface ICameraFrame : IDisposable
{
    Guid Id { get; }

    DateTimeOffset CapturedAtUtc { get; }

    TimeSpan CapturedAtMonotonic { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }
}

public sealed class CapturedJpegFrame
{
    public CapturedJpegFrame(
        Guid id,
        DateTimeOffset capturedAtUtc,
        TimeSpan capturedAtMonotonic,
        int pixelWidth,
        int pixelHeight,
        IEnumerable<byte> jpeg)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A captured frame requires a non-empty identifier.", nameof(id));
        }

        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capture time must be expressed as UTC.", nameof(capturedAtUtc));
        }

        if (capturedAtMonotonic < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capturedAtMonotonic),
                capturedAtMonotonic,
                "Monotonic capture time cannot be negative.");
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), pixelWidth, "Pixel width must be positive.");
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight), pixelHeight, "Pixel height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(jpeg);
        var immutableJpeg = ImmutableArray.CreateRange(jpeg);
        if (immutableJpeg.IsEmpty)
        {
            throw new ArgumentException("A captured JPEG cannot be empty.", nameof(jpeg));
        }

        Id = id;
        CapturedAtUtc = capturedAtUtc;
        CapturedAtMonotonic = capturedAtMonotonic;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Jpeg = immutableJpeg;
    }

    public Guid Id { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public TimeSpan CapturedAtMonotonic { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public ImmutableArray<byte> Jpeg { get; }
}

public interface ICamera : IAsyncDisposable
{
    CameraHealth Health { get; }

    ValueTask OpenAsync(int deviceIndex, CancellationToken cancellationToken = default);

    ValueTask WarmUpAsync(int frameCount, CancellationToken cancellationToken = default);

    ValueTask<ICameraFrame> CaptureFrameAsync(CancellationToken cancellationToken = default);

    ValueTask<CapturedJpegFrame> EncodeJpegAsync(
        ICameraFrame frame,
        int quality,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}

public interface ICameraFactory
{
    ICamera Create();
}

public sealed record CameraAcquisitionOptions(
    int DeviceIndex = 0,
    int WarmupFrameCount = 8,
    int JpegQuality = 85)
{
    public void Validate()
    {
        if (DeviceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DeviceIndex),
                DeviceIndex,
                "Camera device index cannot be negative.");
        }

        if (WarmupFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WarmupFrameCount),
                WarmupFrameCount,
                "Warmup frame count cannot be negative.");
        }

        if (JpegQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JpegQuality),
                JpegQuality,
                "JPEG quality must be between 1 and 100.");
        }
    }
}

public enum PreflightAcquisitionInput
{
    Capture,
    Retry,
    Cancel
}

public enum PreflightAcquisitionStatus
{
    Captured,
    Cancelled
}

public sealed class PreflightAcquisitionResult
{
    private PreflightAcquisitionResult(
        PreflightAcquisitionStatus status,
        PreflightAcquisitionInput input,
        CapturedJpegFrame? frame)
    {
        Status = status;
        Input = input;
        Frame = frame;
    }

    public PreflightAcquisitionStatus Status { get; }

    public PreflightAcquisitionInput Input { get; }

    public CapturedJpegFrame? Frame { get; }

    public static PreflightAcquisitionResult Captured(
        PreflightAcquisitionInput input,
        CapturedJpegFrame frame)
    {
        if (input is not (PreflightAcquisitionInput.Capture or PreflightAcquisitionInput.Retry))
        {
            throw new ArgumentException("Only capture and retry inputs can produce a frame.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(frame);
        return new(PreflightAcquisitionStatus.Captured, input, frame);
    }

    public static PreflightAcquisitionResult Cancelled() =>
        new(PreflightAcquisitionStatus.Cancelled, PreflightAcquisitionInput.Cancel, null);
}

public sealed class PreflightFrameAcquirer(ICameraFactory cameraFactory)
{
    public async Task<PreflightAcquisitionResult> AcquireAsync(
        PreflightAcquisitionInput input,
        CameraAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var camera = cameraFactory.Create();
        try
        {
            if (input == PreflightAcquisitionInput.Cancel)
            {
                return PreflightAcquisitionResult.Cancelled();
            }

            if (input is not (PreflightAcquisitionInput.Capture or PreflightAcquisitionInput.Retry))
            {
                throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown preflight acquisition input.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await camera.OpenAsync(options.DeviceIndex, cancellationToken);
            await camera.WarmUpAsync(options.WarmupFrameCount, cancellationToken);

            using var rawFrame = await camera.CaptureFrameAsync(cancellationToken);
            var jpegFrame = await camera.EncodeJpegAsync(rawFrame, options.JpegQuality, cancellationToken);
            return PreflightAcquisitionResult.Captured(input, jpegFrame);
        }
        finally
        {
            await camera.ReleaseAsync(CancellationToken.None);
        }
    }
}
