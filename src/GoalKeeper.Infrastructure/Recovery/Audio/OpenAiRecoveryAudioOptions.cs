namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class OpenAiRecoveryAudioOptions
{
    public const string ApiKeyConfigurationKey =
        "GoalKeeper:Providers:OpenAI:ApiKey";

    public string ApiKey { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.openai.com/v1");

    public string TranscriptionModel { get; set; } = "gpt-4o-transcribe";

    public string SpeechModel { get; set; } = "tts-1";

    public string Voice { get; set; } = "coral";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public long MaximumAudioRequestBytes { get; set; } = 25 * 1024 * 1024;

    public int MaximumTranscriptionResponseBytes { get; set; } = 64 * 1024;

    public int MaximumSpeechResponseBytes { get; set; } = 4 * 1024 * 1024;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ApiKeyConfigurationKey}' is required.");
        }

        if (!BaseUrl.IsAbsoluteUri ||
            !string.Equals(
                BaseUrl.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(BaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(BaseUrl.Query) ||
            !string.IsNullOrEmpty(BaseUrl.Fragment))
        {
            throw new InvalidOperationException(
                "The OpenAI Recovery audio base URL must be absolute HTTPS without user info, query, or fragment.");
        }

        if (!string.Equals(
                TranscriptionModel,
                "gpt-4o-transcribe",
                StringComparison.Ordinal) ||
            !string.Equals(SpeechModel, "tts-1", StringComparison.Ordinal) ||
            !string.Equals(Voice, "coral", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured Recovery audio model or voice is unsupported.");
        }

        if (RequestTimeout <= TimeSpan.Zero ||
            MaximumAudioRequestBytes <= 0 ||
            MaximumTranscriptionResponseBytes <= 0 ||
            MaximumSpeechResponseBytes <= 0)
        {
            throw new InvalidOperationException(
                "Recovery audio bounds and timeout must be positive.");
        }
    }
}
