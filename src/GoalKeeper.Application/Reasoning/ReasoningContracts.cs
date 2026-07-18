using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using GoalKeeper.Application.Perception;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Reasoning;

public static class ReasoningSchemaVersions
{
    public const int V1 = 1;
}

public static class ReasoningLimits
{
    public const int RecentObservations = 12;
    public const int PriorDecisions = 6;
    public const int ActiveEpisodes = 4;
    public const int HistoricalEpisodes = 8;
    public const int RecoverySummaries = 4;
    public const int DeviationOverrides = 16;
    public const int ContractDeviations = 64;
    public const int MaximumTextLength = 320;
    public const int MaximumRationaleLength = 640;
    public const int MaximumSerializedRequestBytes = 512 * 1024;
}

public sealed record ReasoningContractDeviation(
    Guid Id,
    string Description,
    VisualObservability Observability);

public sealed record ReasoningContractSummary(
    Guid Id,
    string GoalTitle,
    string? GoalDescription,
    TimeSpan TargetFocusDuration,
    IReadOnlyList<ReasoningContractDeviation> Deviations,
    ReasoningMode ReasoningMode,
    Sensitivity Sensitivity);

public sealed record ReasoningDeviationOverride(
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    string Reason);

public sealed record ReasoningObservation(
    Guid Id,
    long SessionVersion,
    DateTimeOffset CapturedAtUtc,
    TimeSpan CapturedAtMonotonic,
    Observation Observation);

[JsonConverter(typeof(JsonStringEnumConverter<ReasoningEpisodeStatus>))]
public enum ReasoningEpisodeStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("historical")]
    Historical
}

public sealed record ReasoningEvidenceReference(Guid ObservationId, TimeSpan CapturedAtMonotonic);

public sealed record ReasoningEpisodeSummary(
    string Key,
    ReasoningEpisodeStatus Status,
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    ReasoningEvidenceReference FirstObservation,
    ReasoningEvidenceReference LatestObservation,
    IReadOnlyList<ReasoningEvidenceReference> KeyObservations,
    IReadOnlyList<ReasoningEvidenceReference> ContradictoryObservations,
    string Summary);

public sealed record ReasoningRecoverySummary(
    Guid InterventionId,
    string Outcome,
    string Summary,
    DateTimeOffset OccurredAtUtc);

public sealed record ReasoningDecisionSummary(
    Guid EvaluationId,
    ReasoningDecision Decision,
    bool Accepted,
    string? RejectionReason,
    DateTimeOffset EvaluatedAtUtc);

public sealed class ReasoningRequest
{
    public ReasoningRequest(
        Guid sessionId,
        long sessionVersion,
        FocusSessionState currentState,
        ReasoningContractSummary contract,
        IEnumerable<ReasoningDeviationOverride> deviationOverrides,
        IEnumerable<ReasoningEpisodeSummary> activeEpisodes,
        IEnumerable<ReasoningEpisodeSummary> historicalEpisodes,
        IEnumerable<ReasoningRecoverySummary> recoverySummaries,
        IEnumerable<ReasoningDecisionSummary> priorDecisions,
        ReasoningObservation newObservation,
        IEnumerable<ReasoningObservation> recentObservations)
    {
        SessionId = sessionId;
        SessionVersion = sessionVersion;
        CurrentState = currentState;
        Contract = contract;
        DeviationOverrides = Copy(deviationOverrides);
        ActiveEpisodes = Copy(activeEpisodes);
        HistoricalEpisodes = Copy(historicalEpisodes);
        RecoverySummaries = Copy(recoverySummaries);
        PriorDecisions = Copy(priorDecisions);
        NewObservation = newObservation;
        RecentObservations = Copy(recentObservations);
    }

    public Guid SessionId { get; }

    public long SessionVersion { get; }

    public FocusSessionState CurrentState { get; }

    public ReasoningContractSummary Contract { get; }

    public IReadOnlyList<ReasoningDeviationOverride> DeviationOverrides { get; }

    public IReadOnlyList<ReasoningEpisodeSummary> ActiveEpisodes { get; }

    public IReadOnlyList<ReasoningEpisodeSummary> HistoricalEpisodes { get; }

    public IReadOnlyList<ReasoningRecoverySummary> RecoverySummaries { get; }

    public IReadOnlyList<ReasoningDecisionSummary> PriorDecisions { get; }

    public ReasoningObservation NewObservation { get; }

    public IReadOnlyList<ReasoningObservation> RecentObservations { get; }

    private static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values) =>
        new(values.ToArray());
}

public sealed record ReasoningInterventionProposal(
    Guid? ListedDeviationId,
    string? UnlistedDescription,
    Guid FirstObservationId,
    Guid LatestObservationId,
    IReadOnlyList<Guid> KeyObservationIds,
    IReadOnlyList<Guid> ContradictoryObservationIds,
    string Rationale);

public sealed record ReasoningProposal(
    Guid SessionId,
    long SessionVersion,
    FocusSessionState CurrentState,
    Guid TriggerObservationId,
    ReasoningDecision Decision,
    ReasoningInterventionProposal? Intervention,
    IReadOnlyList<ReasoningEpisodeSummary> EpisodeUpdates);

public sealed record ReasoningMetadata(
    string Provider,
    string Model,
    string PromptVersion,
    int SchemaVersion,
    TimeSpan Latency,
    string RequestId);

public enum ReasoningFailureCategory
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

public abstract record ReasoningResult(ReasoningMetadata Metadata);

public sealed record ReasoningSuccess(ReasoningProposal Proposal, ReasoningMetadata Metadata)
    : ReasoningResult(Metadata);

public sealed record ReasoningInvalid(
    IReadOnlyList<string> ValidationReasons,
    ReasoningMetadata Metadata)
    : ReasoningResult(Metadata);

public sealed record ReasoningFailure(
    ReasoningFailureCategory Category,
    ReasoningMetadata Metadata)
    : ReasoningResult(Metadata);

public interface IReasoningPort
{
    Task<ReasoningResult> EvaluateAsync(
        ReasoningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ReasoningEvaluationInput(
    SessionContractView Contract,
    FocusSessionRuntimeSnapshot Runtime,
    ObservationView NewObservation,
    IReadOnlyList<ReasoningRecoverySummary> RecoverySummaries);

public sealed record ReasoningOrchestrationResult(
    Guid EvaluationId,
    bool Accepted,
    ReasoningDecision Decision,
    string? RejectionReason,
    EvidenceEpisode? EvidenceEpisode,
    ReasoningRequest? Request,
    ReasoningFailureCategory? FailureCategory = null);

public sealed record ReasoningAdmissionContext(
    ReasoningEvaluation Evaluation,
    ReasoningRequest Request);

public sealed class ReasoningAdmissionPlan
{
    public ReasoningAdmissionPlan(
        FocusSessionRuntimeSnapshot runtime,
        IEnumerable<RuntimeAuditWrite>? auditEvents = null)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        AuditEvents = Array.AsReadOnly((auditEvents ?? []).ToArray());
    }

    public FocusSessionRuntimeSnapshot Runtime { get; }

    public IReadOnlyList<RuntimeAuditWrite> AuditEvents { get; }
}

public delegate Task<ReasoningAdmissionPlan> ReasoningAdmissionHandler(
    ReasoningAdmissionContext context,
    CancellationToken cancellationToken);
