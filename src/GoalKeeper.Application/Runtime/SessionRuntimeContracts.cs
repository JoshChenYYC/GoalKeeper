using GoalKeeper.Application.Monitoring;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Runtime;

public enum SessionRuntimeControllerState
{
    Idle,
    Preflight,
    Running
}

public sealed record SessionRuntimeStatus(
    SessionRuntimeControllerState ControllerState,
    Guid? SetupId,
    Guid? SessionId,
    FocusSessionState? State,
    long? Version,
    DateTimeOffset? ProjectedEndUtc,
    bool HasActiveWorker,
    string? TechnicalFailure);

public sealed record SessionPreflightAttempt(
    Guid SetupId,
    PreflightStatus Status,
    PreflightRejection Rejection,
    bool CanRetry,
    PreflightPreview? Preview);

public sealed record PreflightPreview(
    byte[] Jpeg,
    int PixelWidth,
    int PixelHeight);

public sealed record SessionStartResult(
    PreflightStatus PreflightStatus,
    PreflightRejection Rejection,
    FocusSessionRuntimeView? Session);

public sealed record SessionLiveStatus(
    Guid SessionId,
    string GoalTitle,
    FocusSessionState State,
    TimeSpan FocusElapsed,
    TimeSpan FocusTarget,
    TimeSpan FocusRemaining,
    TimeSpan? StateCountdown,
    DateTimeOffset ProjectedEndUtc,
    bool MonitoringActive,
    string? TechnicalFailure,
    string? RecoveryPrompt,
    bool CanCompleteGoal,
    bool CanEndEarly,
    bool CanSubmitRecovery,
    bool CanReturnToRecovery,
    bool IsTerminal,
    EndedEarlyReason? EndedEarlyReason);

public interface ISessionRuntimeScheduler
{
    Task WaitForNextTickAsync(CancellationToken cancellationToken = default);
}

public interface ISessionRuntimeWorkerCoordinator
{
    Task WaitUntilAvailableAsync(CancellationToken cancellationToken = default);

    void Start(Guid sessionId);

    void Cancel(Guid sessionId);
}
