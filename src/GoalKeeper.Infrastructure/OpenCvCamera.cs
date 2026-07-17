using GoalKeeper.Application;
using GoalKeeper.Domain;
using OpenCvSharp;

namespace GoalKeeper.Infrastructure;

public sealed class OpenCvCameraFactory(
    IClock clock,
    ICameraTechnicalEventSink technicalEventSink) : ICameraFactory
{
    public ICamera Create() =>
        new NativeCameraAdapter(new OpenCvNativeCameraFactory(), clock, technicalEventSink);
}

public sealed class OpenCvNativeCameraFactory : INativeCameraFactory
{
    public INativeCamera Create() => new OpenCvNativeCamera();
}

internal sealed class OpenCvNativeCamera : INativeCamera
{
    private VideoCapture? _capture;

    public bool Open(int deviceIndex)
    {
        if (_capture is not null)
        {
            throw new InvalidOperationException("Native camera has already been opened.");
        }

        var capture = new VideoCapture();
        _capture = capture;
        return capture.Open(deviceIndex, VideoCaptureAPIs.ANY) && capture.IsOpened();
    }

    public INativeCameraFrame? Read()
    {
        var capture = _capture ?? throw new InvalidOperationException("Native camera is not open.");
        var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            return null;
        }

        return new OpenCvNativeCameraFrame(frame);
    }

    public void Release()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null)
        {
            return;
        }

        capture.Release();
        capture.Dispose();
    }
}

internal sealed class OpenCvNativeCameraFrame(Mat frame) : INativeCameraFrame
{
    private Mat? _frame = frame;

    public int PixelWidth => GetFrame().Width;

    public int PixelHeight => GetFrame().Height;

    public byte[]? EncodeJpeg(int quality)
    {
        var parameters = new ImageEncodingParam(ImwriteFlags.JpegQuality, quality);
        Cv2.ImEncode(".jpg", GetFrame(), out var encoded, parameters);
        return encoded.Length == 0 ? null : encoded;
    }

    public void Dispose()
    {
        var frame = Interlocked.Exchange(ref _frame, null);
        frame?.Dispose();
    }

    private Mat GetFrame()
    {
        ObjectDisposedException.ThrowIf(_frame is null, this);
        return _frame;
    }
}
