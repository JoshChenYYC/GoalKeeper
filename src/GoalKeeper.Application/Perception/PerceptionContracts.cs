using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace GoalKeeper.Application.Perception;

public static class ObservationSchemaVersions
{
    public const int V1 = 1;
}

public static class ObservationLimits
{
    public const int MaximumJpegBytes = 20 * 1024 * 1024;
    public const int MaximumPeople = 20;
    public const int MaximumObjects = 32;
    public const int MaximumVisibleCues = 20;
    public const int MaximumLimitations = 8;
    public const int MaximumObjectLabelLength = 80;
    public const int MaximumDescriptionLength = 240;
    public const int MaximumVisualBasisLength = 320;
    public const int MaximumLimitationLength = 160;
}

[JsonConverter(typeof(JsonStringEnumConverter<ImageQualityValue>))]
public enum ImageQualityValue
{
    [JsonStringEnumMemberName("adequate")]
    Adequate,
    [JsonStringEnumMemberName("limited")]
    Limited,
    [JsonStringEnumMemberName("unusable")]
    Unusable
}

[JsonConverter(typeof(JsonStringEnumConverter<VisualSupport>))]
public enum VisualSupport
{
    [JsonStringEnumMemberName("direct")]
    Direct,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("inferred")]
    Inferred,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<PeopleCountStatus>))]
public enum PeopleCountStatus
{
    [JsonStringEnumMemberName("counted")]
    Counted,
    [JsonStringEnumMemberName("not_visible")]
    NotVisible,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<VisibleCueSubject>))]
public enum VisibleCueSubject
{
    [JsonStringEnumMemberName("visible_person")]
    VisiblePerson,
    [JsonStringEnumMemberName("scene")]
    Scene
}

[JsonConverter(typeof(JsonStringEnumConverter<VisibleCueKind>))]
public enum VisibleCueKind
{
    [JsonStringEnumMemberName("position")]
    Position,
    [JsonStringEnumMemberName("orientation")]
    Orientation,
    [JsonStringEnumMemberName("posture")]
    Posture,
    [JsonStringEnumMemberName("gaze")]
    Gaze,
    [JsonStringEnumMemberName("hand_position")]
    HandPosition,
    [JsonStringEnumMemberName("object_relation")]
    ObjectRelation,
    [JsonStringEnumMemberName("motion")]
    Motion,
    [JsonStringEnumMemberName("visibility")]
    Visibility,
    [JsonStringEnumMemberName("other_visible")]
    OtherVisible
}

[JsonConverter(typeof(JsonStringEnumConverter<VisibleCueState>))]
public enum VisibleCueState
{
    [JsonStringEnumMemberName("observed")]
    Observed,
    [JsonStringEnumMemberName("not_visible")]
    NotVisible,
    [JsonStringEnumMemberName("not_occurring")]
    NotOccurring,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

public sealed record ImageQuality
{
    public ImageQuality(ImageQualityValue value, IEnumerable<string> limitations)
    {
        Value = value;
        Limitations = Copy(limitations);
    }

    [JsonPropertyName("value")]
    public ImageQualityValue Value { get; }

    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; }

    private static ReadOnlyCollection<string> Copy(IEnumerable<string> values) =>
        new ReadOnlyCollection<string>(values.ToArray());
}

public sealed record PeopleCount
{
    public PeopleCount(
        PeopleCountStatus status,
        int? value,
        VisualSupport support,
        IEnumerable<string> limitations)
    {
        Status = status;
        Value = value;
        Support = support;
        Limitations = new ReadOnlyCollection<string>(limitations.ToArray());
    }

    [JsonPropertyName("status")]
    public PeopleCountStatus Status { get; }

    [JsonPropertyName("value")]
    public int? Value { get; }

    [JsonPropertyName("support")]
    public VisualSupport Support { get; }

    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; }
}

public sealed record VisibleCue
{
    public VisibleCue(
        VisibleCueSubject subject,
        VisibleCueKind kind,
        VisibleCueState state,
        VisualSupport support,
        string description,
        string? visualBasis,
        IEnumerable<string> limitations)
    {
        Subject = subject;
        Kind = kind;
        State = state;
        Support = support;
        Description = description;
        VisualBasis = visualBasis;
        Limitations = new ReadOnlyCollection<string>(limitations.ToArray());
    }

    [JsonPropertyName("subject")]
    public VisibleCueSubject Subject { get; }

    [JsonPropertyName("kind")]
    public VisibleCueKind Kind { get; }

    [JsonPropertyName("state")]
    public VisibleCueState State { get; }

    [JsonPropertyName("support")]
    public VisualSupport Support { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("visual_basis")]
    public string? VisualBasis { get; }

    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; }
}

