using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Web.Presentation;

public sealed record SessionReviewPageView(
    Guid SessionId,
    Guid GoalId,
    string GoalTitle,
    FocusSessionState State,
    SessionReviewSnapshot? Review);

public sealed record GoalHistoryPageView(
    Guid GoalId,
    string CurrentGoalTitle,
    GoalStatus GoalStatus,
    long GoalVersion,
    StorageUsageView TotalStorage,
    IReadOnlyList<SessionHistoryPageItem> Sessions);

public sealed record SessionHistoryPageItem(
    Guid Id,
    FocusSessionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    EndedEarlyReason? EndedEarlyReason,
    string ContractGoalTitle,
    string? ContractGoalDescription,
    TimeSpan TargetFocusDuration,
    IReadOnlyList<ScheduledBreakInput> ScheduledBreaks,
    string DeviationProfileName,
    IReadOnlyList<DeviationView> Deviations,
    ReasoningMode ReasoningMode,
    Sensitivity Sensitivity,
    DateTimeOffset ConfirmedAtUtc,
    SessionReviewSnapshot? Review,
    StorageUsageView Storage);

public sealed class PostSessionPresentation(
    IGoalKeeperRepository repository,
    IDbContextFactory<GoalKeeperDbContext> dbFactory,
    IClock clock)
{
    public async Task<SessionReviewPageView?> GetReviewAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await repository.GetSessionAsync(sessionId, cancellationToken);
        if (runtime is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var goalTitle = await db.SessionContracts.AsNoTracking()
            .Where(contract => contract.Id == runtime.ContractId)
            .Select(contract => contract.GoalTitle)
            .SingleAsync(cancellationToken);
        return new(
            runtime.Id,
            runtime.GoalId,
            goalTitle,
            runtime.State,
            runtime.Runtime.Review);
    }

    public async Task<SessionReviewSnapshot> SubmitReviewAsync(
        Guid sessionId,
        bool meaningfulProgress,
        InterventionHelpfulness helpfulness,
        string? note,
        bool markGoalComplete,
        CancellationToken cancellationToken = default)
    {
        var runtime = await repository.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException("Focus Session not found.");
        var goalView = await repository.GetGoalAsync(runtime.GoalId, cancellationToken)
            ?? throw new KeyNotFoundException("Goal not found.");
        var contractView = await GetContractAsync(runtime.ContractId, cancellationToken);
        var goal = Goal.Rehydrate(
            goalView.Id,
            goalView.Title,
            goalView.Description,
            goalView.Status,
            goalView.CreatedAtUtc,
            goalView.CompletedAtUtc);
        var contract = RehydrateContract(contractView);
        var session = FocusSession.Rehydrate(goal, contract, runtime.Runtime, clock);
        var review = session.SubmitReview(
            meaningfulProgress,
            helpfulness,
            note,
            markGoalComplete);
        var reviewSnapshot = session.CreateSnapshot().Review!;
        await repository.UpdateSessionAsync(
            session.Id,
            new RuntimeMutation(
                runtime.Version,
                session.CreateSnapshot(),
                [new RuntimeAuditWrite(
                    review.SubmittedAtUtc,
                    "session.review_submitted",
                    runtime.State,
                    runtime.State,
                    "{}")])
            {
                GoalCompletedAtUtc = markGoalComplete && goalView.Status == GoalStatus.Active
                    ? review.SubmittedAtUtc
                    : null
            },
            cancellationToken);
        return reviewSnapshot;
    }

    public async Task<GoalHistoryPageView?> GetHistoryAsync(
        Guid goalId,
        CancellationToken cancellationToken = default)
    {
        var goal = await repository.GetGoalAsync(goalId, cancellationToken);
        if (goal is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sessions = await db.FocusSessions.AsNoTracking()
            .Where(session => session.GoalId == goalId)
            .Include(session => session.Contract).ThenInclude(contract => contract.Breaks)
            .Include(session => session.Contract).ThenInclude(contract => contract.Deviations)
            .Include(session => session.Review)
            .Include(session => session.Snapshots)
            .ToListAsync(cancellationToken);
        var totalStorage = await repository.GetStorageUsageAsync(cancellationToken: cancellationToken);
        return new(
            goal.Id,
            goal.Title,
            goal.Status,
            goal.Version,
            totalStorage,
            sessions.OrderByDescending(session => session.StartedAtUtc)
                .Select(ToHistoryItem)
                .ToArray());
    }

    public Task DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        repository.DeleteSessionAsync(sessionId, cancellationToken);

    public Task DeleteGoalAsync(
        Guid goalId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        repository.DeleteGoalAsync(goalId, expectedVersion, cancellationToken);

    private async Task<SessionContractView> GetContractAsync(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var contract = await db.SessionContracts.AsNoTracking()
            .Include(value => value.Breaks)
            .Include(value => value.Deviations)
            .SingleOrDefaultAsync(value => value.Id == contractId, cancellationToken)
            ?? throw new KeyNotFoundException("Session Contract not found.");
        return ToContractView(contract);
    }

    private static SessionHistoryPageItem ToHistoryItem(FocusSessionEntity session)
    {
        var contract = session.Contract;
        var review = session.Review is null
            ? null
            : new SessionReviewSnapshot(
                session.Review.Id,
                session.Review.SessionId,
                session.Review.MeaningfulProgress,
                Enum.Parse<InterventionHelpfulness>(session.Review.Helpfulness),
                session.Review.Note,
                session.Review.MarkGoalComplete,
                session.Review.SubmittedAtUtc);
        return new(
            session.Id,
            Enum.Parse<FocusSessionState>(session.State),
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.EndReason is null
                ? null
                : Enum.Parse<EndedEarlyReason>(session.EndReason),
            contract.GoalTitle,
            contract.GoalDescription,
            TimeSpan.FromTicks(contract.TargetFocusTicks),
            contract.Breaks.OrderBy(value => value.SortOrder)
                .Select(value => new ScheduledBreakInput(
                    TimeSpan.FromTicks(value.ActiveFocusOffsetTicks),
                    TimeSpan.FromTicks(value.DurationTicks)))
                .ToArray(),
            contract.DeviationProfileName,
            contract.Deviations.OrderBy(value => value.SortOrder)
                .Select(value => new DeviationView(
                    value.DeviationId,
                    value.Description,
                    Enum.Parse<VisualObservability>(value.Observability)))
                .ToArray(),
            Enum.Parse<ReasoningMode>(contract.ReasoningMode),
            Enum.Parse<Sensitivity>(contract.Sensitivity),
            contract.ConfirmedAtUtc,
            review,
            new(
                session.Snapshots.Sum(snapshot => snapshot.StoredBytes),
                session.Snapshots.Count));
    }

    private static SessionContractView ToContractView(ContractEntity contract) =>
        new(
            contract.Id,
            contract.GoalId,
            contract.GoalTitle,
            contract.GoalDescription,
            TimeSpan.FromTicks(contract.TargetFocusTicks),
            contract.Breaks.OrderBy(value => value.SortOrder)
                .Select(value => new ScheduledBreakInput(
                    TimeSpan.FromTicks(value.ActiveFocusOffsetTicks),
                    TimeSpan.FromTicks(value.DurationTicks)))
                .ToArray(),
            contract.DeviationProfileId,
            contract.DeviationProfileName,
            contract.Deviations.OrderBy(value => value.SortOrder)
                .Select(value => new DeviationView(
                    value.DeviationId,
                    value.Description,
                    Enum.Parse<VisualObservability>(value.Observability)))
                .ToArray(),
            Enum.Parse<ReasoningMode>(contract.ReasoningMode),
            Enum.Parse<Sensitivity>(contract.Sensitivity),
            contract.ConfirmedAtUtc);

    private static SessionContract RehydrateContract(SessionContractView contract) =>
        SessionContract.Rehydrate(
            contract.Id,
            new GoalSnapshot(
                contract.GoalId,
                contract.GoalTitle,
                contract.GoalDescription),
            contract.TargetFocusDuration,
            contract.ScheduledBreaks.Select(value =>
                ScheduledBreak.Create(value.ActiveFocusOffset, value.Duration)),
            new DeviationProfileSnapshot(
                contract.DeviationProfileId,
                contract.DeviationProfileName,
                contract.Deviations.Select(value => new DeviationSnapshot(
                    value.Id,
                    value.Description,
                    value.Observability)).ToArray()),
            contract.ReasoningMode,
            contract.Sensitivity,
            contract.ConfirmedAtUtc);
}
