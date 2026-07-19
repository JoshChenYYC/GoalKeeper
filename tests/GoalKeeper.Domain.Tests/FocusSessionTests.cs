using GoalKeeper.Domain;

namespace GoalKeeper.Domain.Tests;

public sealed class FocusSessionTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Confirmed_contract_is_an_immutable_snapshot()
    {
        var goal = Goal.Create("Write", "Draft the introduction", _clock);
        var deviation = Deviation.Create("Sustained attention to a phone", VisualObservability.Observable);
        var profile = DeviationProfile.Create("Default", [deviation], _clock);
        var contract = SessionContract.Confirm(
            goal,
            TimeSpan.FromMinutes(25),
            [ScheduledBreak.Create(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2))],
            profile,
            ReasoningMode.ProfileOnly,
            Sensitivity.Balanced,
            _clock);

        goal.Update("Revised", null, _clock);
        profile.UpdateDeviation(deviation.Id, "Leaving the camera view", _clock);

        Assert.Equal("Write", contract.Goal.Title);
        Assert.Equal("Sustained attention to a phone", contract.DeviationProfile.Deviations.Single().Description);
        Assert.Throws<DomainRuleViolationException>(() =>
            SessionContract.Confirm(goal, TimeSpan.FromMinutes(10),
                [ScheduledBreak.Create(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1))],
                profile, ReasoningMode.ProfileOnly, Sensitivity.Balanced, _clock));
    }

    [Fact]
    public void Delayed_advance_crosses_break_and_fulfills_at_active_target()
    {
        var (_, session, _) = StartSession(
            TimeSpan.FromMinutes(10),
            [ScheduledBreak.Create(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(2))]);

        _clock.Advance(TimeSpan.FromMinutes(12));
        session.Advance();

        Assert.Equal(FocusSessionState.Fulfilled, session.State);
        Assert.Equal(TimeSpan.FromMinutes(10), session.ActiveFocusElapsed);
        Assert.Equal(_clock.UtcNow, session.EndedAtUtc);
    }

    [Fact]
    public void Intervention_does_not_move_projection_until_recommitment()
    {
        var (_, session, deviation) = StartSession();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var originalProjection = session.ProjectedEndUtc;
        var evaluation = InterventionEvaluation(session, deviation, TimeSpan.FromMinutes(3));

        session.AdmitIntervention(evaluation);
        Assert.Equal(FocusSessionState.RecoveryCheckIn, session.State);
        Assert.Equal(originalProjection, session.ProjectedEndUtc);

        _clock.Advance(TimeSpan.FromMinutes(1));
        session.Recommit();

        Assert.Equal(FocusSessionState.RecoveryWindow, session.State);
        Assert.Equal(originalProjection + TimeSpan.FromMinutes(4), session.ProjectedEndUtc);
        Assert.Equal(TimeSpan.FromMinutes(2), session.ActiveFocusElapsed);
    }

    [Fact]
    public void Clarification_restores_disputed_focus_and_only_adds_check_in_pause()
    {
        var (_, session, deviation) = StartSession();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var originalProjection = session.ProjectedEndUtc;
        session.AdmitIntervention(InterventionEvaluation(session, deviation, TimeSpan.FromMinutes(3)));

        _clock.Advance(TimeSpan.FromMinutes(1));
        session.ClarifyBehavior("This research supports the Goal.", applyRemainderOverride: true);

        Assert.Equal(FocusSessionState.Focusing, session.State);
        Assert.Equal(TimeSpan.FromMinutes(5), session.ActiveFocusElapsed);
        Assert.Equal(originalProjection + TimeSpan.FromMinutes(1), session.ProjectedEndUtc);
        Assert.Single(session.DeviationOverrides);
    }

    [Fact]
    public void Recovery_escalation_is_bounded_and_explicit_continuation_resets_it()
    {
        var policy = new FocusSessionPolicy(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), 2, 3);
        var (_, session, deviation) = StartSession(policy: policy);

        AdmitAndRecommit(session, deviation);
        _clock.Advance(TimeSpan.FromSeconds(5));
        session.AdmitIntervention(InterventionEvaluation(session, deviation, TimeSpan.Zero));
        session.Recommit();
        _clock.Advance(TimeSpan.FromSeconds(5));
        session.AdmitIntervention(InterventionEvaluation(session, deviation, TimeSpan.Zero));

        Assert.True(session.RequiresFinalEscalation);
        Assert.Throws<DomainRuleViolationException>(session.Recommit);

        session.ConfirmContinuationAfterEscalation();
        Assert.Equal(0, session.ConsecutiveUnsuccessfulRecoveries);
        Assert.Equal(FocusSessionState.RecoveryWindow, session.State);
    }

    [Fact]
    public void Awaiting_response_and_monitoring_outage_end_after_their_timeouts()
    {
        var (_, noResponse, deviation) = StartSession();
        noResponse.AdmitIntervention(InterventionEvaluation(noResponse, deviation, TimeSpan.Zero));
        noResponse.ReportNoResponse();
        _clock.Advance(TimeSpan.FromMinutes(1));
        noResponse.Advance();
        Assert.Equal(FocusSessionState.EndedEarly, noResponse.State);
        Assert.Equal(EndedEarlyReason.NoResponse, noResponse.EndedEarlyReason);

        var (_, outage, _) = StartSession();
        outage.ReportMonitoringUnavailable();
        _clock.Advance(TimeSpan.FromSeconds(30));
        outage.Advance();
        Assert.Equal(FocusSessionState.EndedEarly, outage.State);
        Assert.Equal(EndedEarlyReason.MonitoringFailure, outage.EndedEarlyReason);
    }

    [Theory]
    [InlineData(FocusSessionState.Focusing)]
    [InlineData(FocusSessionState.ScheduledBreak)]
    [InlineData(FocusSessionState.RecoveryCheckIn)]
    [InlineData(FocusSessionState.RecoveryWindow)]
    [InlineData(FocusSessionState.AwaitingResponse)]
    [InlineData(FocusSessionState.MonitoringUnavailable)]
    public void Application_interruption_ends_every_nonterminal_state_and_unlocks_the_goal(
        FocusSessionState state)
    {
        var (goal, session, deviation) = StartSession(
            breaks:
            [
                ScheduledBreak.Create(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1))
            ]);
        MoveTo(state, session, deviation);
        var version = session.Version;

        session.EndAfterApplicationInterruption();

        var runtime = session.CreateSnapshot();
        Assert.Equal(FocusSessionState.EndedEarly, runtime.State);
        Assert.Equal(EndedEarlyReason.ApplicationInterrupted, runtime.EndedEarlyReason);
        Assert.Equal(version + 1, runtime.Version);
        Assert.Equal(_clock.UtcNow, runtime.EndedAtUtc);
        Assert.Null(runtime.Timer.RunningSince);
        Assert.Null(runtime.BreakEndsAt);
        Assert.Null(runtime.CurrentRecoveryWindow);
        Assert.Null(runtime.ResponseDeadline);
        Assert.Null(runtime.MonitoringUnavailableAt);
        Assert.Null(runtime.MonitoringDeadline);
        var restoredGoal = Goal.Rehydrate(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.Status,
            goal.CreatedAtUtc,
            goal.CompletedAtUtc);
        var restored = FocusSession.Rehydrate(
            restoredGoal,
            session.Contract,
            runtime,
            _clock);
        Assert.Equal(EndedEarlyReason.ApplicationInterrupted, restored.EndedEarlyReason);

        goal.Update("Changed", null, _clock);
        Assert.Equal("Changed", goal.Title);
    }

    [Fact]
    public void Explicit_goal_completion_fulfills_session_and_review_is_optional_once()
    {
        var (goal, session, _) = StartSession();

        session.CompleteGoal();
        var review = session.SubmitReview(true, InterventionHelpfulness.NotApplicable, "Good progress", false);

        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Equal(FocusSessionState.Fulfilled, session.State);
        Assert.Same(review, session.Review);
        Assert.Throws<DomainRuleViolationException>(() =>
            session.SubmitReview(true, InterventionHelpfulness.Helpful, null, false));
    }

    [Theory]
    [InlineData("recommit")]
    [InlineData("clarify")]
    [InlineData("return")]
    [InlineData("restore-monitoring")]
    public void Invalid_state_commands_are_rejected_without_version_change(string command)
    {
        var (_, session, _) = StartSession();
        var version = session.Version;

        Assert.Throws<DomainRuleViolationException>(() => command switch
        {
            "recommit" => Run(session.Recommit),
            "clarify" => session.ClarifyBehavior("Consistent"),
            "return" => Run(session.ReturnToRecoveryCheckIn),
            "restore-monitoring" => Run(session.RestoreMonitoring),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(version, session.Version);
    }

    [Fact]
    public void Recovery_window_expiry_resets_cycle_and_monitoring_restore_preserves_focus()
    {
        var (_, session, deviation) = StartSession();
        _clock.Advance(TimeSpan.FromMinutes(2));
        AdmitAndRecommit(session, deviation);
        _clock.Advance(TimeSpan.FromMinutes(1));
        session.Advance();

        Assert.Equal(FocusSessionState.Focusing, session.State);
        Assert.Equal(0, session.ConsecutiveUnsuccessfulRecoveries);
        var focus = session.ActiveFocusElapsed;
        var projection = session.ProjectedEndUtc;

        session.ReportMonitoringUnavailable();
        _clock.Advance(TimeSpan.FromSeconds(10));
        session.RestoreMonitoring();

        Assert.Equal(focus, session.ActiveFocusElapsed);
        Assert.Equal(projection + TimeSpan.FromSeconds(10), session.ProjectedEndUtc);
    }

    [Fact]
    public void Profile_only_rejects_unlisted_evidence_and_override_blocks_repetition()
    {
        var (_, session, deviation) = StartSession();
        var observation = ObservationReference.Create("unlisted", session.Id, _clock.MonotonicNow);
        var unlisted = EvidenceEpisode.Create(session.Id, DeviationReference.Unlisted("Unlisted"), [observation]);
        var evaluation = ReasoningEvaluation.ProposeIntervention(
            session.Id, session.Version, unlisted, "Rationale", _clock);

        Assert.Throws<DomainRuleViolationException>(() => session.AdmitIntervention(evaluation));

        session.AdmitIntervention(InterventionEvaluation(session, deviation, TimeSpan.Zero));
        session.ClarifyBehavior("Allowed research", applyRemainderOverride: true);
        var repeated = InterventionEvaluation(session, deviation, TimeSpan.Zero);
        Assert.Throws<DomainRuleViolationException>(() => session.AdmitIntervention(repeated));
    }

    [Fact]
    public void Active_session_locks_goal_edits_and_unlocks_after_user_end()
    {
        var (goal, session, _) = StartSession();
        Assert.Throws<DomainRuleViolationException>(() => goal.Update("Changed", null, _clock));
        Assert.Throws<DomainRuleViolationException>(goal.EnsureCanDelete);

        session.EndEarlyByUser();
        goal.Update("Changed", null, _clock);

        Assert.Equal("Changed", goal.Title);
        Assert.Equal(EndedEarlyReason.UserRequest, session.EndedEarlyReason);
    }

    private (Goal Goal, FocusSession Session, Deviation Deviation) StartSession(
        TimeSpan? target = null,
        IReadOnlyCollection<ScheduledBreak>? breaks = null,
        FocusSessionPolicy? policy = null)
    {
        var goal = Goal.Create("Write", null, _clock);
        var deviation = Deviation.Create("Phone", VisualObservability.Observable);
        var profile = DeviationProfile.Create("Default", [deviation], _clock);
        var contract = SessionContract.Confirm(
            goal,
            target ?? TimeSpan.FromMinutes(25),
            breaks ?? [],
            profile,
            ReasoningMode.ProfileOnly,
            Sensitivity.Balanced,
            _clock);
        return (goal, FocusSession.Start(goal, contract, true, _clock, policy), deviation);
    }

    private ReasoningEvaluation InterventionEvaluation(
        FocusSession session,
        Deviation deviation,
        TimeSpan evidenceAge)
    {
        var capturedAt = _clock.MonotonicNow - evidenceAge;
        var observation = ObservationReference.Create(
            $"obs-{Guid.NewGuid():N}", session.Id, capturedAt);
        var episode = EvidenceEpisode.Create(
            session.Id, DeviationReference.Listed(deviation.Id), [observation]);
        return ReasoningEvaluation.ProposeIntervention(
            session.Id, session.Version, episode, "Visible evidence may conflict.", _clock);
    }

    private void AdmitAndRecommit(FocusSession session, Deviation deviation)
    {
        session.AdmitIntervention(InterventionEvaluation(session, deviation, TimeSpan.Zero));
        session.Recommit();
    }

    private void MoveTo(
        FocusSessionState state,
        FocusSession session,
        Deviation deviation)
    {
        switch (state)
        {
            case FocusSessionState.Focusing:
                return;
            case FocusSessionState.ScheduledBreak:
                _clock.Advance(TimeSpan.FromMinutes(1));
                session.Advance();
                return;
            case FocusSessionState.RecoveryCheckIn:
                session.AdmitIntervention(
                    InterventionEvaluation(session, deviation, TimeSpan.Zero));
                return;
            case FocusSessionState.RecoveryWindow:
                AdmitAndRecommit(session, deviation);
                return;
            case FocusSessionState.AwaitingResponse:
                session.AdmitIntervention(
                    InterventionEvaluation(session, deviation, TimeSpan.Zero));
                session.ReportNoResponse();
                return;
            case FocusSessionState.MonitoringUnavailable:
                session.ReportMonitoringUnavailable();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static object? Run(Action action)
    {
        action();
        return null;
    }
}
