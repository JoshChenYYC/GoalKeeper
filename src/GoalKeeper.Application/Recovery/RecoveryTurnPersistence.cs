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

    private sealed record RecoveryOutcomeDocument(
        long SessionVersion,
        RecoveryOutcome StructuredOutcome,
        string? Clarification,
        string? AssistantMessage,
        bool RemainderOverrideConfirmed,
        RecoveryTurnTiming Timing,
        RecoveryMetadata Metadata);
}
