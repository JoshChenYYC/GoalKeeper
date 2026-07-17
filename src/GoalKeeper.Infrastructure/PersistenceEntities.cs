namespace GoalKeeper.Infrastructure;

public sealed class GoalEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
    public List<ContractEntity> Contracts { get; set; } = [];
    public List<FocusSessionEntity> Sessions { get; set; } = [];
}

public sealed class DeviationProfileEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
    public List<DeviationEntity> Deviations { get; set; } = [];
}

public sealed class DeviationEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public DeviationProfileEntity Profile { get; set; } = null!;
    public string Description { get; set; } = "";
    public string Observability { get; set; } = "Observable";
    public int SortOrder { get; set; }
}

public sealed class ContractEntity
{
    public Guid Id { get; set; }
    public Guid GoalId { get; set; }
    public GoalEntity Goal { get; set; } = null!;
    public string GoalTitle { get; set; } = "";
    public string? GoalDescription { get; set; }
    public long TargetFocusTicks { get; set; }
    public Guid DeviationProfileId { get; set; }
    public string DeviationProfileName { get; set; } = "";
    public string ReasoningMode { get; set; } = "ProfileOnly";
    public string Sensitivity { get; set; } = "Balanced";
    public DateTimeOffset ConfirmedAtUtc { get; set; }
    public List<ContractBreakEntity> Breaks { get; set; } = [];
    public List<ContractDeviationEntity> Deviations { get; set; } = [];
    public SessionSetupEntity? Setup { get; set; }

    public static ContractEntity CreateForTest(Guid goalId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        GoalId = goalId,
        GoalTitle = "Store",
        TargetFocusTicks = TimeSpan.FromMinutes(25).Ticks,
        DeviationProfileId = Guid.NewGuid(),
        DeviationProfileName = "Default",
        ConfirmedAtUtc = now
    };
}

public sealed class ContractBreakEntity
{
    public long Id { get; set; }
    public Guid ContractId { get; set; }
    public ContractEntity Contract { get; set; } = null!;
    public long ActiveFocusOffsetTicks { get; set; }
    public long DurationTicks { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ContractDeviationEntity
{
    public long Id { get; set; }
    public Guid ContractId { get; set; }
    public ContractEntity Contract { get; set; } = null!;
    public Guid DeviationId { get; set; }
    public string Description { get; set; } = "";
    public string Observability { get; set; } = "Observable";
    public int SortOrder { get; set; }
}

public sealed class SessionSetupEntity
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public ContractEntity Contract { get; set; } = null!;
    public string Status { get; set; } = "Ready";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class FocusSessionEntity
{
    public Guid Id { get; set; }
    public Guid GoalId { get; set; }
    public GoalEntity Goal { get; set; } = null!;
    public Guid ContractId { get; set; }
    public ContractEntity Contract { get; set; } = null!;
    public string State { get; set; } = "Focusing";
    public int? ActiveSlot { get; set; } = 1;
    public long Version { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ProjectedEndUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public long AccumulatedFocusTicks { get; set; }
    public string? EndReason { get; set; }
    public string RuntimeSnapshotJson { get; set; } = "{}";
    public string? ArtifactDirectory { get; set; }
    public List<SnapshotEntity> Snapshots { get; set; } = [];
    public List<ObservationEntity> Observations { get; set; } = [];
    public List<ReasoningEvaluationEntity> ReasoningEvaluations { get; set; } = [];
    public List<EvidenceEpisodeEntity> EvidenceEpisodes { get; set; } = [];
    public List<InterventionEntity> Interventions { get; set; } = [];
    public List<RecoveryTurnEntity> RecoveryTurns { get; set; } = [];
    public List<DeviationOverrideEntity> DeviationOverrides { get; set; } = [];
    public List<AuditEventEntity> AuditEvents { get; set; } = [];
    public SessionReviewEntity? Review { get; set; }

    public static FocusSessionEntity CreateForTest(Guid goalId, Guid contractId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        GoalId = goalId,
        ContractId = contractId,
        StartedAtUtc = now,
        ProjectedEndUtc = now + TimeSpan.FromMinutes(25),
        Version = 1
    };
}

public sealed class SnapshotEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public int Sequence { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public long CapturedAtMonotonicTicks { get; set; }
    public string ImagePath { get; set; } = "";
    public long StoredBytes { get; set; }
    public string ProcessingStatus { get; set; } = "Captured";
    public long SessionVersion { get; set; }
    public ObservationEntity? Observation { get; set; }

    public static SnapshotEntity CreateForTest(Guid sessionId, int sequence, long bytes) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        Sequence = sequence,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        ImagePath = $"{sequence}.jpg",
        StoredBytes = bytes,
        SessionVersion = 1
    };
}

public sealed class ObservationEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid SnapshotId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public SnapshotEntity Snapshot { get; set; } = null!;
    public long SessionVersion { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public int SchemaVersion { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public List<EvidenceObservationReferenceEntity> EvidenceReferences { get; set; } = [];
}

public sealed class ReasoningEvaluationEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public long SessionVersion { get; set; }
    public string Decision { get; set; } = "ContinueObserving";
    public DateTimeOffset EvaluatedAtUtc { get; set; }
    public int SchemaVersion { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public bool Accepted { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? EvidenceEpisodeId { get; set; }
    public EvidenceEpisodeEntity? EvidenceEpisode { get; set; }
    public InterventionEntity? Intervention { get; set; }
}

public sealed class EvidenceEpisodeEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public Guid? ListedDeviationId { get; set; }
    public string? UnlistedDescription { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public ReasoningEvaluationEntity? Evaluation { get; set; }
    public List<EvidenceObservationReferenceEntity> ObservationReferences { get; set; } = [];
    public InterventionEntity? Intervention { get; set; }
}

public sealed class EvidenceObservationReferenceEntity
{
    public Guid EvidenceEpisodeId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ObservationId { get; set; }
    public int Sequence { get; set; }
    public EvidenceEpisodeEntity EvidenceEpisode { get; set; } = null!;
    public ObservationEntity Observation { get; set; } = null!;
}

public sealed class InterventionEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public Guid EvaluationId { get; set; }
    public ReasoningEvaluationEntity Evaluation { get; set; } = null!;
    public Guid EvidenceEpisodeId { get; set; }
    public EvidenceEpisodeEntity EvidenceEpisode { get; set; } = null!;
    public DateTimeOffset AdmittedAtUtc { get; set; }
    public long DisputedTicks { get; set; }
    public string Status { get; set; } = "Active";
    public List<RecoveryTurnEntity> RecoveryTurns { get; set; } = [];
}

public sealed class RecoveryTurnEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public Guid InterventionId { get; set; }
    public InterventionEntity Intervention { get; set; } = null!;
    public int TurnNumber { get; set; }
    public string Outcome { get; set; } = "Unclear";
    public string? Transcript { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class DeviationOverrideEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public Guid? ListedDeviationId { get; set; }
    public string? UnlistedDescription { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset AppliedAtUtc { get; set; }
}

public sealed class SessionReviewEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public bool MeaningfulProgress { get; set; }
    public string Helpfulness { get; set; } = "NotApplicable";
    public string? Note { get; set; }
    public bool MarkGoalComplete { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public FocusSessionEntity Session { get; set; } = null!;
    public long SessionVersion { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Event { get; set; } = "";
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class ApplicationSettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