public sealed record Observation
{
    public Observation(
        int schemaVersion,
        ImageQuality imageQuality,
        PeopleCount peopleCount,
        IEnumerable<string> objects,
        IEnumerable<VisibleCue> visibleCues)
    {
        SchemaVersion = schemaVersion;
        ImageQuality = imageQuality;
        PeopleCount = peopleCount;
        Objects = new ReadOnlyCollection<string>(objects.ToArray());
        VisibleCues = new ReadOnlyCollection<VisibleCue>(visibleCues.ToArray());
    }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; }

    [JsonPropertyName("image_quality")]
    public ImageQuality ImageQuality { get; }

    [JsonPropertyName("people_count")]
    public PeopleCount PeopleCount { get; }

    [JsonPropertyName("objects")]
    public IReadOnlyList<string> Objects { get; }

    [JsonPropertyName("visible_cues")]
    public IReadOnlyList<VisibleCue> VisibleCues { get; }
}

public sealed record PerceptionRequestOptions
{
    public static PerceptionRequestOptions Default { get; } = new();

    public PerceptionRequestOptions(
        int schemaVersion = ObservationSchemaVersions.V1,
        int maximumObjects = ObservationLimits.MaximumObjects,
        int maximumVisibleCues = ObservationLimits.MaximumVisibleCues)
    {
        if (schemaVersion != ObservationSchemaVersions.V1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Only Observation schema {ObservationSchemaVersions.V1} is supported.");
        }

        if (maximumObjects is < 0 or > ObservationLimits.MaximumObjects)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));
        }

        if (maximumVisibleCues is < 0 or > ObservationLimits.MaximumVisibleCues)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVisibleCues));
        }

        SchemaVersion = schemaVersion;
        MaximumObjects = maximumObjects;
        MaximumVisibleCues = maximumVisibleCues;
    }

    public int SchemaVersion { get; }

    public int MaximumObjects { get; }

    public int MaximumVisibleCues { get; }
}

public sealed class PerceptionRequest
{
    private readonly byte[] _jpegBytes;

    public PerceptionRequest(
        ReadOnlyMemory<byte> jpegBytes,
        PerceptionRequestOptions? options = null)
    {
        if (jpegBytes.Length is < 4 or > ObservationLimits.MaximumJpegBytes)
        {
            throw new ArgumentException("JPEG bytes are empty or exceed the supported size.", nameof(jpegBytes));
        }

        var span = jpegBytes.Span;
        if (span[0] != 0xff || span[1] != 0xd8 ||
            span[^2] != 0xff || span[^1] != 0xd9)
        {
            throw new ArgumentException("The request body is not a complete JPEG image.", nameof(jpegBytes));
        }

        _jpegBytes = jpegBytes.ToArray();
        Options = options ?? PerceptionRequestOptions.Default;
    }

    public ReadOnlyMemory<byte> JpegBytes => _jpegBytes;

    public PerceptionRequestOptions Options { get; }
}

public enum PerceptionFailureCategory
{
    InvalidResponse,
    Timeout,
    RateLimited,
    Network,
    Authentication,
    ProviderUnavailable,
    Cancelled,
    Unknown
}

public sealed record PerceptionMetadata
{
    public PerceptionMetadata(
        string provider,
        string model,
        string promptVersion,
        int schemaVersion,
        TimeSpan latency,
        string requestId)
    {
        Provider = SafeRequired(provider, nameof(provider), 80);
        Model = SafeRequired(model, nameof(model), 120);
        PromptVersion = SafeRequired(promptVersion, nameof(promptVersion), 80);
        RequestId = SafeRequired(requestId, nameof(requestId), 160);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(latency, TimeSpan.Zero);

        SchemaVersion = schemaVersion;
        Latency = latency;
    }

    public string Provider { get; }

    public string Model { get; }

    public string PromptVersion { get; }

    public int SchemaVersion { get; }

    public TimeSpan Latency { get; }

    public string RequestId { get; }

    private static string SafeRequired(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("Safe metadata must be non-empty, bounded, and contain no control characters.",
                parameterName);
        }

        return value;
    }
}

public abstract record PerceptionResult(PerceptionMetadata Metadata);

public sealed record PerceptionSuccess(
    Observation Proposal,
    PerceptionMetadata Metadata)
    : PerceptionResult(Metadata);

public sealed record PerceptionInvalid(
    ObservationValidationFailure Failure,
    PerceptionMetadata Metadata)
    : PerceptionResult(Metadata);

public sealed record PerceptionFailure(
    PerceptionFailureCategory Category,
    PerceptionMetadata Metadata)
    : PerceptionResult(Metadata);

public interface IPerceptionPort
{
    Task<PerceptionResult> ObserveAsync(
        PerceptionRequest request,
        CancellationToken cancellationToken = default);
}
