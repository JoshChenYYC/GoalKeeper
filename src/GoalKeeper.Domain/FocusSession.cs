namespace GoalKeeper.Domain;

public enum FocusSessionState
{
    Focusing,
    ScheduledBreak,
    RecoveryCheckIn,
    RecoveryWindow,
    AwaitingResponse,
    MonitoringUnavailable,
    Fulfilled,
    EndedEarly
}

public enum EndedEarlyReason { UserRequest, NoResponse, MonitoringFailure }

public sealed class FocusSession
{
    private readonly Goal _goal;
    private readonly IClock _clock;
    private readonly FocusSessionPolicy _policy;
    private readonly FocusTimer _timer = new();
    private readonly List<BehaviorClarification> _clarifications = [];
    private readonly List<DeviationOverride> _overrides = [];
    private int _nextBreakIndex;
    private TimeSpan? _breakEndsAt;
    private TimeSpan? _responseDeadline;
    private TimeSpan? _monitoringDeadline;
    private TimeSpan? _monitoringUnavailableAt;

    private FocusSession(
        Guid id,
        Goal goal,
        SessionContract contract,
        IClock clock,
        FocusSessionPolicy policy)
    {
        Id = id;
        _goal = goal;
        Contract = contract;
        _clock = clock;
        _policy = policy;
        State = FocusSessionState.Focusing;
        Version = 1;
        StartedAtUtc = clock.UtcNow;
        ProjectedEndUtc = StartedAtUtc + contract.TargetFocusDuration +
            TimeSpan.FromTicks(contract.ScheduledBreaks.Sum(x => x.Duration.Ticks));
        _timer.Start(clock.MonotonicNow);
    }

    public Guid Id { get; }

    public Guid GoalId => _goal.Id;

    public SessionContract Contract { get; }

