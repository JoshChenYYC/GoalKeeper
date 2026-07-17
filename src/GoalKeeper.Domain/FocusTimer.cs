namespace GoalKeeper.Domain;

public sealed class FocusTimer
{
    private TimeSpan _accumulated;
    private TimeSpan? _runningSince;
    private TimeSpan? _pendingDispute;

    public TimeSpan Accumulated => _accumulated;

    public bool IsRunning => _runningSince is not null;

    public TimeSpan? RunningSince => _runningSince;

    public TimeSpan PendingDisputedDuration => _pendingDispute ?? TimeSpan.Zero;

    internal FocusTimerSnapshot CreateSnapshot() =>
        new(_accumulated, _runningSince, _pendingDispute);

    internal static FocusTimer Rehydrate(FocusTimerSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        return new FocusTimer
        {
            _accumulated = snapshot.Accumulated,
            _runningSince = snapshot.RunningSince,
            _pendingDispute = snapshot.PendingDispute
        };
    }

    public TimeSpan ElapsedAt(TimeSpan now) =>
        _accumulated + (_runningSince is { } started ? now - started : TimeSpan.Zero);

    public void Start(TimeSpan now)
    {
        if (IsRunning)
        {
            throw new DomainRuleViolationException("The Focus Timer is already running.");
        }

        _runningSince = now;
    }

    public void PauseAt(TimeSpan at)
    {
        if (_runningSince is not { } started || at < started)
        {
            throw new DomainRuleViolationException("The Focus Timer cannot pause at that time.");
        }

        _accumulated += at - started;
        _runningSince = null;
    }

    public void ResumeAt(TimeSpan at) => Start(at);

    public TimeSpan TimeAtElapsed(TimeSpan elapsed)
    {
        if (_runningSince is not { } started || elapsed < _accumulated)
        {
            throw new DomainRuleViolationException("The active-focus milestone is unavailable.");
        }

        return started + (elapsed - _accumulated);
    }

    public TimeSpan BeginProvisionalDispute(TimeSpan evidenceStartedAt, TimeSpan admittedAt)
    {
        ValidateProvisionalDispute(evidenceStartedAt, admittedAt);

        PauseAt(admittedAt);
        _pendingDispute = admittedAt - evidenceStartedAt;
        return _pendingDispute.Value;
    }

    internal void ValidateProvisionalDispute(TimeSpan evidenceStartedAt, TimeSpan admittedAt)
    {
        if (_runningSince is not { } runningSince || evidenceStartedAt < runningSince ||
            evidenceStartedAt > admittedAt)
        {
            throw new DomainRuleViolationException("The evidence interval is outside the current focus interval.");
        }
    }

    public TimeSpan ConfirmExcludedTime()
    {
        var disputed = _pendingDispute ?? throw new DomainRuleViolationException("No evidence interval is disputed.");
        if (disputed > _accumulated)
        {
            throw new DomainRuleViolationException("The disputed interval exceeds accumulated focus.");
        }

        _accumulated -= disputed;
        _pendingDispute = null;
        return disputed;
    }

    public TimeSpan RestoreDisputedTime()
    {
        var disputed = _pendingDispute ?? throw new DomainRuleViolationException("No evidence interval is disputed.");
        _pendingDispute = null;
        return disputed;
    }

    private static void ValidateSnapshot(FocusTimerSnapshot snapshot)
    {
        Guard.NonNegative(snapshot.Accumulated, nameof(snapshot.Accumulated));
        if (snapshot.RunningSince is { } runningSince)
        {
            Guard.NonNegative(runningSince, nameof(snapshot.RunningSince));
        }

        if (snapshot.PendingDispute is { } pendingDispute)
        {
            Guard.NonNegative(pendingDispute, nameof(snapshot.PendingDispute));
            if (snapshot.RunningSince is not null || pendingDispute > snapshot.Accumulated)
            {
                throw new DomainRuleViolationException("The Focus Timer snapshot contains an invalid dispute.");
            }
        }
    }
}

public sealed record FocusTimerSnapshot(
    TimeSpan Accumulated,
    TimeSpan? RunningSince,
    TimeSpan? PendingDispute);
