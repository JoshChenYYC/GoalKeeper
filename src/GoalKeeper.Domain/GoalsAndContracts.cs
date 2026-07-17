using System.Collections.ObjectModel;

namespace GoalKeeper.Domain;

public enum GoalStatus { Active, Completed }

public enum VisualObservability { Observable, PartiallyObservable, NotObservable }

public enum ReasoningMode { ProfileOnly, Exploratory }

public enum Sensitivity { Strict, Balanced, Relaxed }

public sealed class Goal
{
    private Guid? _activeSessionId;

    private Goal(Guid id, string title, string? description, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Title = title;
        Description = description;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public GoalStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static Goal Create(string title, string? description, IClock clock) =>
        new(Guid.NewGuid(), Guard.Required(title, nameof(title)), Guard.Optional(description), clock.UtcNow);

    public void Update(string title, string? description, IClock clock)
    {
        _ = clock;
        EnsureEditable();
        Title = Guard.Required(title, nameof(title));
        Description = Guard.Optional(description);
    }

    public void Complete(IClock clock)
    {
        EnsureEditable();
        CompleteAt(clock.UtcNow);
    }

    public void EnsureCanDelete() => EnsureEditable();

    internal void BeginSession(Guid sessionId)
    {
        if (Status != GoalStatus.Active)
        {
            throw new DomainRuleViolationException("A Focus Session requires an active Goal.");
        }

        if (_activeSessionId is not null)
        {
            throw new DomainRuleViolationException("The Goal already has an active Focus Session.");
        }

        _activeSessionId = sessionId;
    }

    internal void EndSession(Guid sessionId)
    {
        if (_activeSessionId == sessionId)
        {
            _activeSessionId = null;
        }
    }

    internal void CompleteFromSession(Guid sessionId, DateTimeOffset completedAtUtc)
    {
        if (_activeSessionId != sessionId)
        {
            throw new DomainRuleViolationException("The Focus Session does not own the Goal lock.");
        }

        CompleteAt(completedAtUtc);
    }

    private void CompleteAt(DateTimeOffset completedAtUtc)
    {
        Status = GoalStatus.Completed;
        CompletedAtUtc = completedAtUtc;
    }

    private void EnsureEditable()
    {
        if (_activeSessionId is not null)
        {
            throw new DomainRuleViolationException("An active Focus Session locks the Goal.");
        }

        if (Status == GoalStatus.Completed)
        {
            throw new DomainRuleViolationException("A completed Goal cannot be edited.");
        }
    }
}

public sealed record Deviation(Guid Id, string Description, VisualObservability Observability)
{
    public static Deviation Create(string description, VisualObservability observability) =>
        new(Guid.NewGuid(), Guard.Required(description, nameof(description)), observability);

    internal Deviation WithDescription(string description) =>
        this with { Description = Guard.Required(description, nameof(description)) };
}

public sealed class DeviationProfile
{
    private readonly List<Deviation> _deviations;

