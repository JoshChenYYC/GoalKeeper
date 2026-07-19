using System.Text.Json;
using System.Text.Json.Serialization;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Domain;

namespace GoalKeeper.Infrastructure.Reasoning;

internal static class ReasoningResponseValidator
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false)
        }
    };

    public static bool TryValidate(
        ReadOnlyMemory<byte> outputJson,
        ReasoningRequest request,
        IReadOnlyDictionary<Guid, TimeSpan> knownTimes,
        out ReasoningProposal? proposal,
        out IReadOnlyList<string> validationReasons)
    {
        proposal = null;
        validationReasons = ["invalid_output_shape"];

        ReasoningResponseWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<ReasoningResponseWire>(
                outputJson.Span,
                ResponseJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (wire is null ||
            wire.SchemaVersion != ReasoningSchemaVersions.V2 ||
            wire.SessionId != request.SessionId ||
            wire.SessionVersion != request.SessionVersion ||
            wire.CurrentState != request.CurrentState ||
            wire.TriggerObservationId != request.NewObservation.Id ||
            !Enum.IsDefined(wire.Decision) ||
            wire.EpisodeUpdates is null ||
            wire.EpisodeUpdates.Count > ReasoningLimits.ActiveEpisodes +
            ReasoningLimits.HistoricalEpisodes)
        {
            validationReasons = ["invalid_response_context"];
            return false;
        }

        if (!TryMapIntervention(wire.Intervention, out var intervention) ||
            !TryMapEpisodes(
                wire.EpisodeUpdates,
                knownTimes,
                out var episodes))
        {
            return false;
        }

        proposal = new(
            wire.SessionId,
            wire.SessionVersion,
            wire.CurrentState,
            wire.TriggerObservationId,
            wire.Decision,
            intervention,
            episodes);
        validationReasons = [];
        return true;
    }

    private static bool TryMapIntervention(
        ReasoningInterventionWire? wire,
        out ReasoningInterventionProposal? intervention)
    {
        intervention = null;
        if (wire is null)
        {
            return true;
        }

        if (wire.FirstObservationId == Guid.Empty ||
            wire.LatestObservationId == Guid.Empty ||
            wire.KeyObservationIds is null ||
            wire.KeyObservationIds.Count is 0 or >
            EvidenceEpisodePolicy.DefaultMaximumKeyObservations ||
            wire.ContradictoryObservationIds is null ||
            wire.ContradictoryObservationIds.Count >
            EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations ||
            !SafeText(wire.Rationale, ReasoningLimits.MaximumRationaleLength) ||
            !AccountabilityMessagePolicy.IsAcceptable(
                wire.AccountabilityMessage) ||
            !SafeOptionalText(
                wire.UnlistedDescription,
                ReasoningLimits.MaximumTextLength))
        {
            return false;
        }

        intervention = new(
            wire.ListedDeviationId,
            wire.UnlistedDescription,
            wire.FirstObservationId,
            wire.LatestObservationId,
            wire.KeyObservationIds.ToArray(),
            wire.ContradictoryObservationIds.ToArray(),
            wire.Rationale!,
            wire.AccountabilityMessage!);
        return true;
    }

    private static bool TryMapEpisodes(
        IReadOnlyList<ReasoningEpisodeWire> wires,
        IReadOnlyDictionary<Guid, TimeSpan> knownTimes,
        out IReadOnlyList<ReasoningEpisodeSummary> episodes)
    {
        var mapped = new List<ReasoningEpisodeSummary>(wires.Count);
        foreach (var wire in wires)
        {
            if (!Enum.IsDefined(wire.Status) ||
                !SafeText(wire.Key, ReasoningLimits.MaximumTextLength) ||
                !SafeText(wire.Summary, ReasoningLimits.MaximumTextLength) ||
                !SafeOptionalText(
                    wire.UnlistedDescription,
                    ReasoningLimits.MaximumTextLength) ||
                wire.FirstObservationId == Guid.Empty ||
                wire.LatestObservationId == Guid.Empty ||
                wire.KeyObservationIds is null ||
                wire.KeyObservationIds.Count is 0 or >
                EvidenceEpisodePolicy.DefaultMaximumKeyObservations ||
                wire.ContradictoryObservationIds is null ||
                wire.ContradictoryObservationIds.Count >
                EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations)
            {
                episodes = [];
                return false;
            }

            if (!TryReference(
                    wire.FirstObservationId,
                    knownTimes,
                    out var first) ||
                !TryReference(
                    wire.LatestObservationId,
                    knownTimes,
                    out var latest) ||
                !TryReferences(
                    wire.KeyObservationIds,
                    knownTimes,
                    out var keys) ||
                !TryReferences(
                    wire.ContradictoryObservationIds,
                    knownTimes,
                    out var contradictions))
            {
                episodes = [];
                return false;
            }

            mapped.Add(new(
                wire.Key!,
                wire.Status,
                wire.ListedDeviationId,
                wire.UnlistedDescription,
                first,
                latest,
                keys,
                contradictions,
                wire.Summary!));
        }

        episodes = mapped;
        return true;
    }

    private static bool TryReferences(
        IEnumerable<Guid> observationIds,
        IReadOnlyDictionary<Guid, TimeSpan> knownTimes,
        out IReadOnlyList<ReasoningEvidenceReference> references)
    {
        var mapped = new List<ReasoningEvidenceReference>();
        foreach (var observationId in observationIds)
        {
            if (!TryReference(observationId, knownTimes, out var reference))
            {
                references = [];
                return false;
            }

            mapped.Add(reference);
        }

        references = mapped;
        return true;
    }

    private static bool TryReference(
        Guid observationId,
        IReadOnlyDictionary<Guid, TimeSpan> knownTimes,
        out ReasoningEvidenceReference reference)
    {
        if (!knownTimes.TryGetValue(observationId, out var capturedAt))
        {
            reference = null!;
            return false;
        }

        reference = new(observationId, capturedAt);
        return true;
    }

    private static bool SafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool SafeOptionalText(string? value, int maximumLength) =>
        value is null || SafeText(value, maximumLength);
}
