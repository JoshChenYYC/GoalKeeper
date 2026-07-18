using System.Reflection;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Perception;

namespace GoalKeeper.Infrastructure.Perception;

internal static class PerceptionPromptAssets
{
    public const string PromptVersion = "perception-v1";

    private const string PromptResourceSuffix =
        ".Perception.Assets.perception-v1.prompt.txt";

    private const string SchemaResourceSuffix =
        ".Perception.Assets.perception-v1.schema.json";

    private static readonly Lazy<string> PromptAsset = new(
        () => ReadResource(PromptResourceSuffix));

    private static readonly Lazy<JsonNode> SchemaAsset = new(
        () => JsonNode.Parse(ReadResource(SchemaResourceSuffix))
            ?? throw new InvalidOperationException("The embedded Perception schema is empty."));

    public static string Prompt => PromptAsset.Value;

    public static JsonNode CreateSchema(PerceptionRequestOptions options)
    {
        var schema = SchemaAsset.Value.DeepClone();
        schema["properties"]!["objects"]!["maxItems"] = options.MaximumObjects;
        schema["properties"]!["visible_cues"]!["maxItems"] =
            options.MaximumVisibleCues;
        return schema;
    }

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(PerceptionPromptAssets).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{suffix}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
