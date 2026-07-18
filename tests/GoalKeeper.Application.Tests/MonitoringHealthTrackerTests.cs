using GoalKeeper.Application.Monitoring;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

public sealed class MonitoringHealthTrackerTests
{
    [Fact]
    public void Grace_uses_first_failure_and_recovery_clears_the_sustained_streak()
    {
        var sessionId = Guid.NewGuid();
        var clock = new MutableClock();
        var sink = new RecordingSink();
        var tracker = new MonitoringHealthTracker(
            sessionId,
            TimeSpan.FromSeconds(10),
            clock,
            sink);

        tracker.ReportFailure(MonitoringTechnicalSource.Camera);
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.ReportFailure(MonitoringTechnicalSource.Perception);
        Assert.Empty(sink.Events);

        clock.Advance(TimeSpan.FromSeconds(4));
        tracker.ReportFailure(MonitoringTechnicalSource.Perception);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.ReportRecovery(MonitoringTechnicalSource.Perception);
        tracker.ReportRecovery(MonitoringTechnicalSource.Perception);

        Assert.Collection(
            sink.Events,
            expired =>
            {
                Assert.Equal(MonitoringHealthEventKind.TechnicalGraceExpired, expired.Kind);
                Assert.Equal(sessionId, expired.SessionId);
                Assert.Equal(TimeSpan.Zero, expired.FirstFailureAtMonotonic);
                Assert.Equal(3, expired.ConsecutiveFailures);
            },
            recovered =>
            {
                Assert.Equal(MonitoringHealthEventKind.Recovered, recovered.Kind);
                Assert.Equal(TimeSpan.Zero, recovered.FirstFailureAtMonotonic);
                Assert.Equal(3, recovered.ConsecutiveFailures);
            });

        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.ReportFailure(MonitoringTechnicalSource.Camera);
        Assert.Equal(2, sink.Events.Count);
    }

    private sealed class MutableClock : IClock
    {
        public TimeSpan MonotonicNow { get; private set; }

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan duration)
        {
            MonotonicNow += duration;
            UtcNow += duration;
        }
    }

    private sealed class RecordingSink : IMonitoringHealthEventSink
    {
        public List<MonitoringHealthEvent> Events { get; } = [];

        public void Report(MonitoringHealthEvent healthEvent) =>
            Events.Add(healthEvent);
    }
}
