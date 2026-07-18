using GoalKeeper.Domain;

namespace GoalKeeper.Application.Runtime;

internal static class RuntimeAggregateFactory
{
    public static Goal RehydrateGoal(GoalView view) =>
        Goal.Rehydrate(
            view.Id,
            view.Title,
            view.Description,
            view.Status,
            view.CreatedAtUtc,
            view.CompletedAtUtc);

    public static SessionContract RehydrateContract(SessionContractView view) =>
        SessionContract.Rehydrate(
            view.Id,
            new GoalSnapshot(view.GoalId, view.GoalTitle, view.GoalDescription),
            view.TargetFocusDuration,
            view.ScheduledBreaks.Select(value =>
                ScheduledBreak.Create(value.ActiveFocusOffset, value.Duration)),
            new DeviationProfileSnapshot(
                view.DeviationProfileId,
                view.DeviationProfileName,
                view.Deviations.Select(value =>
                    new DeviationSnapshot(
                        value.Id,
                        value.Description,
                        value.Observability)).ToArray()),
            view.ReasoningMode,
            view.Sensitivity,
            view.ConfirmedAtUtc);

    public static FocusSession RehydrateSession(
        GoalView goal,
        SessionContractView contract,
        FocusSessionRuntimeSnapshot runtime,
        IClock clock) =>
        FocusSession.Rehydrate(
            RehydrateGoal(goal),
            RehydrateContract(contract),
            runtime,
            clock);

    public static FocusSessionPolicy CreatePolicy(ApplicationSettingsView settings) =>
        new(
            settings.RecoveryWindow,
            settings.ResponseTimeout,
            settings.TechnicalOutageGrace,
            settings.MaximumUnsuccessfulRecoveries,
            settings.MaximumCoachingTurns);
}
