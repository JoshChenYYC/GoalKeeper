using System.Text.Json;
using GoalKeeper.Domain;

namespace GoalKeeper.Domain.Tests;

public sealed class CommandTransitionMatrixTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var state in Enum.GetValues<FocusSessionState>())
        {
            foreach (var command in Enum.GetValues<SessionCommand>())
            {
                yield return [state, command, IsAllowed(state, command)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Command_state_matrix_is_exhaustive_and_rejections_are_atomic(
        FocusSessionState state,
        SessionCommand command,
        bool allowed)
    {
        var fixture = CreateFixture(state, command);
        var before = JsonSerializer.Serialize(fixture.Session.CreateSnapshot());

        if (allowed)
        {
            Run(command, fixture);
            _ = fixture.Session.CreateSnapshot();
        }
        else
        {
            Assert.Throws<DomainRuleViolationException>(() => Run(command, fixture));
            Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.CreateSnapshot()));
        }
    }

    private static bool IsAllowed(FocusSessionState state, SessionCommand command) =>
        command switch
        {
            SessionCommand.Advance => true,
            SessionCommand.AdmitIntervention =>
                state is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow,
            SessionCommand.ClarifyBehavior or
            SessionCommand.ApplyRemainderOverride or
            SessionCommand.Recommit or
            SessionCommand.ReportNoResponse or
            SessionCommand.ConfirmContinuationAfterEscalation =>
                state == FocusSessionState.RecoveryCheckIn,
            SessionCommand.ReturnToRecoveryCheckIn =>
                state == FocusSessionState.AwaitingResponse,
            SessionCommand.ReportMonitoringUnavailable =>
                state is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow,
            SessionCommand.RestoreMonitoring =>
                state == FocusSessionState.MonitoringUnavailable,
            SessionCommand.CompleteGoal =>
                state is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow,
            SessionCommand.EndEarly =>
                state is not (FocusSessionState.Fulfilled or FocusSessionState.EndedEarly),
            SessionCommand.SubmitReview =>
                state is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private static Fixture CreateFixture(
        FocusSessionState state,
        SessionCommand command)
    {
        var clock = new FakeClock();
        var goal = Goal.Create("Write", null, clock);
        var deviation = Deviation.Create("Phone", VisualObservability.Observable);
        var profile = DeviationProfile.Create("Default", [deviation], clock);
        var contract = SessionContract.Confirm(
            goal,
            TimeSpan.FromMinutes(25),
            [ScheduledBreak.Create(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))],
            profile,
            ReasoningMode.ProfileOnly,
            Sensitivity.Balanced,
            clock);
        var session = FocusSession.Start(goal, contract, true, clock);
        MoveTo(state, session, deviation, clock);
        if (state == FocusSessionState.RecoveryCheckIn &&
            command == SessionCommand.ConfirmContinuationAfterEscalation)
        {
            var escalated = session.CreateSnapshot() with
            {
                ConsecutiveUnsuccessfulRecoveries =
                    session.CreateSnapshot().Policy.MaximumUnsuccessfulRecoveries,
                RequiresFinalEscalation = true
            };
            var restoredGoal = Goal.Rehydrate(
                goal.Id,
                goal.Title,
                goal.Description,
                GoalStatus.Active,
                goal.CreatedAtUtc,
                null);
            session = FocusSession.Rehydrate(restoredGoal, contract, escalated, clock);
            goal = restoredGoal;
        }

        return new(goal, session, deviation, clock);
    }

    private static void MoveTo(
        FocusSessionState state,
        FocusSession session,
        Deviation deviation,
        FakeClock clock)
    {
        switch (state)
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
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static void Run(SessionCommand command, Fixture fixture)
    {
        switch (command)
        {
            case SessionCommand.Advance:
                fixture.Session.Advance();
                break;
            case SessionCommand.AdmitIntervention:
                fixture.Session.AdmitIntervention(
                    InterventionEvaluation(fixture.Session, fixture.Deviation, fixture.Clock));
                break;
            case SessionCommand.ClarifyBehavior:
                fixture.Session.ClarifyBehavior("Goal-consistent.");
                break;
            case SessionCommand.ApplyRemainderOverride:
                fixture.Session.ApplyRemainderDeviationOverride("Allowed for this session.");
                break;
            case SessionCommand.Recommit:
                fixture.Session.Recommit();
                break;
            case SessionCommand.ConfirmContinuationAfterEscalation:
                fixture.Session.ConfirmContinuationAfterEscalation();
                break;
            case SessionCommand.ReportNoResponse:
                fixture.Session.ReportNoResponse();
                break;
            case SessionCommand.ReturnToRecoveryCheckIn:
                fixture.Session.ReturnToRecoveryCheckIn();
                break;
            case SessionCommand.ReportMonitoringUnavailable:
                fixture.Session.ReportMonitoringUnavailable();
                break;
            case SessionCommand.RestoreMonitoring:
                fixture.Session.RestoreMonitoring();
                break;
            case SessionCommand.CompleteGoal:
                fixture.Session.CompleteGoal();
                break;
            case SessionCommand.EndEarly:
                fixture.Session.EndEarlyByUser();
                break;
            case SessionCommand.SubmitReview:
                fixture.Session.SubmitReview(
                    true,
                    InterventionHelpfulness.NotApplicable,
                    null,
                    false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
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

    public enum SessionCommand
    {
        Advance,
        AdmitIntervention,
        ClarifyBehavior,
        ApplyRemainderOverride,
        Recommit,
        ConfirmContinuationAfterEscalation,
        ReportNoResponse,
        ReturnToRecoveryCheckIn,
        ReportMonitoringUnavailable,
        RestoreMonitoring,
        CompleteGoal,
        EndEarly,
        SubmitReview
    }

    private sealed record Fixture(
        Goal Goal,
        FocusSession Session,
        Deviation Deviation,
        FakeClock Clock);
}
