namespace GoalKeeper.Application.Recovery;

public sealed class RecoveryAudioCaptureOptions
{
    public const int DefaultSampleRate = 16_000;
    public const int DefaultBitsPerSample = 16;
    public const int DefaultChannels = 1;
    public const long DefaultMaximumBytes = 25 * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(30);
    public static RecoveryAudioCaptureOptions Default { get; } = new();

    public RecoveryAudioCaptureOptions(
        int deviceIndex = 0,
        int sampleRate = DefaultSampleRate,
        int bitsPerSample = DefaultBitsPerSample,
        int channels = DefaultChannels,
        TimeSpan? maximumDuration = null,
        long maximumBytes = DefaultMaximumBytes,
        float silenceAmplitudeThreshold = 0.01f)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var boundedDuration = maximumDuration ?? DefaultMaximumDuration;
        if (boundedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (silenceAmplitudeThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceAmplitudeThreshold));
        }

        DeviceIndex = deviceIndex;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        Channels = channels;
        MaximumDuration = boundedDuration;
        MaximumBytes = maximumBytes;
        SilenceAmplitudeThreshold = silenceAmplitudeThreshold;
    }

    public int DeviceIndex { get; }

    public int SampleRate { get; }

    public int BitsPerSample { get; }

    public int Channels { get; }

    public TimeSpan MaximumDuration { get; }

    public long MaximumBytes { get; }

    public float SilenceAmplitudeThreshold { get; }
}

public interface ITransientAudio : IAsyncDisposable
{
    long Length { get; }

    string ContentType { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

public interface IMicrophonePort
{
    Task<ITransientAudio?> CaptureAsync(
        RecoveryAudioCaptureOptions options,
        CancellationToken cancellationToken = default);
}

public interface ISpeechInputPort
{
    Task<string> TranscribeAsync(
        ITransientAudio audio,
        CancellationToken cancellationToken = default);
}

public interface ISpeechOutputPort
{
    Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task PlayListeningCueAsync(CancellationToken cancellationToken = default);
}

public enum VoiceRecoveryStage
{
    Opening,
    Cue,
    Capture,
    Transcription,
    Conversation,
    Playback
}

public sealed class RecoveryVoiceException : Exception
{
    public RecoveryVoiceException(RecoveryFailureCategory category)
        : this(category, VoiceRecoveryStage.Conversation)
    {
    }

    public RecoveryVoiceException(
        RecoveryFailureCategory category,
        string message,
        Exception? innerException = null)
        : this(category, VoiceRecoveryStage.Conversation, message, innerException)
    {
    }

    public RecoveryVoiceException(
        RecoveryFailureCategory category,
        VoiceRecoveryStage stage,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Voice Recovery failed during {stage}.", innerException)
    {
        Category = RecoveryGuards.Defined(category, nameof(category));
        Stage = RecoveryGuards.Defined(stage, nameof(stage));
    }

    public RecoveryFailureCategory Category { get; }

    public VoiceRecoveryStage Stage { get; }
}

public abstract record VoiceRecoveryPortResult;

public sealed record VoiceRecoveryProposalResponse : VoiceRecoveryPortResult
{
    public VoiceRecoveryProposalResponse(
        string? capturedTranscript,
        RecoveryProposal proposal)
    {
        CapturedTranscript = RecoveryGuards.OptionalText(
            capturedTranscript,
            nameof(capturedTranscript),
            RecoveryLimits.MaximumTranscriptLength);
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public string? CapturedTranscript { get; }

    public RecoveryProposal Proposal { get; }
}

public sealed record VoiceRecoveryFailureResponse : VoiceRecoveryPortResult
{
    public VoiceRecoveryFailureResponse(
        VoiceRecoveryStage stage,
        RecoveryFailureCategory category,
        RecoveryMetadata? metadata = null)
    {
        Stage = RecoveryGuards.Defined(stage, nameof(stage));
        Category = RecoveryGuards.Defined(category, nameof(category));
        Metadata = metadata;
    }

    public VoiceRecoveryStage Stage { get; }

    public RecoveryFailureCategory Category { get; }

    public RecoveryMetadata? Metadata { get; }
}

public interface IVoiceRecoveryPort
{
    Task<VoiceRecoveryPortResult> ProposeAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken = default);
}
