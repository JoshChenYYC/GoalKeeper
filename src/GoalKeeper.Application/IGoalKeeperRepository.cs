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

    Task<StorageUsageView> GetStorageUsageAsync(Guid? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<ApplicationSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
}

public sealed class PersistenceConflictException : InvalidOperationException
{
    public PersistenceConflictException(string message) : base(message) { }
}
