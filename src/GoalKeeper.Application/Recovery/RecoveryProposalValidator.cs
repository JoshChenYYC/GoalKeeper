namespace GoalKeeper.Application.Recovery;

public enum RecoveryValidationErrorCode
{
    InvalidIdentity,
    StaleSession,
    StaleVersion,
    StaleIntervention,
    InvalidTurnOrder,
    InvalidOutcome,
    OutcomeNotAllowed,
    CoachingCapReached,
    MissingField,
    UnexpectedField,
    InvalidValue,
    InvalidTiming,
    InvalidMetadata
}

public sealed record RecoveryValidationIssue(
    string Path,
    RecoveryValidationErrorCode Code,
    string Message);

public sealed class RecoveryValidationFailure
{
    public RecoveryValidationFailure(IEnumerable<RecoveryValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.ToArray());
        if (Issues.Count == 0)
        {
            throw new ArgumentException("A validation failure requires at least one issue.", nameof(issues));
        }
    }

    public IReadOnlyList<RecoveryValidationIssue> Issues { get; }
}

public abstract record RecoveryProposalValidation;

public sealed record ValidRecoveryProposal(RecoveryProposal Proposal) : RecoveryProposalValidation;

public sealed record InvalidRecoveryProposal(RecoveryValidationFailure Failure) : RecoveryProposalValidation;

public static class RecoveryProposalValidator
{
    public static RecoveryProposalValidation Validate(
        RecoveryRequest request,
        RecoveryProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);

        var issues = new List<RecoveryValidationIssue>();
        ValidateIdentity(request, proposal, issues);
        ValidateOutcome(request, proposal, issues);
        ValidateText(request, proposal, issues);
        ValidateTiming(request, proposal, issues);
        ValidateMetadata(request, proposal, issues);

