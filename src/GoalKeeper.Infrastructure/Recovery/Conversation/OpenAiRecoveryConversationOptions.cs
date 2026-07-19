namespace GoalKeeper.Infrastructure.Recovery.Conversation;

public sealed class OpenAiRecoveryConversationOptions
{
    public const string ApiKeyConfigurationKey =
        "GoalKeeper:Providers:OpenAI:ApiKey";

    public const string BaseUrlConfigurationKey =
        "GoalKeeper:Providers:OpenAI:BaseUrl";

    public const string ModelConfigurationKey =
        "GoalKeeper:Providers:Recovery:ConversationModel";

    public const string EffortConfigurationKey =
        "GoalKeeper:Providers:Recovery:ReasoningEffort";

    public string ApiKey { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.openai.com/v1");

    public string Model { get; set; } = "gpt-5.6-terra";

    public string Effort { get; set; } = "low";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

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
                $"Configuration key '{BaseUrlConfigurationKey}' must be an absolute HTTPS URL without user info, query, or fragment.");
        }

        if (!string.Equals(Model, "gpt-5.6-terra", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ModelConfigurationKey}' must be 'gpt-5.6-terra'.");
        }

        if (!string.Equals(Effort, "low", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration key '{EffortConfigurationKey}' must be 'low'.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The Recovery conversation request timeout must be positive.");
        }
    }
}
