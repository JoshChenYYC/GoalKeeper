namespace GoalKeeper.Infrastructure.Reasoning;

public sealed class OpenAiReasoningOptions
{
    public const string ApiKeyConfigurationKey =
        "GoalKeeper:Providers:OpenAI:ApiKey";

    public const string BaseUrlConfigurationKey =
        "GoalKeeper:Providers:OpenAI:BaseUrl";

    public const string ModelConfigurationKey =
        "GoalKeeper:Providers:Reasoning:Model";

    public const string EffortConfigurationKey =
        "GoalKeeper:Providers:Reasoning:Effort";

    public string ApiKey { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.openai.com/v1");

    public string Model { get; set; } = "gpt-5.6-sol";

    public string Effort { get; set; } = "medium";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ApiKeyConfigurationKey}' is required.");
        }

        if (!BaseUrl.IsAbsoluteUri ||
            !string.Equals(BaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration key '{BaseUrlConfigurationKey}' must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(Model) ||
            Model.Length > 120 ||
            Model.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ModelConfigurationKey}' is invalid.");
        }

        if (!string.Equals(Effort, "medium", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration key '{EffortConfigurationKey}' must be 'medium'.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The Reasoning request timeout must be positive.");
        }
    }
}
