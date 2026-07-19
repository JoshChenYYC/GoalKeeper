using NAudio.Wave;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public interface IRecoveryAudioPlaybackSink
{
    Task PlayPcmAsync(
        Stream pcm,
        RecoveryPcmFormat format,
        CancellationToken cancellationToken = default);

    Task PlayCueAsync(CancellationToken cancellationToken = default);
}

public sealed class NAudioRecoveryAudioPlaybackSink :
    IRecoveryAudioPlaybackSink
{
    private static readonly RecoveryPcmFormat SpeechFormat =
        new(24_000, 16, 1);

    public async Task PlayPcmAsync(
        Stream pcm,
        RecoveryPcmFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();

        using var source = new RawSourceWaveStream(
            pcm,
            new WaveFormat(
                format.SampleRate,
                format.BitsPerSample,
                format.Channels));
        using var output = new WaveOutEvent();
        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler =
            (_, eventArgs) => stopped.TrySetResult(eventArgs.Exception);
        output.PlaybackStopped += handler;
        try
        {
            output.Init(source);
            using var registration = cancellationToken.Register(
                static state => ((WaveOutEvent)state!).Stop(),
                output);
            output.Play();
            var failure = await stopped.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            output.PlaybackStopped -= handler;
        }
    }

    public async Task PlayCueAsync(
        CancellationToken cancellationToken = default)
    {
        const int durationMilliseconds = 120;
        const double frequency = 880;
        var sampleCount =
            SpeechFormat.SampleRate * durationMilliseconds / 1_000;
        var pcm = new byte[sampleCount * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = checked((short)(
                Math.Sin(
                    2 * Math.PI * frequency * index /
                    SpeechFormat.SampleRate) *
                short.MaxValue *
                0.18));
            pcm[index * 2] = unchecked((byte)sample);
            pcm[(index * 2) + 1] = unchecked((byte)(sample >> 8));
        }

        using var stream = new MemoryStream(pcm, writable: false);
        await PlayPcmAsync(stream, SpeechFormat, cancellationToken)
            .ConfigureAwait(false);
    }
}
