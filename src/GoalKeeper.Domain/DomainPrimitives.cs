namespace GoalKeeper.Domain;

public sealed class DomainRuleViolationException : InvalidOperationException
{
    public DomainRuleViolationException(string message) : base(message) { }
}

public interface IClock
{
    TimeSpan MonotonicNow { get; }

    DateTimeOffset UtcNow { get; }
}

internal static class Guard
{
    public static string Required(string? value, string name)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new DomainRuleViolationException($"{name} is required.");
        }

        return cleaned;
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static TimeSpan Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new DomainRuleViolationException($"{name} must be positive.");
        }

        return value;
    }

    public static TimeSpan NonNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
        {
            throw new DomainRuleViolationException($"{name} cannot be negative.");
        }

        return value;
    }
}
