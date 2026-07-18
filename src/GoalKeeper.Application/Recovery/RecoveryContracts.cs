using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Recovery;

public static class RecoverySchemaVersions
{
    public const int V1 = 1;
}

public static class RecoveryLimits
{
    public const int MaximumCurrentTurns = 32;
    public const int MaximumOverrides = 32;
    public const int MaximumCoachingTurns = 10;
    public const int MaximumTitleLength = 240;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumSummaryLength = 4_000;
    public const int MaximumTranscriptLength = 4_000;
    public const int MaximumResponseLength = 2_000;
    public const int MaximumMetadataLength = 160;
}

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryOutcome>))]
public enum RecoveryOutcome
{
    [JsonStringEnumMemberName("recommit")]
    Recommit,
    [JsonStringEnumMemberName("behavior_clarification")]
    BehaviorClarification,
    [JsonStringEnumMemberName("end_early")]
    EndEarly,
    [JsonStringEnumMemberName("continue_session")]
    ContinueSession,
    [JsonStringEnumMemberName("additional_coaching")]
    AdditionalCoaching,
    [JsonStringEnumMemberName("unclear_response")]
    UnclearResponse,
    [JsonStringEnumMemberName("no_response")]
    NoResponse
}

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryFailureCategory>))]
public enum RecoveryFailureCategory
{
    InvalidResponse,
    Timeout,
    RateLimited,
    Network,
    Authentication,
    ProviderUnavailable,
    Cancelled,
    Unknown
}

public sealed record RecoveryContractContext
{
    public RecoveryContractContext(
        Guid contractId,
        Guid goalId,
        string goalTitle,
        string? goalDescription,
        TimeSpan targetFocusDuration,
        ReasoningMode reasoningMode,
        Sensitivity sensitivity)
    {
        ContractId = RecoveryGuards.Identifier(contractId, nameof(contractId));
        GoalId = RecoveryGuards.Identifier(goalId, nameof(goalId));
        GoalTitle = RecoveryGuards.RequiredText(
            goalTitle,
            nameof(goalTitle),
            RecoveryLimits.MaximumTitleLength);
        GoalDescription = RecoveryGuards.OptionalText(
            goalDescription,
            nameof(goalDescription),
            RecoveryLimits.MaximumDescriptionLength);
        TargetFocusDuration = RecoveryGuards.Positive(targetFocusDuration, nameof(targetFocusDuration));
        ReasoningMode = RecoveryGuards.Defined(reasoningMode, nameof(reasoningMode));
        Sensitivity = RecoveryGuards.Defined(sensitivity, nameof(sensitivity));
    }

    public Guid ContractId { get; }

    public Guid GoalId { get; }

    public string GoalTitle { get; }

    public string? GoalDescription { get; }

    public TimeSpan TargetFocusDuration { get; }

    public ReasoningMode ReasoningMode { get; }

    public Sensitivity Sensitivity { get; }
}

public sealed record RecoveryInterventionContext
{
    public RecoveryInterventionContext(
        Guid interventionId,
        Guid? listedDeviationId,
        string deviationDescription,
        string evidenceSummary,
        string rationale,
        DateTimeOffset admittedAtUtc)
    {
        InterventionId = RecoveryGuards.Identifier(interventionId, nameof(interventionId));
        if (listedDeviationId == Guid.Empty)
        {
            throw new ArgumentException("A listed Deviation identifier cannot be empty.", nameof(listedDeviationId));
        }

        ListedDeviationId = listedDeviationId;
        DeviationDescription = RecoveryGuards.RequiredText(
            deviationDescription,
            nameof(deviationDescription),
            RecoveryLimits.MaximumDescriptionLength);
        EvidenceSummary = RecoveryGuards.RequiredText(
            evidenceSummary,
            nameof(evidenceSummary),
            RecoveryLimits.MaximumSummaryLength);
        Rationale = RecoveryGuards.RequiredText(
            rationale,
            nameof(rationale),
            RecoveryLimits.MaximumSummaryLength);
        AdmittedAtUtc = admittedAtUtc;
    }

    public Guid InterventionId { get; }

    public Guid? ListedDeviationId { get; }

    public bool IsUnlisted => ListedDeviationId is null;

    public string DeviationDescription { get; }

    public string EvidenceSummary { get; }

    public string Rationale { get; }

    public DateTimeOffset AdmittedAtUtc { get; }
}

