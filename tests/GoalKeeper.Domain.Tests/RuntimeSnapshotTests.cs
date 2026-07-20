using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Domain;

namespace GoalKeeper.Domain.Tests;

public sealed class RuntimeSnapshotTests
{
    [Fact]
    public void V1_runtime_snapshot_without_accountability_message_remains_readable()
    {
        var clock = new FakeClock();
        var (goal, contract, session, deviation) = Start(clock);
        session.AdmitIntervention(InterventionEvaluation(session, deviation, clock));
        var node = JsonNode.Parse(
            JsonSerializer.Serialize(session.CreateSnapshot()))!;
        var evaluation = node["ActiveIntervention"]!["Evaluation"]!.AsObject();
        Assert.True(evaluation.Remove("AccountabilityMessage"));

        var stored = JsonSerializer.Deserialize<FocusSessionRuntimeSnapshot>(
            node.ToJsonString())!;
        var restoredGoal = Goal.Rehydrate(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.Status,
            goal.CreatedAtUtc,
            goal.CompletedAtUtc);
        var restored = FocusSession.Rehydrate(
            restoredGoal,
            contract,
            stored,
            clock);

        Assert.Equal(FocusSessionState.RecoveryCheckIn, restored.State);
        Assert.Null(restored.CreateSnapshot()
            .ActiveIntervention!
            .Evaluation
            .AccountabilityMessage);
    }

    [Theory]
    [InlineData(FocusSessionState.Focusing)]
    [InlineData(FocusSessionState.ScheduledBreak)]
    [InlineData(FocusSessionState.RecoveryCheckIn)]
    [InlineData(FocusSessionState.RecoveryWindow)]
    [InlineData(FocusSessionState.AwaitingResponse)]
    [InlineData(FocusSessionState.MonitoringUnavailable)]
    [InlineData(FocusSessionState.Fulfilled)]
    [InlineData(FocusSessionState.EndedEarly)]
    public void Every_runtime_state_round_trips_through_a_serialized_snapshot(
        FocusSessionState requestedState)
    {
        var clock = new FakeClock();
        var (goal, contract, session, deviation) = Start(clock, includeBreak: true);
        MoveTo(requestedState, session, deviation, clock);
        var before = session.CreateSnapshot();
        var json = JsonSerializer.Serialize(before);
        var stored = JsonSerializer.Deserialize<FocusSessionRuntimeSnapshot>(json)!;
        var restoredGoal = Goal.Rehydrate(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.Status,
            goal.CreatedAtUtc,
            goal.CompletedAtUtc);

        var restored = FocusSession.Rehydrate(restoredGoal, contract, stored, clock);

        Assert.Equal(requestedState, restored.State);
        Assert.Equal(session.Version, restored.Version);
        Assert.Equal(session.ActiveFocusElapsed, restored.ActiveFocusElapsed);
        Assert.Equal(json, JsonSerializer.Serialize(restored.CreateSnapshot()));
    }

    [Fact]
    public void Rejected_intervention_is_completely_atomic()
    {
        var clock = new FakeClock();
        clock.Advance(TimeSpan.FromMinutes(1));
        var (_, _, session, deviation) = Start(clock);
        clock.Advance(TimeSpan.FromMinutes(2));
        var invalidObservation = ObservationReference.Create(
            "before-session",
            session.Id,
            TimeSpan.Zero);
        var episode = EvidenceEpisode.Create(
            session.Id,
            DeviationReference.Listed(deviation.Id),
            [invalidObservation]);
        var evaluation = ReasoningEvaluation.ProposeIntervention(
            session.Id,
            session.Version,
            episode,
            "Invalid interval",
            clock);
        var before = JsonSerializer.Serialize(session.CreateSnapshot());

        Assert.Throws<DomainRuleViolationException>(() => session.AdmitIntervention(evaluation));

        Assert.Equal(before, JsonSerializer.Serialize(session.CreateSnapshot()));
    }

