namespace GoalKeeper.Domain;

public sealed record EvidenceEpisodePlan(
    EvidenceEpisode Episode,
    IReadOnlyList<ObservationReference> KeyObservations,
    IReadOnlyList<ObservationReference> ContradictoryObservations);

public sealed class EvidenceEpisodePolicy
{
    public const int DefaultMaximumKeyObservations = 4;
    public const int DefaultMaximumContradictoryObservations = 4;

    public EvidenceEpisodePolicy(
        int maximumKeyObservations = DefaultMaximumKeyObservations,
        int maximumContradictoryObservations = DefaultMaximumContradictoryObservations)
    {
        if (maximumKeyObservations <= 0 || maximumContradictoryObservations < 0)
        {
            throw new DomainRuleViolationException("Evidence reference limits are invalid.");
        }

        MaximumKeyObservations = maximumKeyObservations;
        MaximumContradictoryObservations = maximumContradictoryObservations;
    }

    public int MaximumKeyObservations { get; }

    public int MaximumContradictoryObservations { get; }

    public EvidenceEpisodePlan Create(
        Guid sessionId,
        DeviationReference deviation,
        ObservationReference firstObservation,
        ObservationReference latestObservation,
        IEnumerable<ObservationReference> keyObservations,
        IEnumerable<ObservationReference> contradictoryObservations)
    {
        ArgumentNullException.ThrowIfNull(deviation);
        ArgumentNullException.ThrowIfNull(firstObservation);
        ArgumentNullException.ThrowIfNull(latestObservation);
        ArgumentNullException.ThrowIfNull(keyObservations);
        ArgumentNullException.ThrowIfNull(contradictoryObservations);

        var keys = keyObservations.ToArray();
        var contradictions = contradictoryObservations.ToArray();
        if (keys.Length == 0 || keys.Length > MaximumKeyObservations ||
            contradictions.Length > MaximumContradictoryObservations)
        {
            throw new DomainRuleViolationException("Evidence references exceed the bounded episode policy.");
        }

        var all = new[] { firstObservation, latestObservation }
            .Concat(keys)
            .Concat(contradictions)
            .ToArray();
        if (all.Any(value => value.SessionId != sessionId))
        {
            throw new DomainRuleViolationException("Evidence references must belong to the Focus Session.");
        }

        if (firstObservation.CapturedAt > latestObservation.CapturedAt)
        {
            throw new DomainRuleViolationException("Evidence start must not occur after latest evidence.");
        }

        if (keys.Concat(contradictions).Any(value =>
                value.CapturedAt < firstObservation.CapturedAt ||
                value.CapturedAt > latestObservation.CapturedAt))
        {
            throw new DomainRuleViolationException("Episode references must fall inside the evidence interval.");
        }

        if (keys.Select(value => value.ObservationId)
                .Intersect(contradictions.Select(value => value.ObservationId), StringComparer.Ordinal)
                .Any())
        {
            throw new DomainRuleViolationException(
                "Supporting and contradictory evidence references must be disjoint.");
        }

        var distinct = all
            .GroupBy(value => value.ObservationId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(value => value.CapturedAt)
            .ThenBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToArray();
        return new(
            EvidenceEpisode.Create(sessionId, deviation, distinct),
            Array.AsReadOnly(keys),
            Array.AsReadOnly(contradictions));
    }
}
