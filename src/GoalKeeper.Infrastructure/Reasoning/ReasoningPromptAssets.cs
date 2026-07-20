using System.Reflection;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Reasoning;

namespace GoalKeeper.Infrastructure.Reasoning;

internal static class ReasoningPromptAssets
{
    public const string PromptVersion = "reasoning-v2";

    private const string PromptResourceSuffix =
        ".Reasoning.Assets.reasoning-v2.prompt.txt";

    private const string SchemaResourceSuffix =
        ".Reasoning.Assets.reasoning-v2.schema.json";

    private static readonly Lazy<string> PromptAsset = new(
        () => ReadResource(PromptResourceSuffix));

    private static readonly Lazy<JsonNode> SchemaAsset = new(
        () => JsonNode.Parse(ReadResource(SchemaResourceSuffix))
            ?? throw new InvalidOperationException("The embedded Reasoning schema is empty."));

    public static string Prompt => PromptAsset.Value;

    public static JsonNode CreateSchema(
        Guid sessionId,
        long sessionVersion,
        string currentState,
        Guid triggerObservationId)
    {
        var schema = SchemaAsset.Value.DeepClone();
        var properties = schema["properties"]!;
        properties["session_id"]!["const"] = sessionId.ToString("D");
        properties["session_version"]!["const"] = sessionVersion;
        properties["current_state"]!["const"] = currentState;
        properties["trigger_observation_id"]!["const"] =
            triggerObservationId.ToString("D");
        return schema;
    }

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(ReasoningPromptAssets).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{suffix}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
