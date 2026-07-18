using GoalKeeper.Application.Monitoring;

namespace GoalKeeper.Infrastructure;

public sealed class SystemMonitoringDelay : IMonitoringDelay
{
    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}
