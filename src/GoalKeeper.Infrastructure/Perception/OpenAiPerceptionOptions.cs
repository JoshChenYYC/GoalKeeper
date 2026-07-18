namespace GoalKeeper.Infrastructure.Perception;

public sealed class OpenAiPerceptionOptions
{
    public const string ApiKeyConfigurationKey =
        "GoalKeeper:Providers:OpenAI:ApiKey";

    public const string BaseUrlConfigurationKey =
        "GoalKeeper:Providers:OpenAI:BaseUrl";

    public const string ModelConfigurationKey =
        "GoalKeeper:Providers:Perception:Model";

    public const string ImageDetailConfigurationKey =
        "GoalKeeper:Providers:Perception:ImageDetail";

    public string ApiKey { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.openai.com/v1");

    public string Model { get; set; } = "gpt-5.6-luna";

    public string ImageDetail { get; set; } = "low";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
            Model.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ModelConfigurationKey}' is invalid.");
        }

        if (!string.Equals(ImageDetail, "low", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ImageDetailConfigurationKey}' must be 'low'.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The Perception request timeout must be positive.");
        }
    }
}