    public FocusSessionState State { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset ProjectedEndUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public EndedEarlyReason? EndedEarlyReason { get; private set; }

    public TimeSpan ActiveFocusElapsed => _timer.ElapsedAt(_clock.MonotonicNow);

    public Intervention? ActiveIntervention { get; private set; }

    public RecoveryWindow? CurrentRecoveryWindow { get; private set; }

    public int ConsecutiveUnsuccessfulRecoveries { get; private set; }

    public bool RequiresFinalEscalation { get; private set; }

    public IReadOnlyList<BehaviorClarification> Clarifications => _clarifications;

    public IReadOnlyList<DeviationOverride> DeviationOverrides => _overrides;

    public SessionReview? Review { get; private set; }

    public bool IsTerminal => State is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly;

    public static FocusSession Start(
        Goal goal,
        SessionContract contract,
        bool preflightSuccessful,
        IClock clock,
        FocusSessionPolicy? policy = null)
    {
        if (!preflightSuccessful)
        {
            throw new DomainRuleViolationException("Monitoring requires successful preflight.");
        }

        if (contract.Goal.Id != goal.Id)
        {
            throw new DomainRuleViolationException("The Session Contract belongs to another Goal.");
        }

        var id = Guid.NewGuid();
        goal.BeginSession(id);
        return new(id, goal, contract, clock, policy ?? FocusSessionPolicy.Default);
    }

    public void Advance()
    {
        var now = _clock.MonotonicNow;
        while (!IsTerminal)
        {
            if (State == FocusSessionState.ScheduledBreak)
            {
                if (now < _breakEndsAt)
                {
                    return;
                }

                _timer.ResumeAt(_breakEndsAt!.Value);
                _breakEndsAt = null;
                TransitionTo(FocusSessionState.Focusing);
                continue;
            }

            if (State == FocusSessionState.AwaitingResponse)
            {
                if (now >= _responseDeadline)
                {
                    EndEarlyAt(global::GoalKeeper.Domain.EndedEarlyReason.NoResponse, _responseDeadline!.Value);
                }

                return;
            }

            if (State == FocusSessionState.MonitoringUnavailable)
            {
                if (now >= _monitoringDeadline)
                {
                    EndEarlyAt(global::GoalKeeper.Domain.EndedEarlyReason.MonitoringFailure, _monitoringDeadline!.Value);
                }

                return;
            }

            if (State is not (FocusSessionState.Focusing or FocusSessionState.RecoveryWindow))
            {
                return;
            }

            var nextActiveMilestone = Contract.TargetFocusDuration;
            var isBreak = false;
            if (_nextBreakIndex < Contract.ScheduledBreaks.Count &&
                Contract.ScheduledBreaks[_nextBreakIndex].ActiveFocusOffset < nextActiveMilestone)
            {
                nextActiveMilestone = Contract.ScheduledBreaks[_nextBreakIndex].ActiveFocusOffset;
                isBreak = true;
            }

            var milestoneAt = _timer.TimeAtElapsed(nextActiveMilestone);
            if (State == FocusSessionState.RecoveryWindow &&
                CurrentRecoveryWindow is { } window &&
                window.EndsAt <= milestoneAt && now >= window.EndsAt)
            {
                CurrentRecoveryWindow = null;
                ConsecutiveUnsuccessfulRecoveries = 0;
                TransitionTo(FocusSessionState.Focusing);
                continue;
            }

            if (now < milestoneAt)
            {
                return;
            }

            _timer.PauseAt(milestoneAt);
            if (isBreak)
            {
                var scheduledBreak = Contract.ScheduledBreaks[_nextBreakIndex++];
                CurrentRecoveryWindow = null;
                _breakEndsAt = milestoneAt + scheduledBreak.Duration;
                TransitionTo(FocusSessionState.ScheduledBreak);
                continue;
            }

            FinishFulfilled(milestoneAt, completeGoal: false);
        }
    }

    public void AdmitIntervention(ReasoningEvaluation evaluation)
    {
        Advance();
        EnsureState(FocusSessionState.Focusing, FocusSessionState.RecoveryWindow);
        if (evaluation.Decision != ReasoningDecision.BeginRecoveryCheckIn ||
            evaluation.SessionId != Id || evaluation.SessionVersion != Version ||
            evaluation.EvidenceEpisode is not { } episode || episode.SessionId != Id)
        {
            throw new DomainRuleViolationException("The Reasoning Evaluation is invalid or stale.");
        }

        ValidateDeviation(episode.Deviation);
        if (_overrides.Any(x => x.Deviation == episode.Deviation))
        {
            throw new DomainRuleViolationException("A Deviation Override applies for the remainder of this session.");
        }

        if (episode.LatestObservation.CapturedAt > _clock.MonotonicNow)
        {
            throw new DomainRuleViolationException("Evidence cannot come from the future.");
        }

        if (State == FocusSessionState.RecoveryWindow)
        {
            ConsecutiveUnsuccessfulRecoveries++;
            RequiresFinalEscalation =
                ConsecutiveUnsuccessfulRecoveries >= _policy.MaximumUnsuccessfulRecoveries;
        }

        var disputed = _timer.BeginProvisionalDispute(
            episode.FirstObservation.CapturedAt, _clock.MonotonicNow);
        ActiveIntervention = new(
            Guid.NewGuid(), evaluation, _clock.MonotonicNow, _clock.UtcNow, disputed);
        CurrentRecoveryWindow = null;
        TransitionTo(FocusSessionState.RecoveryCheckIn);
    }

    public BehaviorClarification ClarifyBehavior(string explanation, bool applyRemainderOverride = false)
    {
        EnsureRecoveryCheckIn();
        var intervention = ActiveIntervention!;
        var clarification = new BehaviorClarification(
            Guid.NewGuid(), Id, intervention.Id, Guard.Required(explanation, nameof(explanation)), _clock.UtcNow);
        _clarifications.Add(clarification);
        if (applyRemainderOverride)
        {
            AddOverride(intervention.Evaluation.EvidenceEpisode!.Deviation, explanation);
        }

        _timer.RestoreDisputedTime();
        ProjectedEndUtc += _clock.MonotonicNow - intervention.AdmittedAt;
        _timer.ResumeAt(_clock.MonotonicNow);
        ActiveIntervention = null;
        RequiresFinalEscalation = false;
        ConsecutiveUnsuccessfulRecoveries = 0;
        TransitionTo(FocusSessionState.Focusing);
        return clarification;
    }

    public DeviationOverride ApplyRemainderDeviationOverride(string reason)
    {
        EnsureRecoveryCheckIn();
        return AddOverride(ActiveIntervention!.Evaluation.EvidenceEpisode!.Deviation, reason);
    }

    public void Recommit()
    {
        EnsureRecoveryCheckIn();
        if (RequiresFinalEscalation)
        {
            throw new DomainRuleViolationException("Explicit continuation or early ending is required.");
        }

        ResolveRecommit(resetRecoveryCount: false);
    }

    public void ConfirmContinuationAfterEscalation()
    {
        EnsureRecoveryCheckIn();
        if (!RequiresFinalEscalation)
        {
            throw new DomainRuleViolationException("No final escalation is active.");
        }

        ResolveRecommit(resetRecoveryCount: true);
    }

    public void ReportNoResponse()
    {
        EnsureRecoveryCheckIn();
        _responseDeadline = _clock.MonotonicNow + _policy.ResponseTimeout;
        TransitionTo(FocusSessionState.AwaitingResponse);
    }

    public void ReturnToRecoveryCheckIn()
    {
        Advance();
        EnsureState(FocusSessionState.AwaitingResponse);
        _responseDeadline = null;
        TransitionTo(FocusSessionState.RecoveryCheckIn);
    }

    public void ReportMonitoringUnavailable()
    {
        Advance();
        EnsureState(FocusSessionState.Focusing, FocusSessionState.RecoveryWindow);
        _timer.PauseAt(_clock.MonotonicNow);
        _monitoringUnavailableAt = _clock.MonotonicNow;
        _monitoringDeadline = _clock.MonotonicNow + _policy.MonitoringOutageGrace;
        CurrentRecoveryWindow = null;
        TransitionTo(FocusSessionState.MonitoringUnavailable);
    }

    public void RestoreMonitoring()
    {
        Advance();
        EnsureState(FocusSessionState.MonitoringUnavailable);
        ProjectedEndUtc += _clock.MonotonicNow - _monitoringUnavailableAt!.Value;
        _timer.ResumeAt(_clock.MonotonicNow);
        _monitoringDeadline = _monitoringUnavailableAt = null;
        TransitionTo(FocusSessionState.Focusing);
    }

    public void CompleteGoal()
    {
        Advance();
        EnsureNotTerminal();
        PauseIfRunning(_clock.MonotonicNow);
        _goal.CompleteFromSession(Id, _clock.UtcNow);
        FinishFulfilled(_clock.MonotonicNow, completeGoal: false);
    }

    public void EndEarlyByUser()
    {
        Advance();
        EnsureNotTerminal();
        EndEarlyAt(global::GoalKeeper.Domain.EndedEarlyReason.UserRequest, _clock.MonotonicNow);
    }

    public SessionReview SubmitReview(
        bool meaningfulProgress,
        InterventionHelpfulness helpfulness,
        string? note,
        bool markGoalComplete)
    {
        if (!IsTerminal)
        {
            throw new DomainRuleViolationException("A Session Review requires a final Focus Session.");
        }

        if (Review is not null)
        {
            throw new DomainRuleViolationException("The Session Review has already been submitted.");
        }

        if (markGoalComplete && _goal.Status == GoalStatus.Active)
        {
            _goal.Complete(_clock);
        }

        Review = new(
            Guid.NewGuid(), Id, meaningfulProgress, helpfulness, Guard.Optional(note), markGoalComplete, _clock.UtcNow);
        return Review;
    }

    private void ResolveRecommit(bool resetRecoveryCount)
    {
        var intervention = ActiveIntervention!;
        var excluded = _timer.ConfirmExcludedTime();
        ProjectedEndUtc += excluded + (_clock.MonotonicNow - intervention.AdmittedAt);
        _timer.ResumeAt(_clock.MonotonicNow);
        CurrentRecoveryWindow = new(
            _clock.MonotonicNow,
            _clock.MonotonicNow + _policy.RecoveryWindowDuration,
            intervention.Evaluation.EvidenceEpisode!.Deviation);
        ActiveIntervention = null;
        RequiresFinalEscalation = false;
        if (resetRecoveryCount)
        {
            ConsecutiveUnsuccessfulRecoveries = 0;
        }

        TransitionTo(FocusSessionState.RecoveryWindow);
    }

    private DeviationOverride AddOverride(DeviationReference deviation, string reason)
    {
        if (_overrides.Any(x => x.Deviation == deviation))
        {
            throw new DomainRuleViolationException("The Deviation Override already exists.");
        }

        var value = new DeviationOverride(
            Guid.NewGuid(), Id, deviation, Guard.Required(reason, nameof(reason)), _clock.UtcNow);
        _overrides.Add(value);
        return value;
    }

    private void ValidateDeviation(DeviationReference deviation)
    {
        if (deviation.ListedDeviationId is { } listed &&
            Contract.DeviationProfile.Deviations.All(x => x.Id != listed))
        {
            throw new DomainRuleViolationException("The listed Deviation is not in the Session Contract.");
        }

        if (deviation.IsUnlisted && Contract.ReasoningMode != ReasoningMode.Exploratory)
        {
            throw new DomainRuleViolationException("Unlisted behavior requires Exploratory mode.");
        }
    }

    private void EnsureRecoveryCheckIn() => EnsureState(FocusSessionState.RecoveryCheckIn);

    private void EnsureState(params FocusSessionState[] allowed)
    {
        if (!allowed.Contains(State))
        {
            throw new DomainRuleViolationException($"The command is invalid while the session is {State}.");
        }
    }

    private void EnsureNotTerminal()
    {
        if (IsTerminal)
        {
            throw new DomainRuleViolationException("The Focus Session has already ended.");
        }
    }

    private void TransitionTo(FocusSessionState state)
    {
        State = state;
        Version++;
    }

    private void FinishFulfilled(TimeSpan endedAt, bool completeGoal)
    {
        _ = completeGoal;
        State = FocusSessionState.Fulfilled;
        Version++;
        EndedAtUtc = UtcAt(endedAt);
        _goal.EndSession(Id);
    }

    private void EndEarlyAt(EndedEarlyReason reason, TimeSpan endedAt)
    {
        PauseIfRunning(endedAt);
        State = FocusSessionState.EndedEarly;
        Version++;
        EndedEarlyReason = reason;
        EndedAtUtc = UtcAt(endedAt);
        _goal.EndSession(Id);
    }

    private void PauseIfRunning(TimeSpan at)
    {
        if (_timer.IsRunning)
        {
            _timer.PauseAt(at);
        }
    }

    private DateTimeOffset UtcAt(TimeSpan monotonicAt) =>
        _clock.UtcNow - (_clock.MonotonicNow - monotonicAt);
}
