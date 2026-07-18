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
    bool CanRetry);

public sealed record SessionStartResult(
    PreflightStatus PreflightStatus,
    PreflightRejection Rejection,
    FocusSessionRuntimeView? Session);

public interface ISessionRuntimeScheduler
{
    Task WaitForNextTickAsync(CancellationToken cancellationToken = default);
}

public interface ISessionRuntimeWorkerCoordinator
{
    void Start(Guid sessionId);

    void Cancel(Guid sessionId);
}
