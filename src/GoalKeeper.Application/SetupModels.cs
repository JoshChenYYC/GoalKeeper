using GoalKeeper.Domain;

namespace GoalKeeper.Application;

public sealed record GoalView(
    Guid Id,
    string Title,
    string? Description,
    GoalStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Version);

public sealed record DeviationInput(string Description, VisualObservability Observability);

public sealed record DeviationView(Guid Id, string Description, VisualObservability Observability);

public sealed record DeviationProfileView(
    Guid Id,
    string Name,
    IReadOnlyList<DeviationView> Deviations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

public sealed record ScheduledBreakInput(TimeSpan ActiveFocusOffset, TimeSpan Duration);

public sealed record SessionContractDraft(
    Guid GoalId,
    string GoalTitle,
    string? GoalDescription,
    TimeSpan TargetFocusDuration,
    IReadOnlyList<ScheduledBreakInput> ScheduledBreaks,
    Guid DeviationProfileId,
    string DeviationProfileName,
    IReadOnlyList<DeviationView> Deviations,
    ReasoningMode ReasoningMode,
    Sensitivity Sensitivity);

public sealed record SessionContractView(
    Guid Id,
    Guid GoalId,
    string GoalTitle,
    string? GoalDescription,
    TimeSpan TargetFocusDuration,
    IReadOnlyList<ScheduledBreakInput> ScheduledBreaks,
    Guid DeviationProfileId,
    string DeviationProfileName,
    IReadOnlyList<DeviationView> Deviations,
    ReasoningMode ReasoningMode,
    Sensitivity Sensitivity,
    DateTimeOffset ConfirmedAtUtc);

public enum SessionSetupStatus { Ready, Started, Cancelled }

public sealed record SessionSetupView(
    Guid Id,
    SessionSetupStatus Status,
    SessionContractView Contract,
    DateTimeOffset CreatedAtUtc,
    long Version);

public sealed record StorageUsageView(long SnapshotBytes, int SnapshotCount);

public sealed record ApplicationSettingsView(
    TimeSpan RecoveryWindow,
    TimeSpan ResponseTimeout,
    TimeSpan TechnicalOutageGrace,
    int MaximumUnsuccessfulRecoveries,
    int MaximumCoachingTurns)
{
    public static ApplicationSettingsView Default { get; } = new(
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(30),
        3,
        3);
}
