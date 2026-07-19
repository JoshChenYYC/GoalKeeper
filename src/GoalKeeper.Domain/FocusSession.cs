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

public enum EndedEarlyReason
{
    UserRequest,
    NoResponse,
    MonitoringFailure,
    ApplicationInterrupted
}

public sealed class FocusSession
{
    private readonly Goal _goal;
    private readonly IClock _clock;
    private readonly FocusSessionPolicy _policy;
    private readonly FocusTimer _timer;
    private readonly List<BehaviorClarification> _clarifications;
    private readonly List<DeviationOverride> _overrides;
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
        _timer = new FocusTimer();
        _clarifications = [];
        _overrides = [];
        State = FocusSessionState.Focusing;
        Version = 1;
        StartedAtUtc = clock.UtcNow;
        ProjectedEndUtc = StartedAtUtc + contract.TargetFocusDuration +
            TimeSpan.FromTicks(contract.ScheduledBreaks.Sum(x => x.Duration.Ticks));
        _timer.Start(clock.MonotonicNow);
    }

    private FocusSession(
        Goal goal,
        SessionContract contract,
        FocusSessionRuntimeSnapshot snapshot,
        IClock clock,
        FocusTimer timer,
        FocusSessionPolicy policy,
        Intervention? intervention,
        RecoveryWindow? recoveryWindow,
        List<BehaviorClarification> clarifications,
        List<DeviationOverride> overrides,
        SessionReview? review)
    {
        Id = snapshot.Id;
        _goal = goal;
        Contract = contract;
        _clock = clock;
        _policy = policy;
        _timer = timer;
        _clarifications = clarifications;
        _overrides = overrides;
        State = snapshot.State;
        Version = snapshot.Version;
        StartedAtUtc = snapshot.StartedAtUtc;
        ProjectedEndUtc = snapshot.ProjectedEndUtc;
        EndedAtUtc = snapshot.EndedAtUtc;
        EndedEarlyReason = snapshot.EndedEarlyReason;
        _nextBreakIndex = snapshot.NextBreakIndex;
        _breakEndsAt = snapshot.BreakEndsAt;
        ActiveIntervention = intervention;
        CurrentRecoveryWindow = recoveryWindow;
        ConsecutiveUnsuccessfulRecoveries = snapshot.ConsecutiveUnsuccessfulRecoveries;
        RequiresFinalEscalation = snapshot.RequiresFinalEscalation;
        _responseDeadline = snapshot.ResponseDeadline;
        _monitoringUnavailableAt = snapshot.MonitoringUnavailableAt;
        _monitoringDeadline = snapshot.MonitoringDeadline;
        Review = review;
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

    public static FocusSession Rehydrate(
        Goal goal,
        SessionContract contract,
        FocusSessionRuntimeSnapshot snapshot,
        IClock clock)
    {
        ValidateSnapshot(goal, contract, snapshot, clock);
        var timer = FocusTimer.Rehydrate(snapshot.Timer);
        var policy = FocusSessionPolicy.Rehydrate(snapshot.Policy);
        var intervention = snapshot.ActiveIntervention is null
            ? null
            : RehydrateIntervention(snapshot.ActiveIntervention);
        var recoveryWindow = snapshot.CurrentRecoveryWindow is null
            ? null
            : RehydrateRecoveryWindow(snapshot.CurrentRecoveryWindow);
        var clarifications = snapshot.Clarifications.Select(RehydrateClarification).ToList();
        var overrides = snapshot.DeviationOverrides.Select(RehydrateOverride).ToList();
        var review = snapshot.Review is null ? null : RehydrateReview(snapshot.Review);
        var session = new FocusSession(
            goal,
            contract,
            snapshot,
            clock,
            timer,
            policy,
            intervention,
            recoveryWindow,
            clarifications,
            overrides,
            review);
        if (!session.IsTerminal)
        {
            goal.BeginSession(session.Id);
        }

        return session;
    }

    public FocusSessionRuntimeSnapshot CreateSnapshot() =>
        new(
            Id,
            GoalId,
            Contract.Id,
            State,
            Version,
            StartedAtUtc,
            ProjectedEndUtc,
            EndedAtUtc,
            EndedEarlyReason,
            _timer.CreateSnapshot(),
            _nextBreakIndex,
            _breakEndsAt,
            ActiveIntervention is null ? null : Snapshot(ActiveIntervention),
            CurrentRecoveryWindow is null ? null : Snapshot(CurrentRecoveryWindow),
            ConsecutiveUnsuccessfulRecoveries,
            RequiresFinalEscalation,
            _responseDeadline,
            _monitoringUnavailableAt,
            _monitoringDeadline,
            _clarifications.Select(Snapshot).ToArray(),
            _overrides.Select(Snapshot).ToArray(),
            Review is null ? null : Snapshot(Review),
            _policy.CreateSnapshot());

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
        EnsureState(FocusSessionState.Focusing, FocusSessionState.RecoveryWindow);
        ValidateEvaluation(evaluation);
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

        _timer.ValidateProvisionalDispute(
            episode.FirstObservation.CapturedAt,
            _clock.MonotonicNow);
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
        var validatedExplanation = Guard.Required(explanation, nameof(explanation));
        if (applyRemainderOverride)
        {
            EnsureOverrideDoesNotExist(intervention.Evaluation.EvidenceEpisode!.Deviation);
        }

        var clarification = new BehaviorClarification(
            Guid.NewGuid(), Id, intervention.Id, validatedExplanation, _clock.UtcNow);
        DeviationOverride? remainderOverride = null;
        if (applyRemainderOverride)
        {
            remainderOverride = CreateOverride(
                intervention.Evaluation.EvidenceEpisode!.Deviation,
                validatedExplanation);
        }

        _timer.RestoreDisputedTime();
        ProjectedEndUtc += _clock.MonotonicNow - intervention.AdmittedAt;
        _timer.ResumeAt(_clock.MonotonicNow);
        _clarifications.Add(clarification);
        if (remainderOverride is not null)
        {
            _overrides.Add(remainderOverride);
        }

        ActiveIntervention = null;
        RequiresFinalEscalation = false;
        ConsecutiveUnsuccessfulRecoveries = 0;
        TransitionTo(FocusSessionState.Focusing);
        return clarification;
    }

    public DeviationOverride ApplyRemainderDeviationOverride(string reason)
    {
        EnsureRecoveryCheckIn();
        var value = AddOverride(ActiveIntervention!.Evaluation.EvidenceEpisode!.Deviation, reason);
        Version++;
        return value;
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
        EnsureState(FocusSessionState.AwaitingResponse);
        if (_clock.MonotonicNow >= _responseDeadline)
        {
            throw new DomainRuleViolationException("The response deadline has expired.");
        }

        _responseDeadline = null;
        TransitionTo(FocusSessionState.RecoveryCheckIn);
    }

    public void ReportMonitoringUnavailable()
    {
        EnsureState(FocusSessionState.Focusing, FocusSessionState.RecoveryWindow);
        _timer.PauseAt(_clock.MonotonicNow);
        _monitoringUnavailableAt = _clock.MonotonicNow;
        _monitoringDeadline = _clock.MonotonicNow + _policy.MonitoringOutageGrace;
        CurrentRecoveryWindow = null;
        TransitionTo(FocusSessionState.MonitoringUnavailable);
    }

    public void RestoreMonitoring()
    {
        EnsureState(FocusSessionState.MonitoringUnavailable);
        if (_clock.MonotonicNow >= _monitoringDeadline)
        {
            throw new DomainRuleViolationException("The monitoring deadline has expired.");
        }

        ProjectedEndUtc += _clock.MonotonicNow - _monitoringUnavailableAt!.Value;
        _timer.ResumeAt(_clock.MonotonicNow);
        _monitoringDeadline = _monitoringUnavailableAt = null;
        TransitionTo(FocusSessionState.Focusing);
    }

    public void CompleteGoal()
    {
        EnsureState(FocusSessionState.Focusing, FocusSessionState.RecoveryWindow);
        _goal.EnsureOwnedBySession(Id);
        PauseIfRunning(_clock.MonotonicNow);
        _goal.CompleteFromSession(Id, _clock.UtcNow);
        FinishFulfilled(_clock.MonotonicNow, completeGoal: false);
    }

    public void EndEarlyByUser()
    {
        EnsureNotTerminal();
        EndEarlyAt(global::GoalKeeper.Domain.EndedEarlyReason.UserRequest, _clock.MonotonicNow);
    }

    public void EndAfterApplicationInterruption()
    {
        EnsureNotTerminal();
        EndEarlyAt(
            global::GoalKeeper.Domain.EndedEarlyReason.ApplicationInterrupted,
            _clock.MonotonicNow);
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

        Guard.DefinedEnum(helpfulness, nameof(helpfulness));
        var validatedNote = Guard.Optional(note);
        if (markGoalComplete && _goal.Status == GoalStatus.Active)
        {
            _goal.Complete(_clock);
        }

        Review = new(
            Guid.NewGuid(), Id, meaningfulProgress, helpfulness, validatedNote, markGoalComplete, _clock.UtcNow);
        Version++;
        return Review;
    }

    private static void ValidateSnapshot(
        Goal goal,
        SessionContract contract,
        FocusSessionRuntimeSnapshot snapshot,
        IClock clock)
    {
        Guard.Identifier(snapshot.Id, nameof(snapshot.Id));
        Guard.Identifier(snapshot.GoalId, nameof(snapshot.GoalId));
        Guard.Identifier(snapshot.ContractId, nameof(snapshot.ContractId));
        Guard.DefinedEnum(snapshot.State, nameof(snapshot.State));
        if (snapshot.GoalId != goal.Id || snapshot.ContractId != contract.Id ||
            contract.Goal.Id != goal.Id)
        {
            throw new DomainRuleViolationException("The runtime snapshot belongs to another Goal or contract.");
        }

        if (snapshot.Version <= 0)
        {
            throw new DomainRuleViolationException("The runtime snapshot version must be positive.");
        }

        if (snapshot.ProjectedEndUtc < snapshot.StartedAtUtc ||
            snapshot.StartedAtUtc > clock.UtcNow)
        {
            throw new DomainRuleViolationException("The runtime snapshot timestamps are invalid.");
        }

        _ = FocusTimer.Rehydrate(snapshot.Timer);
        _ = FocusSessionPolicy.Rehydrate(snapshot.Policy);
        if (snapshot.NextBreakIndex < 0 ||
            snapshot.NextBreakIndex > contract.ScheduledBreaks.Count)
        {
            throw new DomainRuleViolationException("The Scheduled Break cursor is invalid.");
        }

        if (snapshot.ConsecutiveUnsuccessfulRecoveries < 0 ||
            snapshot.ConsecutiveUnsuccessfulRecoveries >
            snapshot.Policy.MaximumUnsuccessfulRecoveries)
        {
            throw new DomainRuleViolationException("The Recovery counter is invalid.");
        }

        if (snapshot.RequiresFinalEscalation &&
            snapshot.ConsecutiveUnsuccessfulRecoveries <
            snapshot.Policy.MaximumUnsuccessfulRecoveries)
        {
            throw new DomainRuleViolationException("The final-escalation flag is inconsistent.");
        }

        var terminal = snapshot.State is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly;
        if (terminal != snapshot.EndedAtUtc.HasValue)
        {
            throw new DomainRuleViolationException("Terminal timestamps are inconsistent.");
        }

        if ((snapshot.State == FocusSessionState.EndedEarly) != snapshot.EndedEarlyReason.HasValue)
        {
            throw new DomainRuleViolationException("The Ended Early reason is inconsistent.");
        }

        if (snapshot.EndedEarlyReason is { } endedReason)
        {
            Guard.DefinedEnum(endedReason, nameof(snapshot.EndedEarlyReason));
        }

        if (snapshot.EndedAtUtc is { } endedAt && endedAt < snapshot.StartedAtUtc)
        {
            throw new DomainRuleViolationException("A Focus Session cannot end before it starts.");
        }

        var timerShouldRun = snapshot.State is
            FocusSessionState.Focusing or FocusSessionState.RecoveryWindow;
        if (timerShouldRun != snapshot.Timer.RunningSince.HasValue)
        {
            throw new DomainRuleViolationException("The Focus Timer running state is inconsistent.");
        }

        var hasDispute = snapshot.Timer.PendingDispute.HasValue;
        var hasIntervention = snapshot.ActiveIntervention is not null;
        var disputeAllowed = snapshot.State is
            FocusSessionState.RecoveryCheckIn or
            FocusSessionState.AwaitingResponse or
            FocusSessionState.EndedEarly;
        var disputeRequired = snapshot.State is
            FocusSessionState.RecoveryCheckIn or FocusSessionState.AwaitingResponse;
        if (hasDispute != hasIntervention || hasDispute && !disputeAllowed ||
            !hasDispute && disputeRequired)
        {
            throw new DomainRuleViolationException("The active Intervention snapshot is inconsistent.");
        }

        if ((snapshot.State == FocusSessionState.ScheduledBreak) != snapshot.BreakEndsAt.HasValue ||
            (snapshot.State == FocusSessionState.RecoveryWindow) !=
            (snapshot.CurrentRecoveryWindow is not null) ||
            (snapshot.State == FocusSessionState.AwaitingResponse) != snapshot.ResponseDeadline.HasValue ||
            (snapshot.State == FocusSessionState.MonitoringUnavailable) !=
            snapshot.MonitoringDeadline.HasValue ||
            (snapshot.State == FocusSessionState.MonitoringUnavailable) !=
            snapshot.MonitoringUnavailableAt.HasValue)
        {
            throw new DomainRuleViolationException("The state deadline snapshot is inconsistent.");
        }

        if (snapshot.ActiveIntervention is { } intervention)
        {
            var value = RehydrateIntervention(intervention);
            if (value.Evaluation.SessionId != snapshot.Id ||
                value.Evaluation.SessionVersion >= snapshot.Version)
            {
                throw new DomainRuleViolationException("The active Intervention version is invalid.");
            }
        }

        if (snapshot.CurrentRecoveryWindow is { } recovery)
        {
            var value = RehydrateRecoveryWindow(recovery);
            if (value.EndsAt <= value.StartedAt)
            {
                throw new DomainRuleViolationException("The Recovery Window snapshot is invalid.");
            }
        }

        if (snapshot.Clarifications is null || snapshot.DeviationOverrides is null)
        {
            throw new DomainRuleViolationException("Runtime snapshot collections are required.");
        }

        var clarificationIds = snapshot.Clarifications.Select(x => RehydrateClarification(x).Id).ToArray();
        var overrideIds = snapshot.DeviationOverrides.Select(x => RehydrateOverride(x).Id).ToArray();
        if (clarificationIds.Distinct().Count() != clarificationIds.Length ||
            overrideIds.Distinct().Count() != overrideIds.Length ||
            snapshot.Clarifications.Any(x => x.SessionId != snapshot.Id) ||
            snapshot.DeviationOverrides.Any(x => x.SessionId != snapshot.Id))
        {
            throw new DomainRuleViolationException("Runtime history references are invalid.");
        }

        if (snapshot.Review is { } review)
        {
            if (!terminal || RehydrateReview(review).SessionId != snapshot.Id)
            {
                throw new DomainRuleViolationException("The Session Review snapshot is invalid.");
            }
        }
    }

    private static void ValidateEvaluation(ReasoningEvaluation evaluation)
    {
        if (evaluation.EvidenceEpisode is { } episode)
        {
            _ = EvidenceEpisode.Rehydrate(
                episode.Id,
                episode.SessionId,
                episode.Deviation,
                episode.Observations);
        }

        _ = ReasoningEvaluation.Rehydrate(
            evaluation.Id,
            evaluation.SessionId,
            evaluation.SessionVersion,
            evaluation.Decision,
            evaluation.EvidenceEpisode,
            evaluation.Rationale,
            evaluation.EvaluatedAtUtc);
    }

    private static InterventionSnapshot Snapshot(Intervention value) =>
        new(
            value.Id,
            Snapshot(value.Evaluation),
            value.AdmittedAt,
            value.AdmittedAtUtc,
            value.DisputedDuration);

    private static ReasoningEvaluationSnapshot Snapshot(ReasoningEvaluation value) =>
        new(
            value.Id,
            value.SessionId,
            value.SessionVersion,
            value.Decision,
            value.EvidenceEpisode is null ? null : Snapshot(value.EvidenceEpisode),
            value.Rationale,
            value.EvaluatedAtUtc);

    private static EvidenceEpisodeSnapshot Snapshot(EvidenceEpisode value) =>
        new(
            value.Id,
            value.SessionId,
            Snapshot(value.Deviation),
            value.Observations.Select(x =>
                new ObservationReferenceSnapshot(x.ObservationId, x.SessionId, x.CapturedAt)).ToArray());

    private static DeviationReferenceSnapshot Snapshot(DeviationReference value) =>
        new(value.ListedDeviationId, value.UnlistedDescription);

    private static RecoveryWindowSnapshot Snapshot(RecoveryWindow value) =>
        new(value.StartedAt, value.EndsAt, Snapshot(value.Deviation));

    private static BehaviorClarificationSnapshot Snapshot(BehaviorClarification value) =>
        new(
            value.Id,
            value.SessionId,
            value.InterventionId,
            value.Explanation,
            value.ClarifiedAtUtc);

    private static DeviationOverrideSnapshot Snapshot(DeviationOverride value) =>
        new(
            value.Id,
            value.SessionId,
            Snapshot(value.Deviation),
            value.Reason,
            value.AppliedAtUtc);

    private static SessionReviewSnapshot Snapshot(SessionReview value) =>
        new(
            value.Id,
            value.SessionId,
            value.MeaningfulProgress,
            value.Helpfulness,
            value.Note,
            value.MarkGoalComplete,
            value.SubmittedAtUtc);

    private static Intervention RehydrateIntervention(InterventionSnapshot value)
    {
        Guard.Identifier(value.Id, nameof(value.Id));
        Guard.NonNegative(value.AdmittedAt, nameof(value.AdmittedAt));
        Guard.NonNegative(value.DisputedDuration, nameof(value.DisputedDuration));
        var evaluation = RehydrateEvaluation(value.Evaluation);
        if (evaluation.Decision != ReasoningDecision.BeginRecoveryCheckIn)
        {
            throw new DomainRuleViolationException("An Intervention requires an Intervention evaluation.");
        }

        return new(
            value.Id,
            evaluation,
            value.AdmittedAt,
            value.AdmittedAtUtc,
            value.DisputedDuration);
    }

    private static ReasoningEvaluation RehydrateEvaluation(ReasoningEvaluationSnapshot value)
    {
        var episode = value.EvidenceEpisode is null ? null : RehydrateEpisode(value.EvidenceEpisode);
        return ReasoningEvaluation.Rehydrate(
            value.Id,
            value.SessionId,
            value.SessionVersion,
            value.Decision,
            episode,
            value.Rationale,
            value.EvaluatedAtUtc);
    }

    private static EvidenceEpisode RehydrateEpisode(EvidenceEpisodeSnapshot value) =>
        EvidenceEpisode.Rehydrate(
            value.Id,
            value.SessionId,
            RehydrateDeviation(value.Deviation),
            value.Observations.Select(x =>
                ObservationReference.Create(x.ObservationId, x.SessionId, x.CapturedAt)));

    private static DeviationReference RehydrateDeviation(DeviationReferenceSnapshot value) =>
        value.ListedDeviationId is { } listed
            ? DeviationReference.Listed(listed)
            : DeviationReference.Unlisted(value.UnlistedDescription ?? "");

    private static RecoveryWindow RehydrateRecoveryWindow(RecoveryWindowSnapshot value)
    {
        Guard.NonNegative(value.StartedAt, nameof(value.StartedAt));
        Guard.NonNegative(value.EndsAt, nameof(value.EndsAt));
        return new(value.StartedAt, value.EndsAt, RehydrateDeviation(value.Deviation));
    }

    private static BehaviorClarification RehydrateClarification(BehaviorClarificationSnapshot value) =>
        new(
            Guard.Identifier(value.Id, nameof(value.Id)),
            Guard.Identifier(value.SessionId, nameof(value.SessionId)),
            Guard.Identifier(value.InterventionId, nameof(value.InterventionId)),
            Guard.Required(value.Explanation, nameof(value.Explanation)),
            value.ClarifiedAtUtc);

    private static DeviationOverride RehydrateOverride(DeviationOverrideSnapshot value) =>
        new(
            Guard.Identifier(value.Id, nameof(value.Id)),
            Guard.Identifier(value.SessionId, nameof(value.SessionId)),
            RehydrateDeviation(value.Deviation),
            Guard.Required(value.Reason, nameof(value.Reason)),
            value.AppliedAtUtc);

    private static SessionReview RehydrateReview(SessionReviewSnapshot value) =>
        new(
            Guard.Identifier(value.Id, nameof(value.Id)),
            Guard.Identifier(value.SessionId, nameof(value.SessionId)),
            value.MeaningfulProgress,
            Guard.DefinedEnum(value.Helpfulness, nameof(value.Helpfulness)),
            Guard.Optional(value.Note),
            value.MarkGoalComplete,
            value.SubmittedAtUtc);

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
        EnsureOverrideDoesNotExist(deviation);

        var value = CreateOverride(deviation, Guard.Required(reason, nameof(reason)));
        _overrides.Add(value);
        return value;
    }

    private DeviationOverride CreateOverride(DeviationReference deviation, string reason) =>
        new(Guid.NewGuid(), Id, deviation, reason, _clock.UtcNow);

    private void EnsureOverrideDoesNotExist(DeviationReference deviation)
    {
        if (_overrides.Any(x => x.Deviation == deviation))
        {
            throw new DomainRuleViolationException("The Deviation Override already exists.");
        }
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
        CurrentRecoveryWindow = null;
        EndedAtUtc = UtcAt(endedAt);
        _goal.EndSession(Id);
    }

    private void EndEarlyAt(EndedEarlyReason reason, TimeSpan endedAt)
    {
        PauseIfRunning(endedAt);
        _breakEndsAt = null;
        _responseDeadline = null;
        _monitoringDeadline = null;
        _monitoringUnavailableAt = null;
        CurrentRecoveryWindow = null;
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
