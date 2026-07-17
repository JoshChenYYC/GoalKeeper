using System.Diagnostics;
using GoalKeeper.Domain;

namespace GoalKeeper.Infrastructure;

public sealed class SystemClock : IClock
{
    public TimeSpan MonotonicNow => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