    [Fact]
    public void Override_and_review_are_versioned_durable_mutations()
    {
        var clock = new FakeClock();
        var (_, _, session, deviation) = Start(clock);
        session.AdmitIntervention(InterventionEvaluation(session, deviation, clock));
        var interventionVersion = session.Version;

        session.ApplyRemainderDeviationOverride("This behavior is goal-consistent.");

        Assert.Equal(interventionVersion + 1, session.Version);
        session.EndEarlyByUser();
        var terminalVersion = session.Version;

        session.SubmitReview(true, InterventionHelpfulness.Mixed, "Useful", false);

        Assert.Equal(terminalVersion + 1, session.Version);
    }

    [Fact]
    public void Rehydrate_rejects_inconsistent_state_without_locking_the_goal()
    {
        var clock = new FakeClock();
        var (goal, contract, session, _) = Start(clock);
        var invalid = session.CreateSnapshot() with
        {
            State = FocusSessionState.ScheduledBreak,
            BreakEndsAt = null
        };
        var restoredGoal = Goal.Rehydrate(
            goal.Id,
            goal.Title,
            goal.Description,
            GoalStatus.Active,
            goal.CreatedAtUtc,
            null);

        Assert.Throws<DomainRuleViolationException>(() =>
            FocusSession.Rehydrate(restoredGoal, contract, invalid, clock));

        restoredGoal.Update("Still editable", null, clock);
        Assert.Equal("Still editable", restoredGoal.Title);
    }

    private static (Goal Goal, SessionContract Contract, FocusSession Session, Deviation Deviation) Start(
        FakeClock clock,
        bool includeBreak = false)
    {
        var goal = Goal.Create("Write", null, clock);
        var deviation = Deviation.Create("Phone", VisualObservability.Observable);
        var profile = DeviationProfile.Create("Default", [deviation], clock);
        var contract = SessionContract.Confirm(
            goal,
            TimeSpan.FromMinutes(25),
            includeBreak
                ? [ScheduledBreak.Create(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))]
                : [],
            profile,
            ReasoningMode.ProfileOnly,
            Sensitivity.Balanced,
            clock);
        return (goal, contract, FocusSession.Start(goal, contract, true, clock), deviation);
    }

    private static void MoveTo(
        FocusSessionState requestedState,
        FocusSession session,
        Deviation deviation,
        FakeClock clock)
    {
        switch (requestedState)
        {
            case FocusSessionState.Focusing:
                return;
            case FocusSessionState.ScheduledBreak:
                clock.Advance(TimeSpan.FromMinutes(1));
                session.Advance();
                return;
            case FocusSessionState.RecoveryCheckIn:
                session.AdmitIntervention(InterventionEvaluation(session, deviation, clock));
                return;
            case FocusSessionState.RecoveryWindow:
                session.AdmitIntervention(InterventionEvaluation(session, deviation, clock));
                session.Recommit();
                return;
            case FocusSessionState.AwaitingResponse:
                session.AdmitIntervention(InterventionEvaluation(session, deviation, clock));
                session.ReportNoResponse();
                return;
            case FocusSessionState.MonitoringUnavailable:
                session.ReportMonitoringUnavailable();
                return;
            case FocusSessionState.Fulfilled:
                session.CompleteGoal();
                return;
            case FocusSessionState.EndedEarly:
                session.EndEarlyByUser();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(requestedState));
        }
    }

    private static ReasoningEvaluation InterventionEvaluation(
        FocusSession session,
        Deviation deviation,
        FakeClock clock)
    {
        var observation = ObservationReference.Create(
            $"obs-{Guid.NewGuid():N}",
            session.Id,
            clock.MonotonicNow);
        var episode = EvidenceEpisode.Create(
            session.Id,
            DeviationReference.Listed(deviation.Id),
            [observation]);
        return ReasoningEvaluation.ProposeIntervention(
            session.Id,
            session.Version,
            episode,
            "Visible evidence may conflict.",
            clock);
    }
}
