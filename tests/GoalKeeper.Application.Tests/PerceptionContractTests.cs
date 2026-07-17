using System.Reflection;
using GoalKeeper.Application.Perception;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

public sealed class PerceptionContractTests
{
    [Fact]
    public void Request_contract_contains_only_jpeg_bytes_and_provider_safe_options()
    {
        var properties = typeof(PerceptionRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(["JpegBytes", "Options"], properties.Select(property => property.Name).Order());
        Assert.Equal(typeof(ReadOnlyMemory<byte>),
            properties.Single(property => property.Name == "JpegBytes").PropertyType);
        Assert.Equal(typeof(PerceptionRequestOptions),
            properties.Single(property => property.Name == "Options").PropertyType);

        var forbiddenNames = new[]
        {
            "goal", "title", "description", "deviation", "sensitivity", "history",
            "session", "contract", "intervention", "reasoning"
        };
        var requestGraph = new[] { typeof(PerceptionRequest), typeof(PerceptionRequestOptions) }
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public));

        Assert.DoesNotContain(requestGraph,
            property => forbiddenNames.Any(forbidden =>
                property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(requestGraph,
            property => property.PropertyType.Assembly == typeof(Goal).Assembly);
    }

    [Fact]
    public void Request_defensively_copies_and_requires_a_complete_bounded_jpeg()
    {
        byte[] source = [0xff, 0xd8, 0x01, 0xff, 0xd9];
        var request = new PerceptionRequest(source);
        source[2] = 0x7f;

        Assert.Equal(0x01, request.JpegBytes.Span[2]);
        Assert.Throws<ArgumentException>(() => new PerceptionRequest(Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new PerceptionRequest(new byte[] { 0xff, 0xd8, 0x00, 0x00 }));
        Assert.Throws<ArgumentException>(() =>
            new PerceptionRequest(new byte[ObservationLimits.MaximumJpegBytes + 1]));
    }

    [Fact]
    public void Provider_safe_options_are_bounded_to_the_v1_neutral_schema()
    {
        var options = new PerceptionRequestOptions(
            ObservationSchemaVersions.V1,
            maximumObjects: 5,
            maximumVisibleCues: 4);

        Assert.Equal(5, options.MaximumObjects);
        Assert.Equal(4, options.MaximumVisibleCues);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerceptionRequestOptions(2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PerceptionRequestOptions(maximumObjects: ObservationLimits.MaximumObjects + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PerceptionRequestOptions(maximumVisibleCues: -1));
    }

    [Fact]
    public void Result_contract_represents_safe_audit_metadata_without_payload_or_credentials()
    {
        var metadata = Metadata();
        var failure = new PerceptionFailure(PerceptionFailureCategory.Network, metadata);

        Assert.Equal("fake-provider", failure.Metadata.Provider);
        Assert.Equal("fake-model", failure.Metadata.Model);
        Assert.Equal("perception-v1", failure.Metadata.PromptVersion);
        Assert.Equal(1, failure.Metadata.SchemaVersion);
        Assert.Equal(TimeSpan.FromMilliseconds(25), failure.Metadata.Latency);
        Assert.Equal("request-123", failure.Metadata.RequestId);

        var resultProperties = new[]
            {
                typeof(PerceptionMetadata),
                typeof(PerceptionFailure),
                typeof(PerceptionInvalid),
                typeof(PerceptionSuccess)
            }
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        var prohibited = new[] { "credential", "secret", "token", "image", "jpeg", "raw", "body", "responsebody" };
        Assert.DoesNotContain(resultProperties,
            property => prohibited.Any(value =>
                property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Every_safe_failure_category_is_representable()
    {
        var categories = Enum.GetValues<PerceptionFailureCategory>();

        Assert.Equal(8, categories.Length);
        foreach (var category in categories)
        {
            Assert.Equal(category, new PerceptionFailure(category, Metadata()).Category);
        }
    }

    [Fact]
    public void Invalid_agent_output_is_a_typed_technical_result_and_never_an_observation_or_deviation()
    {
        var invalid = new PerceptionInvalid(
            new ObservationValidationFailure(
                [new("$.objects", ObservationValidationErrorCode.InvalidType, "Expected an array.")]),
            Metadata());

        Assert.IsAssignableFrom<PerceptionResult>(invalid);
        Assert.IsNotType<PerceptionSuccess>(invalid);
        Assert.DoesNotContain(
            invalid.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(Observation) ||
                        property.PropertyType == typeof(Deviation));
    }

    [Fact]
    public void Safe_metadata_rejects_empty_control_character_and_negative_latency_values()
    {
        Assert.Throws<ArgumentException>(() =>
            new PerceptionMetadata("", "model", "prompt", 1, TimeSpan.Zero, "id"));
        Assert.Throws<ArgumentException>(() =>
            new PerceptionMetadata("provider\n", "model", "prompt", 1, TimeSpan.Zero, "id"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PerceptionMetadata("provider", "model", "prompt", 0, TimeSpan.Zero, "id"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PerceptionMetadata("provider", "model", "prompt", 1, TimeSpan.FromMilliseconds(-1), "id"));
    }

    internal static PerceptionMetadata Metadata() =>
        new(
            "fake-provider",
            "fake-model",
            "perception-v1",
            ObservationSchemaVersions.V1,
            TimeSpan.FromMilliseconds(25),
            "request-123");
}