    private DeviationProfile(
        Guid id,
        string name,
        IEnumerable<Deviation> deviations,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        _deviations = [.. deviations];
        Deviations = new ReadOnlyCollection<Deviation>(_deviations);
        CreatedAtUtc = UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public IReadOnlyList<Deviation> Deviations { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static DeviationProfile Create(string name, IEnumerable<Deviation> deviations, IClock clock)
    {
        var values = deviations.ToArray();
        if (values.Select(x => x.Id).Distinct().Count() != values.Length)
        {
            throw new DomainRuleViolationException("Deviation identifiers must be unique.");
        }

        return new(Guid.NewGuid(), Guard.Required(name, nameof(name)), values, clock.UtcNow);
    }

    public void Rename(string name, IClock clock)
    {
        Name = Guard.Required(name, nameof(name));
        UpdatedAtUtc = clock.UtcNow;
    }

    public void AddDeviation(Deviation deviation, IClock clock)
    {
        if (_deviations.Any(x => x.Id == deviation.Id))
        {
            throw new DomainRuleViolationException("The Deviation already belongs to this profile.");
        }

        _deviations.Add(deviation);
        UpdatedAtUtc = clock.UtcNow;
    }

    public void UpdateDeviation(Guid deviationId, string description, IClock clock)
    {
        var index = _deviations.FindIndex(x => x.Id == deviationId);
        if (index < 0)
        {
            throw new DomainRuleViolationException("The Deviation does not belong to this profile.");
        }

        _deviations[index] = _deviations[index].WithDescription(description);
        UpdatedAtUtc = clock.UtcNow;
    }

    public void RemoveDeviation(Guid deviationId, IClock clock)
    {
        if (_deviations.RemoveAll(x => x.Id == deviationId) == 0)
        {
            throw new DomainRuleViolationException("The Deviation does not belong to this profile.");
        }

        UpdatedAtUtc = clock.UtcNow;
    }
}

public sealed record ScheduledBreak(TimeSpan ActiveFocusOffset, TimeSpan Duration)
{
    public static ScheduledBreak Create(TimeSpan activeFocusOffset, TimeSpan duration) =>
        new(
            Guard.Positive(activeFocusOffset, nameof(activeFocusOffset)),
            Guard.Positive(duration, nameof(duration)));
}

public sealed record GoalSnapshot(Guid Id, string Title, string? Description);

public sealed record DeviationSnapshot(Guid Id, string Description, VisualObservability Observability);

public sealed record DeviationProfileSnapshot(Guid Id, string Name, IReadOnlyList<DeviationSnapshot> Deviations);

public sealed class SessionContract
{
    private SessionContract(
        Guid id,
        GoalSnapshot goal,
        TimeSpan targetFocusDuration,
        IReadOnlyList<ScheduledBreak> scheduledBreaks,
        DeviationProfileSnapshot deviationProfile,
        ReasoningMode reasoningMode,
        Sensitivity sensitivity,
        DateTimeOffset confirmedAtUtc)
    {
        Id = id;
        Goal = goal;
        TargetFocusDuration = targetFocusDuration;
        ScheduledBreaks = scheduledBreaks;
        DeviationProfile = deviationProfile;
        ReasoningMode = reasoningMode;
        Sensitivity = sensitivity;
        ConfirmedAtUtc = confirmedAtUtc;
    }

    public Guid Id { get; }

    public GoalSnapshot Goal { get; }

    public TimeSpan TargetFocusDuration { get; }

    public IReadOnlyList<ScheduledBreak> ScheduledBreaks { get; }

    public DeviationProfileSnapshot DeviationProfile { get; }

    public ReasoningMode ReasoningMode { get; }

    public Sensitivity Sensitivity { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }

    public static SessionContract Confirm(
        Goal goal,
        TimeSpan targetFocusDuration,
        IEnumerable<ScheduledBreak> scheduledBreaks,
        DeviationProfile profile,
        ReasoningMode reasoningMode,
        Sensitivity sensitivity,
        IClock clock)
    {
        Guard.Positive(targetFocusDuration, nameof(targetFocusDuration));
        var breaks = scheduledBreaks.OrderBy(x => x.ActiveFocusOffset).ToArray();
        if (breaks.Any(x => x.ActiveFocusOffset >= targetFocusDuration))
        {
            throw new DomainRuleViolationException("A Scheduled Break must occur before the focus target.");
        }

        if (breaks.Select(x => x.ActiveFocusOffset).Distinct().Count() != breaks.Length)
        {
            throw new DomainRuleViolationException("Scheduled Break offsets must be unique.");
        }

        var deviationSnapshots = profile.Deviations
            .Select(x => new DeviationSnapshot(x.Id, x.Description, x.Observability))
            .ToArray();
        return new(
            Guid.NewGuid(),
            new GoalSnapshot(goal.Id, goal.Title, goal.Description),
            targetFocusDuration,
            Array.AsReadOnly(breaks),
            new DeviationProfileSnapshot(profile.Id, profile.Name, Array.AsReadOnly(deviationSnapshots)),
            reasoningMode,
            sensitivity,
            clock.UtcNow);
    }
}
