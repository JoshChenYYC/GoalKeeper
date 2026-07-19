using System.Text.Json;
using System.Text.Json.Serialization;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Domain;

namespace GoalKeeper.Infrastructure.Reasoning;

internal static class ReasoningWireContracts
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 64,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false)
        }
    };

    public static bool TrySerializeRequest(
        ReasoningRequest request,
        out byte[] serialized)
    {
        serialized = [];
        if (!HasBoundedGraph(request))
        {
            return false;
        }

        try
        {
            var wire = new
            {
                schema_version = ReasoningSchemaVersions.V2,
                session_id = request.SessionId,
                session_version = request.SessionVersion,
                current_state = request.CurrentState,
                contract = new
                {
                    id = request.Contract.Id,
                    goal_title = request.Contract.GoalTitle,
                    goal_description = request.Contract.GoalDescription,
                    target_focus_duration_ticks =
                        request.Contract.TargetFocusDuration.Ticks,
                    deviations = request.Contract.Deviations.Select(value => new
                    {
                        id = value.Id,
                        description = value.Description,
                        observability = value.Observability
                    }),
                    reasoning_mode = request.Contract.ReasoningMode,
                    sensitivity = request.Contract.Sensitivity
                },
                deviation_overrides = request.DeviationOverrides.Select(value => new
                {
                    listed_deviation_id = value.ListedDeviationId,
                    unlisted_description = value.UnlistedDescription,
                    reason = value.Reason
                }),
                active_episodes = request.ActiveEpisodes.Select(Episode),
                historical_episodes = request.HistoricalEpisodes.Select(Episode),
                recovery_summaries = request.RecoverySummaries.Select(value => new
                {
                    intervention_id = value.InterventionId,
                    outcome = value.Outcome,
                    summary = value.Summary,
                    occurred_at_utc = value.OccurredAtUtc
                }),
                prior_decisions = request.PriorDecisions.Select(value => new
                {
                    evaluation_id = value.EvaluationId,
                    decision = value.Decision,
                    accepted = value.Accepted,
                    rejection_reason = value.RejectionReason,
                    evaluated_at_utc = value.EvaluatedAtUtc
                }),
                new_observation = Observation(request.NewObservation),
                recent_observations = request.RecentObservations.Select(Observation)
            };

            serialized = JsonSerializer.SerializeToUtf8Bytes(wire, RequestJsonOptions);
            return serialized.Length <= ReasoningLimits.MaximumSerializedRequestBytes;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                NullReferenceException)
        {
            return false;
        }
    }

    public static bool TryKnownObservationTimes(
        ReasoningRequest request,
        out IReadOnlyDictionary<Guid, TimeSpan> knownTimes)
    {
        knownTimes = new Dictionary<Guid, TimeSpan>();
        if (!HasBoundedGraph(request))
        {
            return false;
        }

        try
        {
            var references = request.RecentObservations
                .Select(value =>
                    new ReasoningEvidenceReference(value.Id, value.CapturedAtMonotonic))
                .Concat(
                    request.ActiveEpisodes
                        .Concat(request.HistoricalEpisodes)
                        .SelectMany(value =>
                            new[]
                            {
                                value.FirstObservation,
                                value.LatestObservation
                            }
                            .Concat(value.KeyObservations)
                            .Concat(value.ContradictoryObservations)));

            var grouped = references
                .Where(value => value.ObservationId != Guid.Empty)
                .GroupBy(value => value.ObservationId)
                .ToArray();
            if (grouped.Any(group =>
                    group.Select(value => value.CapturedAtMonotonic)
                        .Distinct()
                        .Count() != 1))
            {
                return false;
            }

            knownTimes = grouped.ToDictionary(
                group => group.Key,
                group => group.First().CapturedAtMonotonic);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NullReferenceException)
        {
            knownTimes = new Dictionary<Guid, TimeSpan>();
            return false;
        }
    }

    private static bool HasBoundedGraph(ReasoningRequest request) =>
        HasBoundedCollections(request) &&
        request.Contract.Deviations.All(value => value is not null) &&
        request.DeviationOverrides.All(value => value is not null) &&
        request.ActiveEpisodes.All(ValidEpisode) &&
        request.HistoricalEpisodes.All(ValidEpisode) &&
        request.RecoverySummaries.All(value => value is not null) &&
        request.PriorDecisions.All(value => value is not null) &&
        ValidObservation(request.NewObservation) &&
        request.RecentObservations.All(ValidObservation);

    private static bool HasBoundedCollections(ReasoningRequest request) =>
        request.SessionId != Guid.Empty &&
        request.SessionVersion > 0 &&
        Enum.IsDefined(request.CurrentState) &&
        request.Contract is not null &&
        request.NewObservation is not null &&
        request.DeviationOverrides is { Count: <= ReasoningLimits.DeviationOverrides } &&
        request.ActiveEpisodes is { Count: <= ReasoningLimits.ActiveEpisodes } &&
        request.HistoricalEpisodes is { Count: <= ReasoningLimits.HistoricalEpisodes } &&
        request.RecoverySummaries is { Count: <= ReasoningLimits.RecoverySummaries } &&
        request.PriorDecisions is { Count: <= ReasoningLimits.PriorDecisions } &&
        request.RecentObservations is { Count: <= ReasoningLimits.RecentObservations } &&
        request.Contract.Deviations is { Count: <= ReasoningLimits.ContractDeviations };

    private static bool ValidEpisode(ReasoningEpisodeSummary? value) =>
        value is not null &&
        value.FirstObservation is not null &&
        value.LatestObservation is not null &&
        value.KeyObservations is
        {
            Count: <= EvidenceEpisodePolicy.DefaultMaximumKeyObservations
        } &&
        value.KeyObservations.All(reference => reference is not null) &&
        value.ContradictoryObservations is
        {
            Count: <= EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations
        } &&
        value.ContradictoryObservations.All(reference => reference is not null);

    private static bool ValidObservation(ReasoningObservation? value) =>
        value is not null &&
        value.Observation is not null &&
        value.Observation.ImageQuality is not null &&
        ValidLimitations(value.Observation.ImageQuality.Limitations) &&
        value.Observation.PeopleCount is not null &&
        ValidLimitations(value.Observation.PeopleCount.Limitations) &&
        value.Observation.Objects is
        {
            Count: <= ObservationLimits.MaximumObjects
        } &&
        value.Observation.Objects.All(item => item is not null) &&
        value.Observation.VisibleCues is
        {
            Count: <= ObservationLimits.MaximumVisibleCues
        } &&
        value.Observation.VisibleCues.All(cue =>
            cue is not null &&
            ValidLimitations(cue.Limitations));

    private static bool ValidLimitations(IReadOnlyList<string>? values) =>
        values is { Count: <= ObservationLimits.MaximumLimitations } &&
        values.All(value => value is not null);

    private static object Episode(ReasoningEpisodeSummary value) => new
    {
        key = value.Key,
        status = value.Status,
        listed_deviation_id = value.ListedDeviationId,
        unlisted_description = value.UnlistedDescription,
        first_observation = Reference(value.FirstObservation),
        latest_observation = Reference(value.LatestObservation),
        key_observations = value.KeyObservations.Select(Reference),
        contradictory_observations =
            value.ContradictoryObservations.Select(Reference),
        summary = value.Summary
    };

    private static object Reference(ReasoningEvidenceReference value) => new
    {
        observation_id = value.ObservationId,
        captured_at_monotonic_ticks = value.CapturedAtMonotonic.Ticks
    };

    private static object Observation(ReasoningObservation value) => new
    {
        id = value.Id,
        session_version = value.SessionVersion,
        captured_at_utc = value.CapturedAtUtc,
        captured_at_monotonic_ticks = value.CapturedAtMonotonic.Ticks,
        observation = new
        {
            schema_version = value.Observation.SchemaVersion,
            image_quality = new
            {
                value = value.Observation.ImageQuality.Value,
                limitations = value.Observation.ImageQuality.Limitations
            },
            people_count = new
            {
                status = value.Observation.PeopleCount.Status,
                value = value.Observation.PeopleCount.Value,
                support = value.Observation.PeopleCount.Support,
                limitations = value.Observation.PeopleCount.Limitations
            },
            objects = value.Observation.Objects,
            visible_cues = value.Observation.VisibleCues.Select(cue => new
            {
                subject = cue.Subject,
                kind = cue.Kind,
                state = cue.State,
                support = cue.Support,
                description = cue.Description,
                visual_basis = cue.VisualBasis,
                limitations = cue.Limitations
            })
        }
    };
}

internal sealed record ReasoningResponseWire(
    int SchemaVersion,
    Guid SessionId,
    long SessionVersion,
    FocusSessionState CurrentState,
    Guid TriggerObservationId,
    ReasoningDecision Decision,
    ReasoningInterventionWire? Intervention,
    IReadOnlyList<ReasoningEpisodeWire>? EpisodeUpdates);

internal sealed record ReasoningInterventionWire(
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    Guid FirstObservationId,
    Guid LatestObservationId,
    IReadOnlyList<Guid>? KeyObservationIds,
    IReadOnlyList<Guid>? ContradictoryObservationIds,
    string? Rationale,
    string? AccountabilityMessage);

internal sealed record ReasoningEpisodeWire(
    string? Key,
    ReasoningEpisodeStatus Status,
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    Guid FirstObservationId,
    Guid LatestObservationId,
    IReadOnlyList<Guid>? KeyObservationIds,
    IReadOnlyList<Guid>? ContradictoryObservationIds,
    string? Summary);
