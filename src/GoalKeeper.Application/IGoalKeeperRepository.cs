using GoalKeeper.Domain;

namespace GoalKeeper.Application;

public interface IGoalKeeperRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalView>> ListGoalsAsync(CancellationToken cancellationToken = default);

    Task<GoalView?> GetGoalAsync(Guid id, CancellationToken cancellationToken = default);

    Task<GoalView> CreateGoalAsync(string title, string? description, DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    Task<GoalView> UpdateGoalAsync(Guid id, long expectedVersion, string title, string? description,
        DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    Task DeleteGoalAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default);

    Task<DeviationProfileView?> GetProfileAsync(CancellationToken cancellationToken = default);

    Task<DeviationProfileView> SaveProfileAsync(
        string name,
        IReadOnlyList<DeviationInput> deviations,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<SessionContractView?> GetLatestContractAsync(Guid goalId, CancellationToken cancellationToken = default);

    Task<SessionSetupView> CreateReadySetupAsync(
        SessionContractDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<SessionSetupView?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SessionSetupView> TransitionSetupAsync(
        Guid id,
        long expectedVersion,
        SessionSetupStatus targetStatus,
        CancellationToken cancellationToken = default);

    Task<FocusSessionRuntimeView> StartSessionAsync(
        Guid setupId,
        long expectedSetupVersion,
        FocusSessionRuntimeSnapshot initialRuntime,
        string? artifactDirectory = null,
        CancellationToken cancellationToken = default);

    Task<FocusSessionRuntimeView?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<FocusSessionRuntimeView> UpdateSessionAsync(
        Guid id,
        RuntimeMutation mutation,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionHistoryItem>> ListSessionHistoryAsync(
        Guid? goalId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<SnapshotView> AddSnapshotAsync(
        SnapshotWrite snapshot,
        CancellationToken cancellationToken = default);

    Task<SnapshotView> UpdateSnapshotStatusAsync(
        Guid sessionId,
        Guid snapshotId,
        SnapshotProcessingStatus status,
        CancellationToken cancellationToken = default);

    Task<ObservationView> AddObservationAsync(
        ObservationWrite observation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObservationView>> GetRecentObservationsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ReasoningCommitResult> CommitReasoningEvaluationAsync(
        ReasoningCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<ReasoningCommitResult> AppendRejectedReasoningEvaluationAsync(
        ReasoningEvaluationWrite evaluation,
        string rejectionReason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReasoningEvaluationView>> GetRecentReasoningEvaluationsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AddRecoveryTurnAsync(
        RecoveryTurnWrite turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecoveryTurnView>> GetRecoveryTurnsAsync(
        Guid sessionId,
        Guid interventionId,
        CancellationToken cancellationToken = default);

    Task<RecoveryCommitResult> CommitRecoveryTurnAsync(
        RecoveryCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<StorageUsageView> GetStorageUsageAsync(Guid? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<ApplicationSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
}

public sealed class PersistenceConflictException : InvalidOperationException
{
    public PersistenceConflictException(string message) : base(message) { }
}
