using GoalKeeper.Domain;

namespace GoalKeeper.Application.Monitoring;

public sealed class MonitoringHealthTracker(
    Guid sessionId,
    TimeSpan gracePeriod,
    IClock clock,
    IMonitoringHealthEventSink eventSink)
{
    private readonly object _sync = new();
    private readonly Dictionary<MonitoringTechnicalSource, FailureState> _failures = [];

    public void ReportFailure(MonitoringTechnicalSource source)
    {
        lock (_sync)
        {
            var nowUtc = clock.UtcNow;
            var nowMonotonic = clock.MonotonicNow;
            if (!_failures.TryGetValue(source, out var failure))
            {
                failure = new(nowUtc, nowMonotonic);
                _failures.Add(source, failure);
            }

            failure.ConsecutiveFailures++;

            if (!failure.GraceExpired &&
                nowMonotonic - failure.FirstAtMonotonic >= gracePeriod)
            {
                failure.GraceExpired = true;
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.TechnicalGraceExpired,
                    source,
                    nowUtc,
                    nowMonotonic,
                    failure));
            }
        }
    }

    public void ReportRecovery(MonitoringTechnicalSource source)
    {
        lock (_sync)
        {
            if (!_failures.Remove(source, out var failure))
            {
                return;
            }

            var nowUtc = clock.UtcNow;
            var nowMonotonic = clock.MonotonicNow;
            if (!failure.GraceExpired &&
                nowMonotonic - failure.FirstAtMonotonic >= gracePeriod)
            {
                failure.GraceExpired = true;
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.TechnicalGraceExpired,
                    source,
                    nowUtc,
                    nowMonotonic,
                    failure));
            }

            if (failure.GraceExpired)
            {
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.Recovered,
                    source,
                    nowUtc,
                    nowMonotonic,
                    failure));
            }
        }
    }

    private MonitoringHealthEvent CreateEvent(
        MonitoringHealthEventKind kind,
        MonitoringTechnicalSource source,
        DateTimeOffset nowUtc,
        TimeSpan nowMonotonic,
        FailureState failure) =>
        new(
            sessionId,
            kind,
            source,
            nowUtc,
            nowMonotonic,
            failure.FirstAtUtc,
            failure.FirstAtMonotonic,
            failure.ConsecutiveFailures);

    private sealed class FailureState(
        DateTimeOffset firstAtUtc,
        TimeSpan firstAtMonotonic)
    {
        public DateTimeOffset FirstAtUtc { get; } = firstAtUtc;

        public TimeSpan FirstAtMonotonic { get; } = firstAtMonotonic;

        public int ConsecutiveFailures { get; set; }

        public bool GraceExpired { get; set; }
    }
}
