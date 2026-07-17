namespace GoalKeeper.Domain;

public sealed record ObservationReference(string ObservationId, Guid SessionId, TimeSpan CapturedAt)
{
    public static ObservationReference Create(string observationId, Guid sessionId, TimeSpan capturedAt) =>
        new(Guard.Required(observationId, nameof(observationId)), sessionId,
            Guard.NonNegative(capturedAt, nameof(capturedAt)));
}

public sealed record DeviationReference(Guid? ListedDeviationId, string? UnlistedDescription)
{
    public bool IsUnlisted => ListedDeviationId is null;

    public static DeviationReference Listed(Guid deviationId) => new(deviationId, null);

    public static DeviationReference Unlisted(string description) =>
        new(null, Guard.Required(description, nameof(description)));
}

public sealed class EvidenceEpisode
{
    private EvidenceEpisode(
        Guid id,
        Guid sessionId,
        DeviationReference deviation,
        IReadOnlyList<ObservationReference> observations)
    {
        Id = id;
        SessionId = sessionId;
        Deviation = deviation;
        Observations = observations;
    }

    public Guid Id { get; }

    public Guid SessionId { get; }

    public DeviationReference Deviation { get; }

    public IReadOnlyList<ObservationReference> Observations { get; }

    public ObservationReference FirstObservation => Observations[0];

    public ObservationReference LatestObservation => Observations[^1];

    public static EvidenceEpisode Create(
        Guid sessionId,
        DeviationReference deviation,
        IEnumerable<ObservationReference> observations)
    {
        var values = observations.ToArray();
        if (values.Length == 0)
        {
            throw new DomainRuleViolationException("An Evidence Episode requires observations.");
        }

        if (values.Any(x => x.SessionId != sessionId))
        {
            throw new DomainRuleViolationException("Evidence observations must belong to the Focus Session.");
        }

        if (values.Select(x => x.ObservationId).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new DomainRuleViolationException("Evidence observations must be unique.");
        }

        if (values.Zip(values.Skip(1)).Any(x => x.First.CapturedAt > x.Second.CapturedAt))
        {
            throw new DomainRuleViolationException("Evidence observations must be ordered.");
        }

        return new(Guid.NewGuid(), sessionId, deviation, Array.AsReadOnly(values));
    }
}

public enum ReasoningDecision { ContinueObserving, BeginRecoveryCheckIn }

public sealed record ReasoningEvaluation(
    Guid Id,
    Guid SessionId,
    long SessionVersion,
    ReasoningDecision Decision,
    EvidenceEpisode? EvidenceEpisode,
    string? Rationale,
    DateTimeOffset EvaluatedAtUtc)
{
    public static ReasoningEvaluation Continue(Guid sessionId, long sessionVersion, IClock clock) =>
        new(Guid.NewGuid(), sessionId, sessionVersion, ReasoningDecision.ContinueObserving, null, null, clock.UtcNow);

    public static ReasoningEvaluation ProposeIntervention(
        Guid sessionId,
        long sessionVersion,
        EvidenceEpisode episode,
        string rationale,
        IClock clock) =>
        new(Guid.NewGuid(), sessionId, sessionVersion, ReasoningDecision.BeginRecoveryCheckIn,
            episode, Guard.Required(rationale, nameof(rationale)), clock.UtcNow);
}

public sealed record Intervention(
    Guid Id,
    ReasoningEvaluation Evaluation,
    TimeSpan AdmittedAt,
    DateTimeOffset AdmittedAtUtc,
    TimeSpan DisputedDuration);

public sealed record BehaviorClarification(
    Guid Id,
    Guid SessionId,
    Guid InterventionId,
    string Explanation,
    DateTimeOffset ClarifiedAtUtc);

public sealed record DeviationOverride(
    Guid Id,
    Guid SessionId,
    DeviationReference Deviation,
    string Reason,
    DateTimeOffset AppliedAtUtc);

public sealed record RecoveryWindow(
    TimeSpan StartedAt,
    TimeSpan EndsAt,
    DeviationReference Deviation);

public enum InterventionHelpfulness { Helpful, Mixed, Unhelpful, NotApplicable }

public sealed record SessionReview(
    Guid Id,
    Guid SessionId,
    bool MeaningfulProgress,
    InterventionHelpfulness Helpfulness,
    string? Note,
    bool MarkGoalComplete,
    DateTimeOffset SubmittedAtUtc);

public sealed class FocusSessionPolicy
{
    public static FocusSessionPolicy Default { get; } = new(
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(30),
        3,
        3);

    public FocusSessionPolicy(
        TimeSpan recoveryWindowDuration,
        TimeSpan responseTimeout,
        TimeSpan monitoringOutageGrace,
        int maximumUnsuccessfulRecoveries,
        int maximumCoachingTurns)
    {
        RecoveryWindowDuration = Guard.Positive(recoveryWindowDuration, nameof(recoveryWindowDuration));
        ResponseTimeout = Guard.Positive(responseTimeout, nameof(responseTimeout));
        MonitoringOutageGrace = Guard.Positive(monitoringOutageGrace, nameof(monitoringOutageGrace));
        if (maximumUnsuccessfulRecoveries <= 0 || maximumCoachingTurns <= 0)
        {
            throw new DomainRuleViolationException("Recovery caps must be positive.");
        }

        MaximumUnsuccessfulRecoveries = maximumUnsuccessfulRecoveries;
        MaximumCoachingTurns = maximumCoachingTurns;
    }

    public TimeSpan RecoveryWindowDuration { get; }

    public TimeSpan ResponseTimeout { get; }

    public TimeSpan MonitoringOutageGrace { get; }

    public int MaximumUnsuccessfulRecoveries { get; }

    public int MaximumCoachingTurns { get; }
}
