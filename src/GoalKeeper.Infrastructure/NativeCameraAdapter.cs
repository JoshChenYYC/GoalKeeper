using GoalKeeper.Application;
using GoalKeeper.Domain;

namespace GoalKeeper.Infrastructure;

public sealed class NativeCameraAdapter(
    INativeCameraFactory nativeCameraFactory,
    IClock clock,
    ICameraTechnicalEventSink technicalEventSink) : ICamera
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Guid _ownerId = Guid.NewGuid();
    private INativeCamera? _nativeCamera;
    private volatile CameraHealth _health = CameraHealth.Closed;
    private bool _released;

    public CameraHealth Health => _health;

    public async ValueTask OpenAsync(int deviceIndex, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                deviceIndex,
                "Camera device index cannot be negative.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailableForOpen();
            SetHealth(CameraHealth.Opening);

            try
            {
                _nativeCamera = nativeCameraFactory.Create();
                if (!_nativeCamera.Open(deviceIndex))
                {
                    throw Failure(
                        CameraFailureKind.Open,
                        $"Camera {deviceIndex} could not be opened.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                SetHealth(CameraHealth.Open);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetHealth(CameraHealth.Faulted);
                ReleaseAfterFailure();
                throw;
            }
            catch (CameraOperationException)
            {
                ReleaseAfterFailure();
                throw;
            }
            catch (Exception exception)
            {
                var failure = Failure(
                    CameraFailureKind.Open,
                    $"Camera {deviceIndex} failed while opening.",
                    exception);
                ReleaseAfterFailure();
                throw failure;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WarmUpAsync(int frameCount, CancellationToken cancellationToken = default)
    {
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                frameCount,
                "Warmup frame count cannot be negative.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var camera = RequireCamera(CameraHealth.Open);
            SetHealth(CameraHealth.WarmingUp);

            try
            {
                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var frame = camera.Read();
                    if (frame is null)
                    {
                        throw Failure(
                            CameraFailureKind.Warmup,
                            $"Camera stopped responding during warmup frame {frameIndex + 1}.");
                    }
                }

                SetHealth(CameraHealth.Ready);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetHealth(CameraHealth.Faulted);
                ReleaseAfterFailure();
                throw;
            }
            catch (CameraOperationException)
            {
                ReleaseAfterFailure();
                throw;
            }
            catch (Exception exception)
            {
                var failure = Failure(
                    CameraFailureKind.Warmup,
                    "Camera failed during warmup.",
                    exception);
                ReleaseAfterFailure();
                throw failure;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ICameraFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var camera = RequireCamera(CameraHealth.Ready);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativeFrame = camera.Read();
                if (nativeFrame is null)
                {
                    throw Failure(CameraFailureKind.Capture, "Camera did not return a frame.");
                }

                var capturedAtMonotonic = clock.MonotonicNow;
                var capturedAtUtc = clock.UtcNow.ToUniversalTime();
                return new NativeCameraFrame(
                    _ownerId,
                    nativeFrame,
                    Guid.NewGuid(),
                    capturedAtUtc,
                    capturedAtMonotonic);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetHealth(CameraHealth.Faulted);
                ReleaseAfterFailure();
                throw;
            }
            catch (CameraOperationException)
            {
                ReleaseAfterFailure();
                throw;
            }
            catch (Exception exception)
            {
                var failure = Failure(
                    CameraFailureKind.Capture,
                    "Camera failed while capturing a frame.",
                    exception);
                ReleaseAfterFailure();
                throw failure;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CapturedJpegFrame> EncodeJpegAsync(
        ICameraFrame frame,
        int quality,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "JPEG quality must be between 1 and 100.");
        }

        if (frame is not NativeCameraFrame nativeFrame || nativeFrame.OwnerId != _ownerId)
        {
            throw new ArgumentException("The frame was not captured by this camera.", nameof(frame));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireCamera(CameraHealth.Ready);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var jpeg = nativeFrame.EncodeJpeg(quality);
                cancellationToken.ThrowIfCancellationRequested();
                if (jpeg is null || jpeg.Length == 0)
                {
                    throw Failure(CameraFailureKind.JpegEncoding, "Camera frame could not be encoded as JPEG.");
                }

                return new CapturedJpegFrame(
                    nativeFrame.Id,
                    nativeFrame.CapturedAtUtc,
                    nativeFrame.CapturedAtMonotonic,
                    nativeFrame.PixelWidth,
                    nativeFrame.PixelHeight,
                    jpeg);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetHealth(CameraHealth.Faulted);
                ReleaseAfterFailure();
                throw;
            }
            catch (CameraOperationException)
            {
                ReleaseAfterFailure();
                throw;
            }
            catch (Exception exception)
            {
                var failure = Failure(
                    CameraFailureKind.JpegEncoding,
                    "Camera frame failed during JPEG encoding.",
                    exception);
                ReleaseAfterFailure();
                throw failure;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_released)
            {
                return;
            }

            _released = true;
            var camera = _nativeCamera;
            _nativeCamera = null;
            if (camera is null)
            {
                SetHealth(CameraHealth.Closed);
                return;
            }

            try
            {
                camera.Release();
                SetHealth(CameraHealth.Closed);
            }
            catch (Exception exception)
            {
                SetHealth(CameraHealth.Faulted);
                throw Failure(CameraFailureKind.Release, "Camera failed while releasing resources.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ReleaseAsync(CancellationToken.None);

    private INativeCamera RequireCamera(CameraHealth requiredHealth)
    {
        if (_released || _nativeCamera is null)
        {
            throw new InvalidOperationException("Camera is not open.");
        }

        if (_health != requiredHealth)
        {
            throw new InvalidOperationException($"Camera must be {requiredHealth} but is {_health}.");
        }

        return _nativeCamera;
    }

    private void EnsureAvailableForOpen()
    {
        ObjectDisposedException.ThrowIf(_released, this);

        if (_nativeCamera is not null || _health != CameraHealth.Closed)
        {
            throw new InvalidOperationException("Camera has already been opened.");
        }
    }

    private CameraOperationException Failure(
        CameraFailureKind kind,
        string message,
        Exception? innerException = null)
    {
        SetHealth(CameraHealth.Faulted);
        var technicalEvent = new CameraTechnicalEvent(
            kind,
            clock.UtcNow.ToUniversalTime(),
            clock.MonotonicNow,
            message);

        try
        {
            technicalEventSink.Report(technicalEvent);
            return new CameraOperationException(technicalEvent, innerException);
        }
        catch (Exception reportingException)
        {
            var combined = innerException is null
                ? reportingException
                : new AggregateException(innerException, reportingException);
            return new CameraOperationException(technicalEvent, combined);
        }
    }

    private void ReleaseAfterFailure()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        var camera = _nativeCamera;
        _nativeCamera = null;
        if (camera is null)
        {
            return;
        }

        try
        {
            camera.Release();
        }
        catch (Exception exception)
        {
            _ = Failure(CameraFailureKind.Release, "Camera failed while releasing resources.", exception);
        }
    }

    private void SetHealth(CameraHealth health) => _health = health;

    private sealed class NativeCameraFrame(
        Guid ownerId,
        INativeCameraFrame nativeFrame,
        Guid id,
        DateTimeOffset capturedAtUtc,
        TimeSpan capturedAtMonotonic) : ICameraFrame
    {
        private INativeCameraFrame? _nativeFrame = nativeFrame;

        public Guid OwnerId { get; } = ownerId;

        public Guid Id { get; } = id;

        public DateTimeOffset CapturedAtUtc { get; } = capturedAtUtc;

        public TimeSpan CapturedAtMonotonic { get; } = capturedAtMonotonic;

        public int PixelWidth { get; } = nativeFrame.PixelWidth;

        public int PixelHeight { get; } = nativeFrame.PixelHeight;

        public byte[]? EncodeJpeg(int quality)
        {
            ObjectDisposedException.ThrowIf(_nativeFrame is null, this);
            return _nativeFrame.EncodeJpeg(quality);
        }

        public void Dispose()
        {
            var frame = Interlocked.Exchange(ref _nativeFrame, null);
            frame?.Dispose();
        }
    }
}
