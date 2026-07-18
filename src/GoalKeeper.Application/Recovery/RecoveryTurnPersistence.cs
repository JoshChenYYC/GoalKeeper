using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoalKeeper.Application.Recovery;

public static class RecoveryTurnPersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    public static RecoveryTurnWrite ToWrite(RecoveryTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var document = new RecoveryOutcomeDocument(
            turn.SessionVersion,
            turn.Outcome,
            turn.Clarification,
            turn.AssistantMessage,
            turn.RemainderOverrideConfirmed,
            turn.Timing,
            turn.Metadata);

        return new(
            turn.Id,
            turn.SessionId,
            turn.InterventionId,
            turn.TurnNumber,
            JsonSerializer.Serialize(document, SerializerOptions),
            turn.Transcript,
            turn.Timing.CompletedAtUtc);
    }

    public static RecoveryTurn FromView(RecoveryTurnView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        RecoveryOutcomeDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RecoveryOutcomeDocument>(
                view.Outcome,
                SerializerOptions) ?? throw new JsonException(
                "The Recovery outcome document is missing.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The persisted Recovery outcome document is invalid.",
                nameof(view),
                exception);
        }

        return new(
            view.Id,
            view.SessionId,
            document.SessionVersion,
            view.InterventionId,
            view.TurnNumber,
            document.StructuredOutcome,
            view.Transcript,
            document.Clarification,
            document.AssistantMessage,
            document.RemainderOverrideConfirmed,
            document.Timing,
            document.Metadata);
    }

    private sealed record RecoveryOutcomeDocument(
        long SessionVersion,
        RecoveryOutcome StructuredOutcome,
        string? Clarification,
        string? AssistantMessage,
        bool RemainderOverrideConfirmed,
        RecoveryTurnTiming Timing,
        RecoveryMetadata Metadata);
}
