using NAudio.Wave;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed record RecoveryPcmFormat(
    int SampleRate,
    int BitsPerSample,
    int Channels);

public sealed class RecoveryAudioDataAvailableEventArgs(
    byte[] buffer,
    int bytesRecorded) : EventArgs
{
    public byte[] Buffer { get; } =
        buffer ?? throw new ArgumentNullException(nameof(buffer));

    public int BytesRecorded { get; } =
        bytesRecorded is >= 0 && bytesRecorded <= buffer.Length
            ? bytesRecorded
            : throw new ArgumentOutOfRangeException(nameof(bytesRecorded));
}

public sealed class RecoveryAudioCaptureStoppedEventArgs(
    Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

public interface IRecoveryAudioCaptureDeviceFactory
{
    IRecoveryAudioCaptureDevice Create(
        int deviceNumber,
        RecoveryPcmFormat format,
        int bufferMilliseconds);
}

public interface IRecoveryAudioCaptureDevice : IDisposable
{
    event EventHandler<RecoveryAudioDataAvailableEventArgs>? DataAvailable;

    event EventHandler<RecoveryAudioCaptureStoppedEventArgs>? RecordingStopped;

    void Start();

    void StopRecording();
}

public sealed class NAudioCaptureDeviceFactory :
    IRecoveryAudioCaptureDeviceFactory
{
    public IRecoveryAudioCaptureDevice Create(
        int deviceNumber,
        RecoveryPcmFormat format,
        int bufferMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(deviceNumber);

        if (format.SampleRate <= 0 ||
            format.BitsPerSample is not (8 or 16 or 24 or 32) ||
            format.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (bufferMilliseconds is < 10 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        }

        return new NAudioCaptureDevice(
            deviceNumber,
            format,
            bufferMilliseconds);
    }

    private sealed class NAudioCaptureDevice :
        IRecoveryAudioCaptureDevice
    {
        private WaveInEvent? _waveIn;
        private int _started;
        private int _stopped;

        public NAudioCaptureDevice(
            int deviceNumber,
            RecoveryPcmFormat format,
            int bufferMilliseconds)
        {
            var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(
                    format.SampleRate,
                    format.BitsPerSample,
                    format.Channels),
                BufferMilliseconds = bufferMilliseconds,
                NumberOfBuffers = 2
            };
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn = waveIn;
        }

        public event EventHandler<RecoveryAudioDataAvailableEventArgs>?
            DataAvailable;

        public event EventHandler<RecoveryAudioCaptureStoppedEventArgs>?
            RecordingStopped;

        public void Start()
        {
            var waveIn = Volatile.Read(ref _waveIn);
            ObjectDisposedException.ThrowIf(waveIn is null, this);
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Microphone capture has already started.");
            }

            try
            {
                waveIn.StartRecording();
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                throw;
            }
        }

        public void StopRecording()
        {
            var waveIn = Volatile.Read(ref _waveIn);
            if (waveIn is null ||
                Volatile.Read(ref _started) == 0 ||
                Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            waveIn.StopRecording();
        }

        public void Dispose()
        {
            var waveIn = Interlocked.Exchange(ref _waveIn, null);
            if (waveIn is null)
            {
                return;
            }

            try
            {
                StopCore(waveIn);
            }
            finally
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
                waveIn.Dispose();
            }
        }

        private void StopCore(WaveInEvent waveIn)
        {
            if (Volatile.Read(ref _started) != 0 &&
                Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                waveIn.StopRecording();
            }
        }

        private void OnDataAvailable(
            object? sender,
            WaveInEventArgs eventArgs) =>
            DataAvailable?.Invoke(
                this,
                new(
                    eventArgs.Buffer,
                    eventArgs.BytesRecorded));

        private void OnRecordingStopped(
            object? sender,
            StoppedEventArgs eventArgs)
        {
            Interlocked.Exchange(ref _stopped, 1);
            RecordingStopped?.Invoke(
                this,
                new(eventArgs.Exception));
        }
    }
}
