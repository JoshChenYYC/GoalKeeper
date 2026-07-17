namespace GoalKeeper.Domain;

public sealed record ObservationReference(string ObservationId, Guid SessionId, TimeSpan CapturedAt)
{
    public static ObservationReference Create(string observationId, Guid sessionId, TimeSpan capturedAt) =>
        new(Guard.Required(observationId, nameof(observationId)), Guard.Identifier(sessionId, nameof(sessionId)),
            Guard.NonNegative(capturedAt, nameof(capturedAt)));
}

public sealed record DeviationReference(Guid? ListedDeviationId, string? UnlistedDescription)
{
    public bool IsUnlisted => ListedDeviationId is null;

    public static DeviationReference Listed(Guid deviationId) =>
        new(Guard.Identifier(deviationId, nameof(deviationId)), null);

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
        Guard.Identifier(sessionId, nameof(sessionId));
        ValidateDeviation(deviation);
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

        foreach (var value in values)
        {
            _ = ObservationReference.Create(value.ObservationId, value.SessionId, value.CapturedAt);
        }

        return new(Guid.NewGuid(), sessionId, deviation, Array.AsReadOnly(values));
    }

    internal static EvidenceEpisode Rehydrate(
        Guid id,
        Guid sessionId,
        DeviationReference deviation,
        IEnumerable<ObservationReference> observations)
    {
        Guard.Identifier(id, nameof(id));
        var created = Create(sessionId, deviation, observations);
        return new(id, created.SessionId, created.Deviation, created.Observations);
    }

    internal static void ValidateDeviation(DeviationReference deviation)
    {
        if (deviation.ListedDeviationId is { } listed)
        {
            Guard.Identifier(listed, nameof(deviation.ListedDeviationId));
            if (!string.IsNullOrWhiteSpace(deviation.UnlistedDescription))
            {
                throw new DomainRuleViolationException("A listed Deviation cannot include an unlisted description.");
            }
        }
        else
        {
            _ = Guard.Required(deviation.UnlistedDescription, nameof(deviation.UnlistedDescription));
        }
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
        Create(
            Guid.NewGuid(),
            sessionId,
            sessionVersion,
            ReasoningDecision.ContinueObserving,
            null,
            null,
            clock.UtcNow);

    public static ReasoningEvaluation ProposeIntervention(
        Guid sessionId,
        long sessionVersion,
        EvidenceEpisode episode,
        string rationale,
        IClock clock) =>
        Create(
            Guid.NewGuid(),
            sessionId,
            sessionVersion,
            ReasoningDecision.BeginRecoveryCheckIn,
            episode,
            rationale,
            clock.UtcNow);

    internal static ReasoningEvaluation Rehydrate(
        Guid id,
        Guid sessionId,
        long sessionVersion,
        ReasoningDecision decision,
        EvidenceEpisode? evidenceEpisode,
        string? rationale,
        DateTimeOffset evaluatedAtUtc) =>
        Create(id, sessionId, sessionVersion, decision, evidenceEpisode, rationale, evaluatedAtUtc);

    private static ReasoningEvaluation Create(
        Guid id,
        Guid sessionId,
        long sessionVersion,
        ReasoningDecision decision,
        EvidenceEpisode? evidenceEpisode,
        string? rationale,
        DateTimeOffset evaluatedAtUtc)
    {
        Guard.Identifier(id, nameof(id));
        Guard.Identifier(sessionId, nameof(sessionId));
        Guard.DefinedEnum(decision, nameof(decision));
        if (sessionVersion <= 0)
        {
            throw new DomainRuleViolationException("The session version must be positive.");
        }

        if (decision == ReasoningDecision.ContinueObserving)
        {
            if (evidenceEpisode is not null || !string.IsNullOrWhiteSpace(rationale))
            {
                throw new DomainRuleViolationException("Continue-observing cannot contain Intervention evidence.");
            }
        }
        else if (evidenceEpisode is null || evidenceEpisode.SessionId != sessionId)
        {
            throw new DomainRuleViolationException("An Intervention evaluation requires same-session evidence.");
        }
        else
        {
            rationale = Guard.Required(rationale, nameof(rationale));
        }

        return new(id, sessionId, sessionVersion, decision, evidenceEpisode, rationale, evaluatedAtUtc);
    }
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

    internal FocusSessionPolicySnapshot CreateSnapshot() =>
        new(
            RecoveryWindowDuration,
            ResponseTimeout,
            MonitoringOutageGrace,
            MaximumUnsuccessfulRecoveries,
            MaximumCoachingTurns);

    internal static FocusSessionPolicy Rehydrate(FocusSessionPolicySnapshot snapshot) =>
        new(
            snapshot.RecoveryWindowDuration,
            snapshot.ResponseTimeout,
            snapshot.MonitoringOutageGrace,
            snapshot.MaximumUnsuccessfulRecoveries,
            snapshot.MaximumCoachingTurns);
}
