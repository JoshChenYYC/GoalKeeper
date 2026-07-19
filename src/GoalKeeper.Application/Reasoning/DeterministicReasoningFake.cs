using GoalKeeper.Domain;

namespace GoalKeeper.Application.Reasoning;

public abstract record ReasoningFakeStep
{
    private ReasoningFakeStep()
    {
    }

    public static ReasoningFakeStep Continue() => new ContinueStep();

    public static ReasoningFakeStep ListedIntervention(Guid deviationId) =>
        new ListedInterventionStep(deviationId);

    public static ReasoningFakeStep ExploratoryIntervention(string description) =>
        new ExploratoryInterventionStep(description);

    public static ReasoningFakeStep StaleResult() => new StaleResultStep();

    public static ReasoningFakeStep InvalidReferences(params Guid[] observationIds) =>
        new InvalidReferencesStep(observationIds);

    public static ReasoningFakeStep Return(ReasoningResult result) => new ReturnStep(result);

    public static ReasoningFakeStep Throw(Exception exception) => new ThrowStep(exception);

    internal sealed record ContinueStep : ReasoningFakeStep;

    internal sealed record ListedInterventionStep(Guid DeviationId) : ReasoningFakeStep;

    internal sealed record ExploratoryInterventionStep(string Description) : ReasoningFakeStep;

    internal sealed record StaleResultStep : ReasoningFakeStep;

    internal sealed record InvalidReferencesStep(IReadOnlyList<Guid> ObservationIds) : ReasoningFakeStep;

    internal sealed record ReturnStep(ReasoningResult Result) : ReasoningFakeStep;

    internal sealed record ThrowStep(Exception Exception) : ReasoningFakeStep;
}

public sealed class DeterministicReasoningFake : IReasoningPort
{
    private readonly object _sync = new();
    private readonly Queue<ReasoningFakeStep> _steps;
    private readonly List<ReasoningRequest> _requests = [];

    public DeterministicReasoningFake(IEnumerable<ReasoningFakeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<ReasoningFakeStep>(steps);
    }

    public IReadOnlyList<ReasoningRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task<ReasoningResult> EvaluateAsync(
        ReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ReasoningFakeStep step;
        lock (_sync)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Reasoning fake has no scripted response.");
            }

            _requests.Add(request);
            step = _steps.Dequeue();
        }

        if (step is ReasoningFakeStep.ThrowStep failed)
        {
            throw failed.Exception;
        }

        var result = step switch
        {
            ReasoningFakeStep.ContinueStep => Continue(request),
            ReasoningFakeStep.ListedInterventionStep listed =>
                Intervention(request, listed.DeviationId, null),
            ReasoningFakeStep.ExploratoryInterventionStep exploratory =>
                Intervention(request, null, exploratory.Description),
            ReasoningFakeStep.StaleResultStep => Continue(request) with
            {
                Proposal = Continue(request).Proposal with
                {
                    SessionVersion = request.SessionVersion - 1
                }
            },
            ReasoningFakeStep.InvalidReferencesStep invalid => Intervention(
                request,
                request.Contract.Deviations.Count == 0 ? null : request.Contract.Deviations[0].Id,
                request.Contract.Deviations.Count == 0 ? "unlisted behavior" : null,
                invalid.ObservationIds),
            ReasoningFakeStep.ReturnStep returned => returned.Result,
            _ => throw new InvalidOperationException("Unsupported deterministic Reasoning fake step.")
        };
        return Task.FromResult(result);
    }

    private static ReasoningSuccess Continue(ReasoningRequest request) =>
        new(
            new(
                request.SessionId,
                request.SessionVersion,
                request.CurrentState,
                request.NewObservation.Id,
                ReasoningDecision.ContinueObserving,
                null,
                request.ActiveEpisodes.Concat(request.HistoricalEpisodes).ToArray()),
            Metadata());

    private static ReasoningSuccess Intervention(
        ReasoningRequest request,
        Guid? listedDeviationId,
        string? unlistedDescription,
        IReadOnlyList<Guid>? observationIds = null)
    {
        var references = observationIds?.ToArray() ?? [request.NewObservation.Id];
        var first = references[0];
        var latest = references[^1];
        var intervention = new ReasoningInterventionProposal(
            listedDeviationId,
            unlistedDescription,
            first,
            latest,
            references,
            [],
            "The bounded visible evidence may warrant a Recovery Check-in.",
            GoalKeeper.Application.Recovery.AccountabilityMessageFactory.Create(
                request.SessionId,
                request.Contract.GoalTitle,
                unlistedDescription ?? "the visible distraction"));
        var known = request.RecentObservations
            .ToDictionary(value => value.Id, value => value.CapturedAtMonotonic);
        var evidenceReferences = references
            .Where(known.ContainsKey)
            .Select(value => new ReasoningEvidenceReference(value, known[value]))
            .ToArray();
        var firstReference = evidenceReferences.FirstOrDefault() ??
            new ReasoningEvidenceReference(first, TimeSpan.Zero);
        var latestReference = evidenceReferences.LastOrDefault() ??
            new ReasoningEvidenceReference(latest, TimeSpan.Zero);
        var episode = new ReasoningEpisodeSummary(
            "scripted-episode",
            ReasoningEpisodeStatus.Active,
            listedDeviationId,
            unlistedDescription,
            firstReference,
            latestReference,
            evidenceReferences,
            [],
            "Scripted evidence episode.");
        return new(
            new(
                request.SessionId,
                request.SessionVersion,
                request.CurrentState,
                request.NewObservation.Id,
                ReasoningDecision.BeginRecoveryCheckIn,
                intervention,
                observationIds is null ? [episode] : []),
            Metadata());
    }

    private static ReasoningMetadata Metadata() =>
        new(
            "deterministic-fake",
            "reasoning-script-v2",
            "reasoning-v2",
            ReasoningSchemaVersions.V2,
            TimeSpan.Zero,
            $"reasoning-{Guid.NewGuid():N}");
}