public sealed record RecoveryDisputedInterval
{
    public RecoveryDisputedInterval(TimeSpan startedAt, TimeSpan endedAt)
    {
        StartedAt = RecoveryGuards.NonNegative(startedAt, nameof(startedAt));
        EndedAt = RecoveryGuards.NonNegative(endedAt, nameof(endedAt));
        if (endedAt < startedAt)
        {
            throw new ArgumentException("The disputed interval cannot end before it starts.", nameof(endedAt));
        }
    }

    public TimeSpan StartedAt { get; }

    public TimeSpan EndedAt { get; }

    public TimeSpan Duration => EndedAt - StartedAt;
}

public sealed record RecoveryOverrideContext
{
    public RecoveryOverrideContext(
        Guid id,
        Guid? listedDeviationId,
        string deviationDescription,
        string reason,
        DateTimeOffset appliedAtUtc)
    {
        Id = RecoveryGuards.Identifier(id, nameof(id));
        if (listedDeviationId == Guid.Empty)
        {
            throw new ArgumentException("A listed Deviation identifier cannot be empty.", nameof(listedDeviationId));
        }

        ListedDeviationId = listedDeviationId;
        DeviationDescription = RecoveryGuards.RequiredText(
            deviationDescription,
            nameof(deviationDescription),
            RecoveryLimits.MaximumDescriptionLength);
        Reason = RecoveryGuards.RequiredText(reason, nameof(reason), RecoveryLimits.MaximumSummaryLength);
        AppliedAtUtc = appliedAtUtc;
    }

    public Guid Id { get; }

    public Guid? ListedDeviationId { get; }

    public string DeviationDescription { get; }

    public string Reason { get; }

    public DateTimeOffset AppliedAtUtc { get; }
}

public sealed record RecoveryTurnTiming(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public sealed record RecoveryMetadata(
    string? Provider,
    string? Model,
    string? PromptVersion,
    int SchemaVersion,
    TimeSpan Latency,
    string? RequestId);

public sealed record RecoveryProposal(
    Guid SessionId,
    long SessionVersion,
    Guid InterventionId,
    int TurnNumber,
    RecoveryOutcome Outcome,
    string? Transcript,
    string? Clarification,
    string? AssistantMessage,
    bool RemainderOverrideConfirmed,
    RecoveryTurnTiming? Timing,
    RecoveryMetadata? Metadata);

public sealed record RecoveryTurn(
    Guid Id,
    Guid SessionId,
    long SessionVersion,
    Guid InterventionId,
    int TurnNumber,
    RecoveryOutcome Outcome,
    string? Transcript,
    string? Clarification,
    string? AssistantMessage,
    bool RemainderOverrideConfirmed,
    RecoveryTurnTiming Timing,
    RecoveryMetadata Metadata);

public sealed class RecoveryRequestOptions
{
    public static RecoveryRequestOptions Default { get; } = new();

    public RecoveryRequestOptions(
        int maximumCoachingTurns = 3,
        int schemaVersion = RecoverySchemaVersions.V1)
    {
        if (maximumCoachingTurns is < 1 or > RecoveryLimits.MaximumCoachingTurns)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCoachingTurns));
        }

        if (schemaVersion != RecoverySchemaVersions.V1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Only Recovery schema {RecoverySchemaVersions.V1} is supported.");
        }

        MaximumCoachingTurns = maximumCoachingTurns;
        SchemaVersion = schemaVersion;
    }

    public int MaximumCoachingTurns { get; }

    public int SchemaVersion { get; }
}

public sealed class RecoveryRequest
{
    public RecoveryRequest(
        Guid sessionId,
        long sessionVersion,
        RecoveryContractContext contract,
        RecoveryInterventionContext intervention,
        RecoveryDisputedInterval disputedInterval,
        IEnumerable<RecoveryOverrideContext> activeOverrides,
        IEnumerable<RecoveryOutcome> allowedOutcomes,
        IEnumerable<RecoveryTurn> currentCheckInTurns,
        string? currentTranscript,
        DateTimeOffset requestedAtUtc,
        RecoveryRequestOptions? options = null)
    {
        SessionId = RecoveryGuards.Identifier(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionVersion);
        SessionVersion = sessionVersion;
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Intervention = intervention ?? throw new ArgumentNullException(nameof(intervention));
        DisputedInterval = disputedInterval ?? throw new ArgumentNullException(nameof(disputedInterval));
        Options = options ?? RecoveryRequestOptions.Default;
        CurrentTranscript = RecoveryGuards.OptionalText(
            currentTranscript,
            nameof(currentTranscript),
            RecoveryLimits.MaximumTranscriptLength);
        RequestedAtUtc = requestedAtUtc;

        ActiveOverrides = CopyBounded(
            activeOverrides,
            RecoveryLimits.MaximumOverrides,
            nameof(activeOverrides));
        AllowedOutcomes = CopyOutcomes(allowedOutcomes);
        CurrentCheckInTurns = CopyBounded(
            currentCheckInTurns,
            RecoveryLimits.MaximumCurrentTurns,
            nameof(currentCheckInTurns));

        ValidateCurrentTurns();
    }

