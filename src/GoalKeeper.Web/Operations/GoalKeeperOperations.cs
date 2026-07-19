using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Web.Operations;

public enum GoalKeeperProviderMode
{
    Disabled,
    Hosted
}

public sealed class GoalKeeperOperationalOptions
{
    public const string SectionName = "GoalKeeper";

    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoalKeeper");

    public GoalKeeperProviderOptions Providers { get; set; } = new();
}

public sealed class GoalKeeperProviderOptions
{
    public GoalKeeperProviderMode Mode { get; set; }

    public OpenAiHostOptions OpenAI { get; set; } = new();

    public PerceptionHostOptions Perception { get; set; } = new();

    public ReasoningHostOptions Reasoning { get; set; } = new();

    public RecoveryHostOptions Recovery { get; set; } = new();
}

public sealed class OpenAiHostOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}

public sealed class PerceptionHostOptions
{
    public string Model { get; set; } = "gpt-5.6-luna";

    public string ImageDetail { get; set; } = "low";
}

public sealed class ReasoningHostOptions
{
    public string Model { get; set; } = "gpt-5.6-sol";

    public string Effort { get; set; } = "medium";
}

public sealed class RecoveryHostOptions
{
    public string ConversationModel { get; set; } = "gpt-5.6-terra";

    public string ReasoningEffort { get; set; } = "low";

    public string TranscriptionModel { get; set; } = "gpt-4o-transcribe";

    public string SpeechModel { get; set; } = "tts-1";

    public string Voice { get; set; } = "coral";
}

public static class GoalKeeperOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddGoalKeeperOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<GoalKeeperOperationalOptions>,
            GoalKeeperOperationalOptionsValidator>();
        services.AddOptions<GoalKeeperOperationalOptions>()
            .Bind(configuration.GetSection(
                GoalKeeperOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<GoalKeeperOperationalLogger>();
        return services;
    }

    public static string ResolveDataRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[
            $"{GoalKeeperOperationalOptions.SectionName}:DataRoot"];
        var resolved = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "GoalKeeper")
            : configured;
        if (!Path.IsPathFullyQualified(resolved))
        {
            throw new InvalidOperationException(
                "Configuration key 'GoalKeeper:DataRoot' must be an absolute path.");
        }

        return resolved;
    }
}

public sealed class GoalKeeperOperationalOptionsValidator :
    IValidateOptions<GoalKeeperOperationalOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        GoalKeeperOperationalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.DataRoot) ||
            !Path.IsPathFullyQualified(options.DataRoot))
        {
            failures.Add(
                "Configuration key 'GoalKeeper:DataRoot' must be an absolute path.");
        }

        if (!Enum.IsDefined(options.Providers.Mode))
        {
            failures.Add(
                "Configuration key 'GoalKeeper:Providers:Mode' must be 'Disabled' or 'Hosted'.");
        }

        if (options.Providers.Mode == GoalKeeperProviderMode.Hosted)
        {
            ValidateHosted(options.Providers, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateHosted(
        GoalKeeperProviderOptions providers,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(providers.OpenAI.ApiKey) ||
            providers.OpenAI.ApiKey.Any(char.IsControl))
        {
            failures.Add(
                "Configuration key 'GoalKeeper:Providers:OpenAI:ApiKey' is required in Hosted mode.");
        }

        if (!Uri.TryCreate(
                providers.OpenAI.BaseUrl,
                UriKind.Absolute,
                out var baseUrl) ||
            !string.Equals(
                baseUrl.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo) ||
            !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            failures.Add(
                "Configuration key 'GoalKeeper:Providers:OpenAI:BaseUrl' must be an absolute HTTPS URL without user info, query, or fragment.");
        }

        Exact(
            providers.Perception.Model,
            "gpt-5.6-luna",
            "GoalKeeper:Providers:Perception:Model",
            failures);
        Exact(
            providers.Perception.ImageDetail,
            "low",
            "GoalKeeper:Providers:Perception:ImageDetail",
            failures);
        Exact(
            providers.Reasoning.Model,
            "gpt-5.6-sol",
            "GoalKeeper:Providers:Reasoning:Model",
            failures);
        Exact(
            providers.Reasoning.Effort,
            "medium",
            "GoalKeeper:Providers:Reasoning:Effort",
            failures);
        Exact(
            providers.Recovery.ConversationModel,
            "gpt-5.6-terra",
            "GoalKeeper:Providers:Recovery:ConversationModel",
            failures);
        Exact(
            providers.Recovery.ReasoningEffort,
            "low",
            "GoalKeeper:Providers:Recovery:ReasoningEffort",
            failures);
        Exact(
            providers.Recovery.TranscriptionModel,
            "gpt-4o-transcribe",
            "GoalKeeper:Providers:Recovery:TranscriptionModel",
            failures);
        Exact(
            providers.Recovery.SpeechModel,
            "tts-1",
            "GoalKeeper:Providers:Recovery:SpeechModel",
            failures);
        Exact(
            providers.Recovery.Voice,
            "coral",
            "GoalKeeper:Providers:Recovery:Voice",
            failures);
    }

    private static void Exact(
        string value,
        string expected,
        string key,
        List<string> failures)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            failures.Add(
                $"Configuration key '{key}' must be '{expected}'.");
        }
    }
}

public sealed partial class GoalKeeperOperationalLogger(
    ILogger<GoalKeeperOperationalLogger> logger)
{
    private const int MaximumIdentifierLength = 160;

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    public void TechnicalBoundaryEvent(
        string boundary,
        string category,
        Guid sessionId,
        string? requestId = null)
    {
        var safeBoundary = SafeBoundary(boundary);
        var safeCategory = SafeCategory(category);
        var safeRequestId = SafeRequestId(requestId);
        if (safeCategory == "recovered")
        {
            LogBoundaryRecovered(
                logger,
                safeBoundary,
                sessionId,
                safeRequestId);
            return;
        }

        LogTechnicalBoundary(
            logger,
            safeBoundary,
            safeCategory,
            sessionId,
            safeRequestId);
    }

    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Warning,
        Message = "Technical boundary event: boundary={Boundary}, category={Category}, session_id={SessionId}, request_id={RequestId}")]
    private static partial void LogTechnicalBoundary(
        ILogger logger,
        string boundary,
        string category,
        Guid sessionId,
        string requestId);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Information,
        Message = "Technical boundary recovered: boundary={Boundary}, session_id={SessionId}, request_id={RequestId}")]
    private static partial void LogBoundaryRecovered(
        ILogger logger,
        string boundary,
        Guid sessionId,
        string requestId);

    private static string SafeBoundary(string value) =>
        value switch
        {
            "camera" or "perception" or "reasoning" or "recovery" => value,
            _ => "redacted"
        };

    private static string SafeCategory(string value) =>
        value switch
        {
            "authentication" or
            "capture" or
            "cancelled" or
            "indeterminate" or
            "invalid_response" or
            "jpeg_encoding" or
            "network" or
            "open" or
            "provider_unavailable" or
            "rate_limited" or
            "recovered" or
            "release" or
            "stale_result" or
            "technical_grace_expired" or
            "timeout" => value,
            "warmup" => value,
            _ => "redacted"
        };

    private static string SafeRequestId(string? value) =>
        value is not null &&
        value.Length is > 0 and <= MaximumIdentifierLength &&
        SafeIdentifierPattern().IsMatch(value) &&
        value.StartsWith("req_", StringComparison.Ordinal)
            ? value
            : "redacted";
}
