using GoalKeeper.Domain;

namespace GoalKeeper.Application;

public sealed class SetupWorkflow(IGoalKeeperRepository repository, IClock clock)
{
    public async Task<GoalView> GetGoalAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetGoalAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Goal not found.");

    public Task<DeviationProfileView?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        repository.GetProfileAsync(cancellationToken);

    public Task<SessionSetupView?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.GetSetupAsync(id, cancellationToken);

    public Task<IReadOnlyList<GoalView>> ListGoalsAsync(CancellationToken cancellationToken = default) =>
        repository.ListGoalsAsync(cancellationToken);

    public Task<GoalView> CreateGoalAsync(string title, string? description,
        CancellationToken cancellationToken = default)
    {
        title = Required(title, "Goal title");
        return repository.CreateGoalAsync(title, Optional(description), clock.UtcNow, cancellationToken);
    }

    public Task<GoalView> UpdateGoalAsync(Guid id, long expectedVersion, string title, string? description,
        CancellationToken cancellationToken = default) =>
        repository.UpdateGoalAsync(id, expectedVersion, Required(title, "Goal title"), Optional(description),
            clock.UtcNow, cancellationToken);

    public Task DeleteGoalAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default) =>
        repository.DeleteGoalAsync(id, expectedVersion, cancellationToken);

    public Task<DeviationProfileView> SaveProfileAsync(
        string name,
        IReadOnlyList<DeviationInput> deviations,
        CancellationToken cancellationToken = default)
    {
        if (deviations.Count == 0)
        {
            throw new DomainRuleViolationException("Accountability rules require at least one behavior to call out.");
        }

        var cleaned = deviations.Select(x => x with { Description = Required(x.Description, "Behavior") }).ToArray();
        return repository.SaveProfileAsync(Required(name, "Rules name"), cleaned, clock.UtcNow, cancellationToken);
    }

    public async Task<SessionContractDraft> PrepareAsync(Guid goalId, CancellationToken cancellationToken = default)
    {
        var goal = await repository.GetGoalAsync(goalId, cancellationToken)
            ?? throw new KeyNotFoundException("Goal not found.");
        if (goal.Status != GoalStatus.Active)
        {
            throw new DomainRuleViolationException("A Session Setup requires an active Goal.");
        }

        var latest = await repository.GetLatestContractAsync(goalId, cancellationToken);
        if (latest is not null)
        {
            return new(
                goal.Id, goal.Title, goal.Description, latest.TargetFocusDuration,
                latest.ScheduledBreaks.ToArray(), latest.DeviationProfileId, latest.DeviationProfileName,
                latest.Deviations.ToArray(), latest.ReasoningMode, latest.Sensitivity);
        }

        var profile = await repository.GetProfileAsync(cancellationToken)
            ?? throw new DomainRuleViolationException("Define accountability rules before preparing a session.");
        return new(
            goal.Id, goal.Title, goal.Description, TimeSpan.FromMinutes(25), [], profile.Id, profile.Name,
            profile.Deviations.ToArray(), ReasoningMode.ProfileOnly, Sensitivity.Balanced);
    }

    public async Task<SessionSetupView> ConfirmAsync(
        SessionContractDraft draft,
        CancellationToken cancellationToken = default)
    {
        var goal = await repository.GetGoalAsync(draft.GoalId, cancellationToken)
            ?? throw new KeyNotFoundException("Goal not found.");
        if (goal.Status != GoalStatus.Active)
        {
            throw new DomainRuleViolationException("A Session Setup requires an active Goal.");
        }

        ValidateDraft(draft);
        var confirmed = draft with { GoalTitle = goal.Title, GoalDescription = goal.Description };
        return await repository.CreateReadySetupAsync(confirmed, clock.UtcNow, cancellationToken);
    }

    private static void ValidateDraft(SessionContractDraft draft)
    {
        if (draft.TargetFocusDuration <= TimeSpan.Zero)
        {
            throw new DomainRuleViolationException("Target focus duration must be positive.");
        }

        if (draft.ScheduledBreaks.Any(x => x.ActiveFocusOffset <= TimeSpan.Zero || x.Duration <= TimeSpan.Zero ||
            x.ActiveFocusOffset >= draft.TargetFocusDuration))
        {
            throw new DomainRuleViolationException("Scheduled Breaks require positive durations before the target.");
        }

        if (draft.ScheduledBreaks.Select(x => x.ActiveFocusOffset).Distinct().Count() != draft.ScheduledBreaks.Count)
        {
            throw new DomainRuleViolationException("Scheduled Break offsets must be unique.");
        }
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainRuleViolationException($"{name} is required.")
            : value.Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