    public Guid SessionId { get; }

    public long SessionVersion { get; }

    public RecoveryContractContext Contract { get; }

    public RecoveryInterventionContext Intervention { get; }

    public RecoveryDisputedInterval DisputedInterval { get; }

    public IReadOnlyList<RecoveryOverrideContext> ActiveOverrides { get; }

    public IReadOnlyList<RecoveryOutcome> AllowedOutcomes { get; }

    public IReadOnlyList<RecoveryTurn> CurrentCheckInTurns { get; }

    public string? CurrentTranscript { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public RecoveryRequestOptions Options { get; }

    public int NextTurnNumber => CurrentCheckInTurns.Count + 1;

    public int PersistedCoachingTurnCount =>
        CurrentCheckInTurns.Count(turn => turn.Outcome == RecoveryOutcome.AdditionalCoaching);

    private void ValidateCurrentTurns()
    {
        foreach (var (turn, index) in CurrentCheckInTurns.Select((turn, index) => (turn, index)))
        {
            ArgumentNullException.ThrowIfNull(turn);
            if (turn.Id == Guid.Empty ||
                turn.SessionId != SessionId ||
                turn.InterventionId != Intervention.InterventionId ||
                turn.SessionVersion <= 0 ||
                turn.SessionVersion > SessionVersion ||
                turn.TurnNumber != index + 1)
            {
                throw new ArgumentException(
                    "Persisted Recovery turns must be same-session, same-Intervention, versioned, and ordered.",
                    nameof(CurrentCheckInTurns));
            }

            if (!Enum.IsDefined(turn.Outcome) ||
                turn.Timing.CompletedAtUtc < turn.Timing.StartedAtUtc ||
                turn.Timing.CompletedAtUtc > RequestedAtUtc)
            {
                throw new ArgumentException(
                    "Persisted Recovery turns contain an invalid outcome or timing.",
                    nameof(CurrentCheckInTurns));
            }
        }

        if (PersistedCoachingTurnCount > Options.MaximumCoachingTurns)
        {
            throw new ArgumentException(
                "Persisted Recovery turns exceed the configured coaching cap.",
                nameof(CurrentCheckInTurns));
        }
    }

    private static ReadOnlyCollection<T> CopyBounded<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var values = source.ToArray();
        if (values.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Collections cannot contain null values.", parameterName);
        }

        return Array.AsReadOnly(values);
    }

    private static ReadOnlyCollection<RecoveryOutcome> CopyOutcomes(
        IEnumerable<RecoveryOutcome> allowedOutcomes)
    {
        var values = CopyBounded(
            allowedOutcomes,
            Enum.GetValues<RecoveryOutcome>().Length,
            nameof(allowedOutcomes));
        if (values.Count == 0 ||
            values.Any(outcome => !Enum.IsDefined(outcome)) ||
            values.Distinct().Count() != values.Count)
        {
            throw new ArgumentException(
                "Allowed Recovery outcomes must be non-empty, defined, and unique.",
                nameof(allowedOutcomes));
        }

        return values;
    }
}

public abstract record RecoveryPortResult;

public sealed record RecoveryProposalResponse(RecoveryProposal Proposal) : RecoveryPortResult;

public sealed record RecoveryFailureResponse(
    RecoveryFailureCategory Category,
    RecoveryMetadata Metadata) : RecoveryPortResult;

public interface IRecoveryPort
{
    Task<RecoveryPortResult> ProposeAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken = default);
}

internal static class RecoveryGuards
{
    public static Guid Identifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }

        return value;
    }

    public static string RequiredText(string? value, string parameterName, int maximumLength)
    {
        var result = OptionalText(value, parameterName, maximumLength);
        return result ?? throw new ArgumentException("The value is required.", parameterName);
    }

    public static string? OptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Text must be non-empty, contain no control characters, and be at most {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    public static TimeSpan Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static TimeSpan NonNegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}
