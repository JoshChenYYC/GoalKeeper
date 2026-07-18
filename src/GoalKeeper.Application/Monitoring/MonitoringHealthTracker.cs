using GoalKeeper.Domain;

namespace GoalKeeper.Application.Monitoring;

public sealed class MonitoringHealthTracker(
    Guid sessionId,
    TimeSpan gracePeriod,
    IClock clock,
    IMonitoringHealthEventSink eventSink)
{
    private readonly object _sync = new();
    private FailureState? _failure;

    public void ReportFailure(MonitoringTechnicalSource source)
    {
        lock (_sync)
        {
            var nowUtc = clock.UtcNow;
            var nowMonotonic = clock.MonotonicNow;
            _failure ??= new(source, nowUtc, nowMonotonic);
            _failure.ConsecutiveFailures++;
            _failure.LatestSource = source;

            if (!_failure.GraceExpired &&
                nowMonotonic - _failure.FirstAtMonotonic >= gracePeriod)
            {
                _failure.GraceExpired = true;
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.TechnicalGraceExpired,
                    source,
                    nowUtc,
                    nowMonotonic,
                    _failure));
            }
        }
    }

    public void ReportRecovery(MonitoringTechnicalSource source)
    {
        lock (_sync)
        {
            if (_failure is null)
            {
                return;
            }

            var nowUtc = clock.UtcNow;
            var nowMonotonic = clock.MonotonicNow;
            if (!_failure.GraceExpired &&
                nowMonotonic - _failure.FirstAtMonotonic >= gracePeriod)
            {
                _failure.GraceExpired = true;
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.TechnicalGraceExpired,
                    _failure.LatestSource,
                    nowUtc,
                    nowMonotonic,
                    _failure));
            }

            if (_failure.GraceExpired)
            {
                eventSink.Report(CreateEvent(
                    MonitoringHealthEventKind.Recovered,
                    source,
                    nowUtc,
                    nowMonotonic,
                    _failure));
            }

            _failure = null;
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
        MonitoringTechnicalSource source,
        DateTimeOffset firstAtUtc,
        TimeSpan firstAtMonotonic)
    {
        public MonitoringTechnicalSource LatestSource { get; set; } = source;

        public DateTimeOffset FirstAtUtc { get; } = firstAtUtc;

        public TimeSpan FirstAtMonotonic { get; } = firstAtMonotonic;

        public int ConsecutiveFailures { get; set; }

        public bool GraceExpired { get; set; }
    }
}
