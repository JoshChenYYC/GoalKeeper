namespace GoalKeeper.Domain;

public sealed class FocusTimer
{
    private TimeSpan _accumulated;
    private TimeSpan? _runningSince;
    private TimeSpan? _pendingDispute;

    public bool IsRunning => _runningSince is not null;

    public TimeSpan? RunningSince => _runningSince;

    public TimeSpan PendingDisputedDuration => _pendingDispute ?? TimeSpan.Zero;

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
        if (_runningSince is not { } runningSince || evidenceStartedAt < runningSince || evidenceStartedAt > admittedAt)
        {
            throw new DomainRuleViolationException("The evidence interval is outside the current focus interval.");
        }

        PauseAt(admittedAt);
        _pendingDispute = admittedAt - evidenceStartedAt;
        return _pendingDispute.Value;
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
}
