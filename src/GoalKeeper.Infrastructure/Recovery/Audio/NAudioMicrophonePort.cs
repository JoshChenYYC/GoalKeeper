using System.Buffers.Binary;
using System.Security.Cryptography;
using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class NAudioMicrophonePort(
    IRecoveryAudioCaptureDeviceFactory captureDeviceFactory) :
    IMicrophonePort
{
    private const int WaveHeaderBytes = 44;
    private const int BufferMilliseconds = 50;
    private static readonly RecoveryPcmFormat RequiredFormat =
        new(
            RecoveryAudioCaptureOptions.DefaultSampleRate,
            RecoveryAudioCaptureOptions.DefaultBitsPerSample,
            RecoveryAudioCaptureOptions.DefaultChannels);

    public NAudioMicrophonePort()
        : this(new NAudioCaptureDeviceFactory())
    {
    }

    public async Task<ITransientAudio?> CaptureAsync(
        RecoveryAudioCaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFormat(options);

        var durationBound = checked((long)Math.Ceiling(
            options.MaximumDuration.TotalSeconds *
            options.SampleRate *
            options.Channels *
            (options.BitsPerSample / 8d)));
        var maximumPcmBytes = Math.Min(
            checked(options.MaximumBytes - WaveHeaderBytes),
            durationBound);
        if (maximumPcmBytes <= 0 || maximumPcmBytes > int.MaxValue)
        {
            throw Failure(
                RecoveryFailureCategory.InvalidResponse,
                "The configured audio byte limit is unsupported.");
        }

        var pcm = new byte[(int)maximumPcmBytes];
        var writeCount = 0;
        var maximumAmplitude = 0;
        var sync = new object();
        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = 0;

        using var device = CreateDevice(options);
        void StopOnce()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
            {
                device.StopRecording();
            }
        }

        EventHandler<RecoveryAudioDataAvailableEventArgs> onData =
            (_, eventArgs) =>
            {
                try
                {
                    lock (sync)
                    {
                        var remaining = pcm.Length - writeCount;
                        var copied = Math.Min(
                            remaining,
                            eventArgs.BytesRecorded);
                        if (copied > 0)
                        {
                            eventArgs.Buffer.AsSpan(0, copied)
                                .CopyTo(pcm.AsSpan(writeCount));
                            maximumAmplitude = Math.Max(
                                maximumAmplitude,
                                MaximumPcm16Amplitude(
                                    eventArgs.Buffer.AsSpan(0, copied)));
                            writeCount += copied;
                        }

                        if (copied < eventArgs.BytesRecorded ||
                            writeCount == pcm.Length)
                        {
                            full.TrySetResult();
                        }
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(
                        eventArgs.Buffer.AsSpan(
                            0,
                            eventArgs.BytesRecorded));
                }
            };
        EventHandler<RecoveryAudioCaptureStoppedEventArgs> onStopped =
            (_, eventArgs) => stopped.TrySetResult(eventArgs.Exception);
        device.DataAvailable += onData;
        device.RecordingStopped += onStopped;

        try
        {
            try
            {
                device.Start();
            }
            catch (Exception exception)
            {
                throw Failure(
                    RecoveryFailureCategory.ProviderUnavailable,
                    "The microphone could not be started.",
                    exception);
            }

            using var duration = new CancellationTokenSource(
                options.MaximumDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                duration.Token);
            var boundary = await Task.WhenAny(
                    stopped.Task,
                    full.Task,
                    WaitForCancellationAsync(linked.Token))
                .ConfigureAwait(false);

            if (boundary == stopped.Task)
            {
                var captureFailure = await stopped.Task.ConfigureAwait(false);
                if (captureFailure is not null)
                {
                    throw Failure(
                        RecoveryFailureCategory.ProviderUnavailable,
                        "The microphone stopped unexpectedly.",
                        captureFailure);
                }
            }
            else
            {
                StopOnce();
                Exception? stopFailure;
                try
                {
                    stopFailure = await stopped.Task.WaitAsync(
                            TimeSpan.FromSeconds(2),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw Failure(
                        RecoveryFailureCategory.ProviderUnavailable,
                        "The microphone did not confirm that capture stopped.",
                        exception);
                }

                if (stopFailure is not null)
                {
                    throw Failure(
                        RecoveryFailureCategory.ProviderUnavailable,
                        "The microphone stopped unexpectedly.",
                        stopFailure);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            int finalCount;
            int finalAmplitude;
            lock (sync)
            {
                finalCount = writeCount & ~1;
                finalAmplitude = maximumAmplitude;
            }

            if (finalCount == 0 ||
                finalAmplitude / 32768f < options.SilenceAmplitudeThreshold)
            {
                return null;
            }

            return InMemoryTransientAudio.FromPcm16(
                pcm.AsSpan(0, finalCount),
                options.SampleRate,
                options.Channels);
        }
        finally
        {
            device.DataAvailable -= onData;
            device.RecordingStopped -= onStopped;
            try
            {
                StopOnce();
            }
            finally
            {
                lock (sync)
                {
                    CryptographicOperations.ZeroMemory(pcm);
                }
            }
        }
    }

    private IRecoveryAudioCaptureDevice CreateDevice(
        RecoveryAudioCaptureOptions options)
    {
        try
        {
            return captureDeviceFactory.Create(
                options.DeviceIndex,
                RequiredFormat,
                BufferMilliseconds);
        }
        catch (RecoveryVoiceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Failure(
                RecoveryFailureCategory.ProviderUnavailable,
                "The microphone could not be opened.",
                exception);
        }
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ConfigureAwait(false);
    }

    private static int MaximumPcm16Amplitude(ReadOnlySpan<byte> pcm)
    {
        var maximum = 0;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[index..]);
            maximum = Math.Max(maximum, Math.Abs((int)sample));
        }

        return maximum;
    }

    private static void ValidateFormat(RecoveryAudioCaptureOptions options)
    {
        if (options.SampleRate != RequiredFormat.SampleRate ||
            options.BitsPerSample != RequiredFormat.BitsPerSample ||
            options.Channels != RequiredFormat.Channels)
        {
            throw Failure(
                RecoveryFailureCategory.InvalidResponse,
                "Voice Recovery capture requires 16 kHz, 16-bit mono PCM.");
        }
    }

    private static RecoveryVoiceException Failure(
        RecoveryFailureCategory category,
        string message,
        Exception? innerException = null) =>
        new(category, VoiceRecoveryStage.Capture, message, innerException);
}
