namespace GoalKeeper.Domain;

public sealed record FocusSessionPolicySnapshot(
    TimeSpan RecoveryWindowDuration,
    TimeSpan ResponseTimeout,
    TimeSpan MonitoringOutageGrace,
    int MaximumUnsuccessfulRecoveries,
    int MaximumCoachingTurns);

public sealed record ObservationReferenceSnapshot(
    string ObservationId,
    Guid SessionId,
    TimeSpan CapturedAt);

public sealed record DeviationReferenceSnapshot(
    Guid? ListedDeviationId,
    string? UnlistedDescription);

public sealed record EvidenceEpisodeSnapshot(
    Guid Id,
    Guid SessionId,
    DeviationReferenceSnapshot Deviation,
    IReadOnlyList<ObservationReferenceSnapshot> Observations);

public sealed record ReasoningEvaluationSnapshot(
    Guid Id,
    Guid SessionId,
    long SessionVersion,
    ReasoningDecision Decision,
    EvidenceEpisodeSnapshot? EvidenceEpisode,
    string? Rationale,
    DateTimeOffset EvaluatedAtUtc)
{
    public string? AccountabilityMessage { get; init; }
}

public sealed record InterventionSnapshot(
    Guid Id,
    ReasoningEvaluationSnapshot Evaluation,
    TimeSpan AdmittedAt,
    DateTimeOffset AdmittedAtUtc,
    TimeSpan DisputedDuration);

public sealed record RecoveryWindowSnapshot(
    TimeSpan StartedAt,
    TimeSpan EndsAt,
    DeviationReferenceSnapshot Deviation);

public sealed record BehaviorClarificationSnapshot(
    Guid Id,
    Guid SessionId,
    Guid InterventionId,
    string Explanation,
    DateTimeOffset ClarifiedAtUtc);

public sealed record DeviationOverrideSnapshot(
    Guid Id,
    Guid SessionId,
    DeviationReferenceSnapshot Deviation,
    string Reason,
    DateTimeOffset AppliedAtUtc);

public sealed record SessionReviewSnapshot(
    Guid Id,
    Guid SessionId,
    bool MeaningfulProgress,
    InterventionHelpfulness Helpfulness,
    string? Note,
    bool MarkGoalComplete,
    DateTimeOffset SubmittedAtUtc);

public sealed record FocusSessionRuntimeSnapshot(
    Guid Id,
    Guid GoalId,
    Guid ContractId,
    FocusSessionState State,
    long Version,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ProjectedEndUtc,
    DateTimeOffset? EndedAtUtc,
    EndedEarlyReason? EndedEarlyReason,
    FocusTimerSnapshot Timer,
    int NextBreakIndex,
    TimeSpan? BreakEndsAt,
    InterventionSnapshot? ActiveIntervention,
    RecoveryWindowSnapshot? CurrentRecoveryWindow,
    int ConsecutiveUnsuccessfulRecoveries,
    bool RequiresFinalEscalation,
    TimeSpan? ResponseDeadline,
    TimeSpan? MonitoringUnavailableAt,
    TimeSpan? MonitoringDeadline,
    IReadOnlyList<BehaviorClarificationSnapshot> Clarifications,
    IReadOnlyList<DeviationOverrideSnapshot> DeviationOverrides,
    SessionReviewSnapshot? Review,
    FocusSessionPolicySnapshot Policy);