        return issues.Count == 0
            ? new ValidRecoveryProposal(proposal)
            : new InvalidRecoveryProposal(new RecoveryValidationFailure(issues));
    }

    private static void ValidateIdentity(
        RecoveryRequest request,
        RecoveryProposal proposal,
        List<RecoveryValidationIssue> issues)
    {
        if (proposal.SessionId == Guid.Empty)
        {
            Add(issues, "$.session_id", RecoveryValidationErrorCode.InvalidIdentity,
                "The session identifier is required.");
        }
        else if (proposal.SessionId != request.SessionId)
        {
            Add(issues, "$.session_id", RecoveryValidationErrorCode.StaleSession,
                "The proposal belongs to a different Focus Session.");
        }

        if (proposal.SessionVersion != request.SessionVersion)
        {
            Add(issues, "$.session_version", RecoveryValidationErrorCode.StaleVersion,
                "The proposal does not target the current Focus Session version.");
        }

        if (proposal.InterventionId == Guid.Empty)
        {
            Add(issues, "$.intervention_id", RecoveryValidationErrorCode.InvalidIdentity,
                "The Intervention identifier is required.");
        }
        else if (proposal.InterventionId != request.Intervention.InterventionId)
        {
            Add(issues, "$.intervention_id", RecoveryValidationErrorCode.StaleIntervention,
                "The proposal belongs to a different Intervention.");
        }

        if (proposal.TurnNumber != request.NextTurnNumber)
        {
            Add(issues, "$.turn_number", RecoveryValidationErrorCode.InvalidTurnOrder,
                "The proposal must be the next persisted Recovery Check-in turn.");
        }
    }

    private static void ValidateOutcome(
        RecoveryRequest request,
        RecoveryProposal proposal,
        List<RecoveryValidationIssue> issues)
    {
        if (!Enum.IsDefined(proposal.Outcome))
        {
            Add(issues, "$.outcome", RecoveryValidationErrorCode.InvalidOutcome,
                "The proposal outcome is not defined.");
            return;
        }

        if (!request.AllowedOutcomes.Contains(proposal.Outcome))
        {
            Add(issues, "$.outcome", RecoveryValidationErrorCode.OutcomeNotAllowed,
                "The proposal outcome is not allowed for this Recovery Check-in.");
        }

        if (proposal.Outcome == RecoveryOutcome.AdditionalCoaching &&
            request.PersistedCoachingTurnCount >= request.Options.MaximumCoachingTurns)
        {
            Add(issues, "$.outcome", RecoveryValidationErrorCode.CoachingCapReached,
                "The persisted Recovery Check-in turns have reached the coaching cap.");
        }
    }

    private static void ValidateText(
        RecoveryRequest request,
        RecoveryProposal proposal,
        List<RecoveryValidationIssue> issues)
    {
        ValidateOptionalSafeText(
            proposal.Transcript,
            "$.transcript",
            RecoveryLimits.MaximumTranscriptLength,
            issues);
        ValidateOptionalSafeText(
            proposal.Clarification,
            "$.clarification",
            RecoveryLimits.MaximumSummaryLength,
            issues);
        ValidateOptionalSafeText(
            proposal.AssistantMessage,
            "$.assistant_message",
            RecoveryLimits.MaximumAccountabilityMessageLength,
            issues);

        if (proposal.AssistantMessage is { } assistantMessage &&
            !GoalKeeper.Application.Reasoning.AccountabilityMessagePolicy.IsAcceptable(
                assistantMessage))
        {
            issues.Add(new(
                "$.assistant_message",
                RecoveryValidationErrorCode.InvalidValue,
                "The assistant response violates the accountability-message safety policy."));
        }

        if (!string.Equals(proposal.Transcript, request.CurrentTranscript, StringComparison.Ordinal))
        {
            Add(issues, "$.transcript", RecoveryValidationErrorCode.InvalidValue,
                "The proposal transcript must match the bounded current Check-in transcript.");
        }

        if (proposal.Outcome != RecoveryOutcome.NoResponse && proposal.Transcript is null)
        {
            Add(issues, "$.transcript", RecoveryValidationErrorCode.MissingField,
                "A spoken Recovery outcome requires a transcript.");
        }

        if (proposal.Outcome == RecoveryOutcome.NoResponse && proposal.Transcript is not null)
        {
            Add(issues, "$.transcript", RecoveryValidationErrorCode.UnexpectedField,
                "No response cannot include a transcript.");
        }

        var requiresClarification = proposal.Outcome == RecoveryOutcome.BehaviorClarification;
        RequiredWhen(
            requiresClarification,
            proposal.Clarification,
            "$.clarification",
            "Behavior Clarification requires the goal-consistent explanation.",
            issues);

        var requiresAssistantMessage =
            proposal.Outcome is RecoveryOutcome.AdditionalCoaching or RecoveryOutcome.UnclearResponse;
        RequiredWhen(
            requiresAssistantMessage,
            proposal.AssistantMessage,
            "$.assistant_message",
            "Coaching and unclear outcomes require a bounded assistant response.",
            issues);

        if (proposal.RemainderOverrideConfirmed && !requiresClarification)
        {
            Add(issues, "$.remainder_override_confirmed", RecoveryValidationErrorCode.UnexpectedField,
                "Only Behavior Clarification can explicitly confirm a remainder override.");
        }
    }

    private static void ValidateTiming(
        RecoveryRequest request,
        RecoveryProposal proposal,
        List<RecoveryValidationIssue> issues)
    {
        if (proposal.Timing is null)
        {
            Add(issues, "$.timing", RecoveryValidationErrorCode.MissingField,
                "Turn timing is required.");
            return;
        }

        if (proposal.Timing.StartedAtUtc < request.RequestedAtUtc ||
            proposal.Timing.CompletedAtUtc < proposal.Timing.StartedAtUtc)
        {
            Add(issues, "$.timing", RecoveryValidationErrorCode.InvalidTiming,
                "Turn timing must begin after the request and complete in order.");
        }
    }

    private static void ValidateMetadata(
        RecoveryRequest request,
        RecoveryProposal proposal,
        List<RecoveryValidationIssue> issues)
    {
        if (proposal.Metadata is null)
        {
            Add(issues, "$.metadata", RecoveryValidationErrorCode.MissingField,
                "Recovery metadata is required.");
            return;
        }

        ValidateRequiredSafeMetadata(proposal.Metadata.Provider, "$.metadata.provider", issues);
        ValidateRequiredSafeMetadata(proposal.Metadata.Model, "$.metadata.model", issues);
        ValidateRequiredSafeMetadata(proposal.Metadata.PromptVersion, "$.metadata.prompt_version", issues);
        ValidateRequiredSafeMetadata(proposal.Metadata.RequestId, "$.metadata.request_id", issues);

        if (proposal.Metadata.SchemaVersion != request.Options.SchemaVersion)
        {
            Add(issues, "$.metadata.schema_version", RecoveryValidationErrorCode.InvalidMetadata,
                "The metadata schema does not match the requested Recovery schema.");
        }

        if (proposal.Metadata.Latency < TimeSpan.Zero)
        {
            Add(issues, "$.metadata.latency", RecoveryValidationErrorCode.InvalidMetadata,
                "Recovery latency cannot be negative.");
        }
    }

    private static void RequiredWhen(
        bool required,
        string? value,
        string path,
        string message,
        List<RecoveryValidationIssue> issues)
    {
        if (required && value is null)
        {
            Add(issues, path, RecoveryValidationErrorCode.MissingField, message);
        }
        else if (!required && value is not null)
        {
            Add(issues, path, RecoveryValidationErrorCode.UnexpectedField,
                "The field is not valid for this Recovery outcome.");
        }
    }

    private static void ValidateRequiredSafeMetadata(
        string? value,
        string path,
        List<RecoveryValidationIssue> issues)
    {
        if (!IsSafeText(value, RecoveryLimits.MaximumMetadataLength))
        {
            Add(issues, path, RecoveryValidationErrorCode.InvalidMetadata,
                "Metadata must be non-empty, bounded, and contain no control characters.");
        }
    }

    private static void ValidateOptionalSafeText(
        string? value,
        string path,
        int maximumLength,
        List<RecoveryValidationIssue> issues)
    {
        if (value is not null && !IsSafeText(value, maximumLength))
        {
            Add(issues, path, RecoveryValidationErrorCode.InvalidValue,
                "Text must be non-empty, bounded, and contain no control characters.");
        }
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static void Add(
        List<RecoveryValidationIssue> issues,
        string path,
        RecoveryValidationErrorCode code,
        string message) =>
        issues.Add(new(path, code, message));
}

public static class RecoveryTurnFactory
{
    public static RecoveryTurn Create(Guid turnId, ValidRecoveryProposal validated)
    {
        ArgumentNullException.ThrowIfNull(validated);
        if (turnId == Guid.Empty)
        {
            throw new ArgumentException("The Recovery turn identifier cannot be empty.", nameof(turnId));
        }

        var proposal = validated.Proposal;
        return new(
            turnId,
            proposal.SessionId,
            proposal.SessionVersion,
            proposal.InterventionId,
            proposal.TurnNumber,
            proposal.Outcome,
            proposal.Transcript,
            proposal.Clarification,
            proposal.AssistantMessage,
            proposal.RemainderOverrideConfirmed,
            proposal.Timing!,
            proposal.Metadata!);
    }
}
