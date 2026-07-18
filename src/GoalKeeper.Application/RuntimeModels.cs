using GoalKeeper.Domain;

namespace GoalKeeper.Application;

public enum SnapshotProcessingStatus
{
    Captured,
    Superseded,
    Observed,
    Stale,
    AgentError
}

public sealed record FocusSessionRuntimeView(
    Guid Id,
    Guid GoalId,
    Guid ContractId,
    FocusSessionState State,
    long Version,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? ArtifactDirectory,
    FocusSessionRuntimeSnapshot Runtime);

public sealed record SessionHistoryItem(
    Guid Id,
    Guid GoalId,
    string GoalTitle,
    FocusSessionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    EndedEarlyReason? EndedEarlyReason,
    long Version);

public sealed record RuntimeAuditWrite(
    DateTimeOffset OccurredAtUtc,
    string Event,
    FocusSessionState? FromState,
    FocusSessionState? ToState,
    string PayloadJson);

public sealed record RuntimeMutation(
    long ExpectedVersion,
    FocusSessionRuntimeSnapshot Runtime,
    IReadOnlyList<RuntimeAuditWrite> AuditEvents)
{
    public DateTimeOffset? GoalCompletedAtUtc { get; init; }
}

public sealed record SnapshotWrite(
    Guid Id,
    Guid SessionId,
    int Sequence,
    DateTimeOffset CapturedAtUtc,
    TimeSpan CapturedAtMonotonic,
    string ImagePath,
    long StoredBytes,
    SnapshotProcessingStatus Status,
    long SessionVersion);

public sealed record SnapshotView(
    Guid Id,
    Guid SessionId,
    int Sequence,
    DateTimeOffset CapturedAtUtc,
    TimeSpan CapturedAtMonotonic,
    string ImagePath,
    long StoredBytes,
    SnapshotProcessingStatus Status,
    long SessionVersion);

public sealed record ObservationWrite(
    Guid Id,
    Guid SessionId,
    Guid SnapshotId,
    long SessionVersion,
    DateTimeOffset ProcessedAtUtc,
    int SchemaVersion,
    string DocumentJson);

public sealed record ObservationView(
    Guid Id,
    Guid SessionId,
    Guid SnapshotId,
    long SessionVersion,
    DateTimeOffset CapturedAtUtc,
    TimeSpan CapturedAtMonotonic,
    DateTimeOffset ProcessedAtUtc,
    int SchemaVersion,
    string DocumentJson);

public sealed record EvidenceObservationWrite(
    Guid ObservationId,
    int Sequence);

public sealed record EvidenceEpisodeWrite(
    Guid Id,
    Guid SessionId,
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    DateTimeOffset CreatedAtUtc,
    string DocumentJson,
    IReadOnlyList<EvidenceObservationWrite> Observations);

public sealed record ReasoningEvaluationWrite(
    Guid Id,
    Guid SessionId,
    long SessionVersion,
    ReasoningDecision Decision,
    DateTimeOffset EvaluatedAtUtc,
    int SchemaVersion,
    string DocumentJson);

public sealed record InterventionWrite(
    Guid Id,
    Guid SessionId,
    Guid EvaluationId,
    Guid EvidenceEpisodeId,
    DateTimeOffset AdmittedAtUtc,
    TimeSpan DisputedDuration,
    string Status);

public sealed record ReasoningCommitRequest(
    long ExpectedSessionVersion,
    FocusSessionRuntimeSnapshot ProposedRuntime,
    ReasoningEvaluationWrite Evaluation,
    EvidenceEpisodeWrite? EvidenceEpisode,
    InterventionWrite? Intervention,
    IReadOnlyList<RuntimeAuditWrite> AuditEvents);

public sealed record ReasoningCommitResult(
    bool Applied,
    string? RejectionReason,
    long CurrentSessionVersion);

public sealed record ReasoningEvaluationView(
    Guid Id,
    Guid SessionId,
    long SessionVersion,
    ReasoningDecision Decision,
    DateTimeOffset EvaluatedAtUtc,
    int SchemaVersion,
    string DocumentJson,
    bool Accepted,
    string? RejectionReason);

public sealed record RecoveryTurnWrite(
    Guid Id,
    Guid SessionId,
    Guid InterventionId,
    int TurnNumber,
    string Outcome,
    string? Transcript,
    DateTimeOffset OccurredAtUtc);

public sealed record RecoveryTurnView(
    Guid Id,
    Guid SessionId,
    Guid InterventionId,
    int TurnNumber,
    string Outcome,
    string? Transcript,
    DateTimeOffset OccurredAtUtc);

public sealed record RecoveryCommitRequest(
    long ExpectedSessionVersion,
    FocusSessionRuntimeSnapshot ProposedRuntime,
    RecoveryTurnWrite Turn,
    IReadOnlyList<RuntimeAuditWrite> AuditEvents);

public sealed record RecoveryCommitResult(
    bool Applied,
    string? RejectionReason,
    long CurrentSessionVersion);
