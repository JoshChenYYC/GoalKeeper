using GoalKeeper.Application.Perception;

namespace GoalKeeper.Application.Monitoring;

public sealed record MonitoringOptions(
    TimeSpan CaptureCadence,
    TimeSpan ObservationFreshnessLimit,
    TimeSpan TechnicalGracePeriod,
    CameraAcquisitionOptions Camera,
    PerceptionRequestOptions? Perception = null)
{
    public PerceptionRequestOptions PerceptionOptions { get; } =
        Perception ?? PerceptionRequestOptions.Default;

    public void Validate()
    {
        if (CaptureCadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureCadence));
        }

        if (ObservationFreshnessLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservationFreshnessLimit));
        }

        if (TechnicalGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TechnicalGracePeriod));
        }

        ArgumentNullException.ThrowIfNull(Camera);
        Camera.Validate();
    }
}

public interface IMonitoringSessionState
{
    Guid SessionId { get; }

    long SessionVersion { get; }

    bool IsScheduledBreak { get; }
}

public interface IMonitoringDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed record RetainedSnapshotArtifact(string Path, long StoredBytes);

public interface ISnapshotArtifactStore
{
    Task<RetainedSnapshotArtifact> RetainAsync(
        Guid sessionId,
        int sequence,
        CapturedJpegFrame frame,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken = default);
}

public sealed record ReasoningEligibleObservation(
    ObservationView Persisted,
    Observation Observation);

public interface IMonitoringObservationSink
{
    Task PublishAsync(
        ReasoningEligibleObservation observation,
        CancellationToken cancellationToken = default);
}

public enum MonitoringTechnicalSource
{
    Camera,
    Perception,
    Reasoning
}

public enum MonitoringHealthEventKind
{
    TechnicalGraceExpired,
    Recovered
}

public sealed record MonitoringHealthEvent(
    Guid SessionId,
    MonitoringHealthEventKind Kind,
    MonitoringTechnicalSource Source,
    DateTimeOffset OccurredAtUtc,
    TimeSpan OccurredAtMonotonic,
    DateTimeOffset FirstFailureAtUtc,
    TimeSpan FirstFailureAtMonotonic,
    int ConsecutiveFailures);

public interface IMonitoringHealthEventSink
{
    void Report(MonitoringHealthEvent healthEvent);
}

public enum PreflightStatus
{
    AwaitingConfirmation,
    Rejected,
    TechnicalFailure,
    Passed,
    Cancelled
}

public enum PreflightRejection
{
    None,
    ImageQuality,
    PeopleCount,
    PerceptionInvalid,
    PerceptionFailure,
    UserRejected
}

public sealed record PreflightResult(
    PreflightStatus Status,
    CapturedJpegFrame? Frame,
    Observation? Observation,
    PreflightRejection Rejection)
{
    public bool CanRetry =>
        Status is PreflightStatus.Rejected or PreflightStatus.TechnicalFailure;
}
