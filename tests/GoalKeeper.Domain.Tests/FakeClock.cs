using GoalKeeper.Domain;

namespace GoalKeeper.Domain.Tests;

internal sealed class FakeClock : IClock
{
    public TimeSpan MonotonicNow { get; private set; }

    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        MonotonicNow += duration;
        UtcNow += duration;
    }
}
