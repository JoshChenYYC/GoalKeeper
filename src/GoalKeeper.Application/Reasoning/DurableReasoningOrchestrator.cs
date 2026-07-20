using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using GoalKeeper.Application.Perception;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Reasoning;

public sealed class DurableReasoningOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IGoalKeeperRepository _repository;
    private readonly IReasoningPort _reasoning;
    private readonly IClock _clock;
    private readonly EvidenceEpisodePolicy _episodePolicy;
    private readonly TimeSpan _freshnessLimit;

    public DurableReasoningOrchestrator(
        IGoalKeeperRepository repository,
        IReasoningPort reasoning,
        IClock clock,
        TimeSpan freshnessLimit,
        EvidenceEpisodePolicy? episodePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(reasoning);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessLimit, TimeSpan.Zero);

        _repository = repository;
        _reasoning = reasoning;
        _clock = clock;
        _freshnessLimit = freshnessLimit;
        _episodePolicy = episodePolicy ?? new EvidenceEpisodePolicy();
    }

    public async Task<ReasoningOrchestrationResult> EvaluateAsync(
        ReasoningEvaluationInput input,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateCoreAsync(input, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReasoningOrchestrationResult> EvaluateAndAdmitAsync(
        ReasoningEvaluationInput input,
        ReasoningAdmissionHandler admissionHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionHandler);
        return await EvaluateCoreAsync(input, admissionHandler, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReasoningOrchestrationResult> EvaluateCoreAsync(
        ReasoningEvaluationInput input,
        ReasoningAdmissionHandler? admissionHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        ReasoningRequest request;
        try
        {
            request = await BuildRequestAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (ReasoningInputException exception)
        {
            return await RecordRejectedAsync(
                input,
                ReasoningDecision.ContinueObserving,
                exception.Reason,
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        ReasoningResult result;
        try
        {
            result = await _reasoning.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await RecordRejectedAsync(
                input,
                ReasoningDecision.ContinueObserving,
                "technical_failure",
                request,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        if (result is ReasoningInvalid)
        {
            return await RecordRejectedAsync(
                input,
                ReasoningDecision.ContinueObserving,
                "invalid_response",
                request,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        if (result is ReasoningFailure failure)
        {
            return await RecordRejectedAsync(
                input,
                ReasoningDecision.ContinueObserving,
                $"technical_{failure.Category.ToString().ToLowerInvariant()}",
                request,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        if (result is not ReasoningSuccess success)
        {
            return await RecordRejectedAsync(
                input,
                ReasoningDecision.ContinueObserving,
                "invalid_result_type",
                request,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        var validation = await ValidateProposalAsync(input, request, success, cancellationToken)
            .ConfigureAwait(false);
        if (validation.RejectionReason is not null)
        {
            return await RecordRejectedAsync(
                input,
                success.Proposal.Decision,
                validation.RejectionReason,
                request,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await CommitAcceptedAsync(
                input,
                request,
                success,
                validation.EpisodePlan,
                validation.EpisodeMemory,
                admissionHandler,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReasoningAdmissionException exception)
        {
            return await RecordRejectedAsync(
                input,
                success.Proposal.Decision,
                exception.Reason,
                request,
                success,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceConflictException)
        {
            return await RecordRejectedAsync(
                input,
                success.Proposal.Decision,
                "superseded_session",
                request,
                success,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainRuleViolationException)
        {
            return await RecordRejectedAsync(
                input,
                success.Proposal.Decision,
                "admission_rejected",
                request,
                success,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReasoningRequest> BuildRequestAsync(
        ReasoningEvaluationInput input,
        CancellationToken cancellationToken)
    {
        var current = await _repository.GetSessionAsync(input.Runtime.Id, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new ReasoningInputException("session_not_found");
        }

        if (current.Version != input.Runtime.Version ||
            current.State != input.Runtime.State ||
            current.ContractId != input.Contract.Id)
        {
            throw new ReasoningInputException("superseded_session");
        }

        var observations = await _repository.GetRecentObservationsAsync(
            input.Runtime.Id,
            ReasoningLimits.RecentObservations,
            cancellationToken).ConfigureAwait(false);
        var persistedNew = observations.SingleOrDefault(value => value.Id == input.NewObservation.Id);
        if (persistedNew is null || persistedNew != input.NewObservation)
        {
            throw new ReasoningInputException("trigger_observation_not_persisted");
        }

        var reasoningObservations = observations.Select(ToReasoningObservation).ToArray();
        if (reasoningObservations.Length == 0 ||
            reasoningObservations[^1].Id != input.NewObservation.Id)
        {
            throw new ReasoningInputException("trigger_observation_not_latest");
        }

        var evaluations = await _repository.GetRecentReasoningEvaluationsAsync(
            input.Runtime.Id,
            ReasoningLimits.PriorDecisions,
            cancellationToken).ConfigureAwait(false);
        var memory = ReadLatestMemory(evaluations);
        var active = memory.Where(value => value.Status == ReasoningEpisodeStatus.Active)
            .Take(ReasoningLimits.ActiveEpisodes)
            .ToArray();
        var historical = memory.Where(value => value.Status == ReasoningEpisodeStatus.Historical)
            .TakeLast(ReasoningLimits.HistoricalEpisodes)
            .ToArray();
        var request = new ReasoningRequest(
            input.Runtime.Id,
            input.Runtime.Version,
            input.Runtime.State,
            ToContract(input.Contract),
            input.Runtime.DeviationOverrides.TakeLast(ReasoningLimits.DeviationOverrides)
                .Select(ToOverride),
            active,
            historical,
            input.RecoverySummaries.TakeLast(ReasoningLimits.RecoverySummaries)
                .Select(SanitizeRecovery),
            evaluations.TakeLast(ReasoningLimits.PriorDecisions).Select(value =>
                new ReasoningDecisionSummary(
                    value.Id,
                    value.Decision,
                    value.Accepted,
                    value.RejectionReason,
                    value.EvaluatedAtUtc)),
            reasoningObservations.Single(value => value.Id == input.NewObservation.Id),
            reasoningObservations);

        if (JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions).Length >
            ReasoningLimits.MaximumSerializedRequestBytes)
        {
            throw new ReasoningInputException("bounded_request_too_large");
        }

        return request;
    }

    private async Task<ProposalValidation> ValidateProposalAsync(
        ReasoningEvaluationInput input,
        ReasoningRequest request,
        ReasoningSuccess success,
        CancellationToken cancellationToken)
    {
        var proposal = success.Proposal;
        if (!ValidMetadata(success.Metadata))
        {
            return ProposalValidation.Rejected("invalid_metadata");
        }

        if (!Enum.IsDefined(proposal.Decision) ||
            proposal.SessionId != request.SessionId ||
            proposal.SessionVersion != request.SessionVersion ||
            proposal.CurrentState != request.CurrentState ||
            proposal.TriggerObservationId != request.NewObservation.Id)
        {
            return ProposalValidation.Rejected("stale_or_mismatched_result");
        }

        var current = await _repository.GetSessionAsync(request.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null ||
            current.Version != request.SessionVersion ||
            current.State != request.CurrentState)
        {
            return ProposalValidation.Rejected("superseded_session");
        }

        if (request.CurrentState is not FocusSessionState.Focusing and
            not FocusSessionState.RecoveryWindow)
        {
            return ProposalValidation.Rejected("invalid_current_state");
        }

        var observationAge = _clock.UtcNow - request.NewObservation.CapturedAtUtc;
        if (observationAge < TimeSpan.Zero || observationAge > _freshnessLimit)
        {
            return ProposalValidation.Rejected("stale_observation");
        }

        var knownReferences = KnownReferences(request);
        var memoryResult = ValidateEpisodeMemory(input, proposal.EpisodeUpdates, knownReferences);
        if (memoryResult.RejectionReason is not null)
        {
            return ProposalValidation.Rejected(memoryResult.RejectionReason);
        }

        if (proposal.Decision == ReasoningDecision.ContinueObserving)
        {
            return proposal.Intervention is null
                ? ProposalValidation.Accepted(null, memoryResult.Memory)
                : ProposalValidation.Rejected("continue_contains_intervention");
        }

        if (proposal.Intervention is null)
        {
            return ProposalValidation.Rejected("intervention_payload_missing");
        }

        var intervention = proposal.Intervention;
        var deviationResult = ValidateDeviation(input, intervention);
        if (deviationResult.RejectionReason is not null)
        {
            return ProposalValidation.Rejected(deviationResult.RejectionReason);
        }

        var references = ResolveReferences(intervention, knownReferences);
        if (references.RejectionReason is not null)
        {
            return ProposalValidation.Rejected(references.RejectionReason);
        }

        if (!IsOrdered(intervention.KeyObservationIds, knownReferences) ||
            !IsOrdered(intervention.ContradictoryObservationIds, knownReferences))
        {
            return ProposalValidation.Rejected("reordered_references");
        }

        var triggerTime = request.NewObservation.CapturedAtMonotonic;
        if (new[] { references.First!, references.Latest! }
            .Concat(references.Keys!)
            .Concat(references.Contradictions!)
            .Any(value => value.CapturedAt > triggerTime))
        {
            return ProposalValidation.Rejected("reference_newer_than_trigger");
        }

        if (string.IsNullOrWhiteSpace(intervention.Rationale) ||
            intervention.Rationale.Length > ReasoningLimits.MaximumRationaleLength)
        {
            return ProposalValidation.Rejected("invalid_rationale");
        }

        if (!AccountabilityMessagePolicy.IsAcceptable(
                intervention.AccountabilityMessage))
        {
            return ProposalValidation.Rejected("invalid_accountability_message");
        }

        try
        {
            var plan = _episodePolicy.Create(
                request.SessionId,
                deviationResult.Deviation!,
                references.First!,
                references.Latest!,
                references.Keys!,
                references.Contradictions!);
            return ProposalValidation.Accepted(plan, memoryResult.Memory);
        }
        catch (DomainRuleViolationException)
        {
            return ProposalValidation.Rejected("invalid_episode_references");
        }
    }

    private async Task<ReasoningOrchestrationResult> CommitAcceptedAsync(
        ReasoningEvaluationInput input,
        ReasoningRequest request,
        ReasoningSuccess success,
        EvidenceEpisodePlan? plan,
        IReadOnlyList<ReasoningEpisodeSummary> memory,
        ReasoningAdmissionHandler? admissionHandler,
        CancellationToken cancellationToken)
    {
        var evaluationId = Guid.NewGuid();
        var evaluatedAt = _clock.UtcNow;
        ReasoningAdmissionPlan? admission = null;
        ReasoningEvaluation? domainEvaluation = null;
        if (plan is not null && admissionHandler is not null)
        {
            domainEvaluation = new(
                evaluationId,
                request.SessionId,
                request.SessionVersion,
                ReasoningDecision.BeginRecoveryCheckIn,
                plan.Episode,
                success.Proposal.Intervention!.Rationale,
                evaluatedAt)
            {
                AccountabilityMessage =
                    success.Proposal.Intervention.AccountabilityMessage
            };
            admission = await admissionHandler(
                    new(domainEvaluation, request),
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateAdmission(request, plan, domainEvaluation, admission);
        }

        var document = SerializeDocument(
            "accepted",
            null,
            request.NewObservation.Id,
            success.Metadata,
            success.Proposal,
            memory,
            []);
        var evaluation = new ReasoningEvaluationWrite(
            evaluationId,
            request.SessionId,
            request.SessionVersion,
            success.Proposal.Decision,
            evaluatedAt,
            ReasoningSchemaVersions.V2,
            document);
        EvidenceEpisodeWrite? episodeWrite = null;
        InterventionWrite? interventionWrite = null;
        if (plan is not null)
        {
            episodeWrite = new(
                plan.Episode.Id,
                request.SessionId,
                plan.Episode.Deviation.ListedDeviationId,
                plan.Episode.Deviation.UnlistedDescription,
                evaluatedAt,
                JsonSerializer.Serialize(success.Proposal.Intervention, JsonOptions),
                plan.Episode.Observations.Select((value, index) =>
                    new EvidenceObservationWrite(Guid.Parse(value.ObservationId), index)).ToArray());
            if (admission is not null)
            {
                var active = admission.Runtime.ActiveIntervention!;
                interventionWrite = new(
                    active.Id,
                    request.SessionId,
                    evaluationId,
                    plan.Episode.Id,
                    active.AdmittedAtUtc,
                    active.DisputedDuration,
                    "Active");
            }
        }

        var commit = await _repository.CommitReasoningEvaluationAsync(
            new(
                request.SessionVersion,
                admission?.Runtime ??
                input.Runtime with { Version = input.Runtime.Version + 1 },
                evaluation,
                episodeWrite,
                interventionWrite,
                admission?.AuditEvents ?? []),
            cancellationToken).ConfigureAwait(false);
        return new(
            evaluationId,
            commit.Applied,
            success.Proposal.Decision,
            commit.RejectionReason,
            commit.Applied ? plan?.Episode : null,
            request);
    }

    private static void ValidateAdmission(
        ReasoningRequest request,
        EvidenceEpisodePlan plan,
        ReasoningEvaluation evaluation,
        ReasoningAdmissionPlan? admission)
    {
        var runtime = admission?.Runtime;
        var active = runtime?.ActiveIntervention;
        var admittedEvaluation = active?.Evaluation;
        if (runtime is null ||
            runtime.Id != request.SessionId ||
            runtime.ContractId != request.Contract.Id ||
            runtime.Version != request.SessionVersion + 1 ||
            runtime.State != FocusSessionState.RecoveryCheckIn ||
            active is null ||
            admittedEvaluation is null ||
            admittedEvaluation.Id != evaluation.Id ||
            admittedEvaluation.SessionId != request.SessionId ||
            admittedEvaluation.SessionVersion != request.SessionVersion ||
            admittedEvaluation.Decision != ReasoningDecision.BeginRecoveryCheckIn ||
            admittedEvaluation.EvidenceEpisode?.Id != plan.Episode.Id)
        {
            throw new ReasoningAdmissionException("invalid_admission_plan");
        }
    }

    private async Task<ReasoningOrchestrationResult> RecordRejectedAsync(
        ReasoningEvaluationInput input,
        ReasoningDecision decision,
        string reason,
        ReasoningRequest? request,
        ReasoningResult? result,
        CancellationToken cancellationToken)
    {
        var evaluationId = Guid.NewGuid();
        var metadata = result?.Metadata;
        var proposal = (result as ReasoningSuccess)?.Proposal;
        var memory = request is null
            ? Array.Empty<ReasoningEpisodeSummary>()
            : request.ActiveEpisodes.Concat(request.HistoricalEpisodes).ToArray();
        var document = SerializeDocument(
            "rejected",
            reason,
            input.NewObservation.Id,
            metadata,
            proposal,
            memory,
            result is ReasoningInvalid { ValidationReasons: { } reasons }
                ? reasons.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(16)
                    .Select(Bounded)
                    .ToArray()
                : [reason]);
        var evaluation = new ReasoningEvaluationWrite(
            evaluationId,
            input.Runtime.Id,
            input.Runtime.Version,
            Enum.IsDefined(decision) ? decision : ReasoningDecision.ContinueObserving,
            _clock.UtcNow,
            ReasoningSchemaVersions.V2,
            document);
        var recorded = await _repository.AppendRejectedReasoningEvaluationAsync(
            evaluation,
            reason,
            cancellationToken).ConfigureAwait(false);
        return new(
            evaluationId,
            false,
            evaluation.Decision,
            recorded.RejectionReason ?? reason,
            null,
            request,
            FailureCategory(reason, result));
    }

    private static ReasoningFailureCategory? FailureCategory(
        string reason,
        ReasoningResult? result) =>
        result switch
        {
            ReasoningFailure failure => failure.Category,
            ReasoningInvalid => ReasoningFailureCategory.InvalidResponse,
            null when reason == "technical_failure" => ReasoningFailureCategory.Unknown,
            _ => null
        };

    private EpisodeMemoryValidation ValidateEpisodeMemory(
        ReasoningEvaluationInput input,
        IReadOnlyList<ReasoningEpisodeSummary>? updates,
        Dictionary<Guid, ObservationReference> known)
    {
        if (updates is null ||
            updates.Count(value => value.Status == ReasoningEpisodeStatus.Active) >
            ReasoningLimits.ActiveEpisodes ||
            updates.Count(value => value.Status == ReasoningEpisodeStatus.Historical) >
            ReasoningLimits.HistoricalEpisodes ||
            updates.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != updates.Count)
        {
            return EpisodeMemoryValidation.Rejected("invalid_episode_memory");
        }

        foreach (var episode in updates)
        {
            if (!Enum.IsDefined(episode.Status) ||
                string.IsNullOrWhiteSpace(episode.Key) ||
                episode.Key.Length > ReasoningLimits.MaximumTextLength ||
                string.IsNullOrWhiteSpace(episode.Summary) ||
                episode.Summary.Length > ReasoningLimits.MaximumTextLength ||
                episode.KeyObservations.Count is 0 or > EvidenceEpisodePolicy.DefaultMaximumKeyObservations ||
                episode.ContradictoryObservations.Count >
                EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations)
            {
                return EpisodeMemoryValidation.Rejected("invalid_episode_memory");
            }

            var deviation = ValidateDeviation(
                input,
                new(
                    episode.ListedDeviationId,
                    episode.UnlistedDescription,
                    episode.FirstObservation.ObservationId,
                    episode.LatestObservation.ObservationId,
                    episode.KeyObservations.Select(value => value.ObservationId).ToArray(),
                    episode.ContradictoryObservations.Select(value => value.ObservationId).ToArray(),
                    episode.Summary,
                    "Episode memory validation does not produce a user-facing message."));
            if (deviation.RejectionReason is not null)
            {
                return EpisodeMemoryValidation.Rejected(deviation.RejectionReason);
            }

            var references = new[] { episode.FirstObservation, episode.LatestObservation }
                .Concat(episode.KeyObservations)
                .Concat(episode.ContradictoryObservations);
            if (references.Any(value =>
                    !known.TryGetValue(value.ObservationId, out var persisted) ||
                    persisted.CapturedAt != value.CapturedAtMonotonic))
            {
                return EpisodeMemoryValidation.Rejected("unlisted_observation_reference");
            }

            try
            {
                _ = _episodePolicy.Create(
                    input.Runtime.Id,
                    deviation.Deviation!,
                    known[episode.FirstObservation.ObservationId],
                    known[episode.LatestObservation.ObservationId],
                    episode.KeyObservations.Select(value => known[value.ObservationId]),
                    episode.ContradictoryObservations.Select(value => known[value.ObservationId]));
            }
            catch (DomainRuleViolationException)
            {
                return EpisodeMemoryValidation.Rejected("invalid_episode_memory");
            }
        }

        return EpisodeMemoryValidation.Accepted(updates.ToArray());
    }

    private static DeviationValidation ValidateDeviation(
        ReasoningEvaluationInput input,
        ReasoningInterventionProposal intervention)
    {
        if ((intervention.ListedDeviationId is null) ==
            string.IsNullOrWhiteSpace(intervention.UnlistedDescription))
        {
            return DeviationValidation.Rejected("invalid_deviation_reference");
        }

        if (intervention.ListedDeviationId is { } listed)
        {
            if (input.Contract.Deviations.All(value => value.Id != listed))
            {
                return DeviationValidation.Rejected("unknown_listed_deviation");
            }

            if (input.Runtime.DeviationOverrides.Any(value =>
                    value.Deviation.ListedDeviationId == listed))
            {
                return DeviationValidation.Rejected("deviation_override_applies");
            }

            return DeviationValidation.Accepted(DeviationReference.Listed(listed));
        }

        if (input.Contract.ReasoningMode == ReasoningMode.ProfileOnly)
        {
            return DeviationValidation.Rejected("profile_only_unlisted_deviation");
        }

        var description = input.Runtime.DeviationOverrides.Any(value =>
                string.Equals(
                    value.Deviation.UnlistedDescription,
                    intervention.UnlistedDescription,
                    StringComparison.OrdinalIgnoreCase))
            ? null
            : intervention.UnlistedDescription;
        return description is null
            ? DeviationValidation.Rejected("deviation_override_applies")
            : DeviationValidation.Accepted(DeviationReference.Unlisted(description));
    }

    private static ReferenceResolution ResolveReferences(
        ReasoningInterventionProposal intervention,
        Dictionary<Guid, ObservationReference> known)
    {
        if (intervention.KeyObservationIds is null ||
            intervention.ContradictoryObservationIds is null ||
            intervention.KeyObservationIds.Count is 0 or >
            EvidenceEpisodePolicy.DefaultMaximumKeyObservations ||
            intervention.ContradictoryObservationIds.Count >
            EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations ||
            intervention.KeyObservationIds.Distinct().Count() != intervention.KeyObservationIds.Count ||
            intervention.ContradictoryObservationIds.Distinct().Count() !=
            intervention.ContradictoryObservationIds.Count ||
            intervention.KeyObservationIds.Intersect(intervention.ContradictoryObservationIds).Any())
        {
            return ReferenceResolution.Rejected("invalid_reference_set");
        }

        var all = new[] { intervention.FirstObservationId, intervention.LatestObservationId }
            .Concat(intervention.KeyObservationIds)
            .Concat(intervention.ContradictoryObservationIds)
            .ToArray();
        if (all.Any(value => !known.ContainsKey(value)))
        {
            return ReferenceResolution.Rejected("unlisted_observation_reference");
        }

        return ReferenceResolution.Accepted(
            known[intervention.FirstObservationId],
            known[intervention.LatestObservationId],
            intervention.KeyObservationIds.Select(value => known[value]).ToArray(),
            intervention.ContradictoryObservationIds.Select(value => known[value]).ToArray());
    }

    private static Dictionary<Guid, ObservationReference> KnownReferences(
        ReasoningRequest request)
    {
        var values = request.RecentObservations.Select(value =>
                ObservationReference.Create(
                    value.Id.ToString("D"),
                    request.SessionId,
                    value.CapturedAtMonotonic))
            .Concat(request.ActiveEpisodes.Concat(request.HistoricalEpisodes)
                .SelectMany(value => new[] { value.FirstObservation, value.LatestObservation }
                    .Concat(value.KeyObservations)
                    .Concat(value.ContradictoryObservations))
                .Select(value => ObservationReference.Create(
                    value.ObservationId.ToString("D"),
                    request.SessionId,
                    value.CapturedAtMonotonic)));
        return values.GroupBy(value => Guid.Parse(value.ObservationId))
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static bool IsOrdered(
        IReadOnlyList<Guid> identifiers,
        Dictionary<Guid, ObservationReference> known) =>
        identifiers.Zip(identifiers.Skip(1)).All(pair =>
            known[pair.First].CapturedAt <= known[pair.Second].CapturedAt);

    private static ReasoningObservation ToReasoningObservation(ObservationView value)
    {
        var validation = ObservationValidator.Validate(
            Encoding.UTF8.GetBytes(value.DocumentJson),
            PerceptionRequestOptions.Default);
        if (validation is ValidatedObservation valid)
        {
            return new(
                value.Id,
                value.SessionVersion,
                value.CapturedAtUtc,
                value.CapturedAtMonotonic,
                valid.Value);
        }

        throw new ReasoningInputException("invalid_persisted_observation");
    }

    private static ReasoningContractSummary ToContract(SessionContractView value) =>
        new(
            value.Id,
            Bounded(value.GoalTitle),
            BoundedOptional(value.GoalDescription),
            value.TargetFocusDuration,
            value.Deviations.Take(ReasoningLimits.ContractDeviations).Select(deviation =>
                new ReasoningContractDeviation(
                    deviation.Id,
                    Bounded(deviation.Description),
                    deviation.Observability)).ToArray(),
            value.ReasoningMode,
            value.Sensitivity);

    private static ReasoningDeviationOverride ToOverride(DeviationOverrideSnapshot value) =>
        new(
            value.Deviation.ListedDeviationId,
            BoundedOptional(value.Deviation.UnlistedDescription),
            Bounded(value.Reason));

    private static ReasoningRecoverySummary SanitizeRecovery(ReasoningRecoverySummary value) =>
        value with
        {
            Outcome = Bounded(value.Outcome),
            Summary = Bounded(value.Summary)
        };

    private static IReadOnlyList<ReasoningEpisodeSummary> ReadLatestMemory(
        IReadOnlyList<ReasoningEvaluationView> evaluations)
    {
        foreach (var evaluation in evaluations.Reverse())
        {
            try
            {
                var document = JsonSerializer.Deserialize<EvaluationDocument>(
                    evaluation.DocumentJson,
                    JsonOptions);
                if (document?.EpisodeMemory is not null)
                {
                    return document.EpisodeMemory;
                }
            }
            catch (JsonException)
            {
                throw new ReasoningInputException("invalid_durable_reasoning_memory");
            }
        }

        return [];
    }

    private static string SerializeDocument(
        string outcome,
        string? rejectionReason,
        Guid triggerObservationId,
        ReasoningMetadata? metadata,
        ReasoningProposal? proposal,
        IReadOnlyList<ReasoningEpisodeSummary> memory,
        IReadOnlyList<string> validationReasons) =>
        JsonSerializer.Serialize(
            new EvaluationDocument(
                outcome,
                rejectionReason,
                triggerObservationId,
                metadata,
                proposal,
                memory,
                validationReasons),
            JsonOptions);

    private static bool ValidMetadata(ReasoningMetadata metadata) =>
        Safe(metadata.Provider, 80) &&
        Safe(metadata.Model, 120) &&
        Safe(metadata.PromptVersion, 80) &&
        Safe(metadata.RequestId, 160) &&
        metadata.SchemaVersion == ReasoningSchemaVersions.V2 &&
        metadata.Latency >= TimeSpan.Zero;

    private static bool Safe(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static string Bounded(string value) =>
        value.Length <= ReasoningLimits.MaximumTextLength
            ? value
            : value[..ReasoningLimits.MaximumTextLength];

    private static string? BoundedOptional(string? value) =>
        value is null ? null : Bounded(value);

    private static void ValidateInput(ReasoningEvaluationInput input)
    {
        if (input.Runtime.Id == Guid.Empty ||
            input.Runtime.Version <= 0 ||
            input.Contract.Id != input.Runtime.ContractId ||
            input.NewObservation.Id == Guid.Empty ||
            input.NewObservation.SessionId != input.Runtime.Id ||
            input.NewObservation.SessionVersion != input.Runtime.Version)
        {
            throw new ArgumentException("The Reasoning evaluation input is inconsistent.", nameof(input));
        }
    }

    private sealed record EvaluationDocument(
        string Outcome,
        string? RejectionReason,
        Guid TriggerObservationId,
        ReasoningMetadata? Metadata,
        ReasoningProposal? Proposal,
        IReadOnlyList<ReasoningEpisodeSummary> EpisodeMemory,
        IReadOnlyList<string> ValidationReasons);

    private sealed record ProposalValidation(
        string? RejectionReason,
        EvidenceEpisodePlan? EpisodePlan,
        IReadOnlyList<ReasoningEpisodeSummary> EpisodeMemory)
    {
        public static ProposalValidation Rejected(string reason) => new(reason, null, []);

        public static ProposalValidation Accepted(
            EvidenceEpisodePlan? plan,
            IReadOnlyList<ReasoningEpisodeSummary> memory) =>
            new(null, plan, memory);
    }

    private sealed record EpisodeMemoryValidation(
        string? RejectionReason,
        IReadOnlyList<ReasoningEpisodeSummary> Memory)
    {
        public static EpisodeMemoryValidation Rejected(string reason) => new(reason, []);

        public static EpisodeMemoryValidation Accepted(
            IReadOnlyList<ReasoningEpisodeSummary> memory) =>
            new(null, memory);
    }

    private sealed record DeviationValidation(
        string? RejectionReason,
        DeviationReference? Deviation)
    {
        public static DeviationValidation Rejected(string reason) => new(reason, null);

        public static DeviationValidation Accepted(DeviationReference deviation) =>
            new(null, deviation);
    }

    private sealed record ReferenceResolution(
        string? RejectionReason,
        ObservationReference? First,
        ObservationReference? Latest,
        IReadOnlyList<ObservationReference>? Keys,
        IReadOnlyList<ObservationReference>? Contradictions)
    {
        public static ReferenceResolution Rejected(string reason) =>
            new(reason, null, null, null, null);

        public static ReferenceResolution Accepted(
            ObservationReference first,
            ObservationReference latest,
            IReadOnlyList<ObservationReference> keys,
            IReadOnlyList<ObservationReference> contradictions) =>
            new(null, first, latest, keys, contradictions);
    }

    private sealed class ReasoningInputException : InvalidOperationException
    {
        public ReasoningInputException(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    private sealed class ReasoningAdmissionException : InvalidOperationException
    {
        public ReasoningAdmissionException(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }
}
