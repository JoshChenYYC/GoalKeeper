using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Infrastructure.Recovery.Conversation;

internal static class RecoveryConversationWireContracts
{
    private const int MaximumWireRequestBytes = 512 * 1024;

    public static bool TrySerializeRequest(
        RecoveryRequest request,
        out string serialized)
    {
        try
        {
            var payload = new JsonObject
            {
                ["schema_version"] = request.Options.SchemaVersion,
                ["contract"] = new JsonObject
                {
                    ["goal_title"] = request.Contract.GoalTitle,
                    ["goal_description"] = request.Contract.GoalDescription,
                    ["target_focus_seconds"] =
                        request.Contract.TargetFocusDuration.TotalSeconds,
                    ["reasoning_mode"] = SnakeCase(
                        request.Contract.ReasoningMode.ToString()),
                    ["sensitivity"] = SnakeCase(
                        request.Contract.Sensitivity.ToString())
                },
                ["intervention"] = new JsonObject
                {
                    ["is_unlisted"] = request.Intervention.IsUnlisted,
                    ["deviation_description"] =
                        request.Intervention.DeviationDescription,
                    ["evidence_summary"] = request.Intervention.EvidenceSummary,
                    ["rationale"] = request.Intervention.Rationale,
                    ["accountability_message"] =
                        AccountabilityMessageFactory.Resolve(request),
                    ["admitted_at_utc"] =
                        request.Intervention.AdmittedAtUtc.ToString("O")
                },
                ["disputed_interval"] = new JsonObject
                {
                    ["started_at_seconds"] =
                        request.DisputedInterval.StartedAt.TotalSeconds,
                    ["ended_at_seconds"] =
                        request.DisputedInterval.EndedAt.TotalSeconds
                },
                ["active_overrides"] = new JsonArray(
                    request.ActiveOverrides.Select(ToWireOverride).ToArray()),
                ["allowed_outcomes"] = new JsonArray(
                    request.AllowedOutcomes
                        .Select(outcome => JsonValue.Create(OutcomeName(outcome)))
                        .ToArray()),
                ["current_check_in_turns"] = new JsonArray(
                    request.CurrentCheckInTurns.Select(ToWireTurn).ToArray()),
                ["current_transcript"] = request.CurrentTranscript,
                ["policy"] = new JsonObject
                {
                    ["next_turn_number"] = request.NextTurnNumber,
                    ["maximum_coaching_turns"] =
                        request.Options.MaximumCoachingTurns,
                    ["persisted_coaching_turn_count"] =
                        request.PersistedCoachingTurnCount
                },
                ["requested_at_utc"] = request.RequestedAtUtc.ToString("O")
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            if (bytes.Length > MaximumWireRequestBytes)
            {
                serialized = string.Empty;
                return false;
            }

            serialized = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
            ArgumentException or
            InvalidOperationException)
        {
            serialized = string.Empty;
            return false;
        }
    }

    public static bool TryParseDecision(
        ReadOnlyMemory<byte> json,
        out RecoveryConversationDecision? decision)
    {
        decision = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "outcome",
                "clarification",
                "assistant_message",
                "remainder_override_confirmed"
            };
            foreach (var property in root.EnumerateObject())
            {
                if (!expected.Remove(property.Name))
                {
                    return false;
                }
            }

            if (expected.Count != 0 ||
                !root.TryGetProperty("outcome", out var outcomeElement) ||
                outcomeElement.ValueKind != JsonValueKind.String ||
                !TryOutcome(outcomeElement.GetString(), out var outcome) ||
                !TryOptionalString(root, "clarification", out var clarification) ||
                !TryOptionalString(
                    root,
                    "assistant_message",
                    out var assistantMessage) ||
                !root.TryGetProperty(
                    "remainder_override_confirmed",
                    out var confirmedElement) ||
                confirmedElement.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            decision = new(
                outcome,
                clarification,
                assistantMessage,
                confirmedElement.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string OutcomeName(RecoveryOutcome outcome) =>
        outcome switch
        {
            RecoveryOutcome.Recommit => "recommit",
            RecoveryOutcome.BehaviorClarification => "behavior_clarification",
            RecoveryOutcome.EndEarly => "end_early",
            RecoveryOutcome.ContinueSession => "continue_session",
            RecoveryOutcome.AdditionalCoaching => "additional_coaching",
            RecoveryOutcome.UnclearResponse => "unclear_response",
            RecoveryOutcome.NoResponse => "no_response",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static JsonNode? ToWireOverride(RecoveryOverrideContext value) =>
        new JsonObject
        {
            ["deviation_description"] = value.DeviationDescription,
            ["reason"] = value.Reason,
            ["applied_at_utc"] = value.AppliedAtUtc.ToString("O")
        };

    private static JsonNode? ToWireTurn(RecoveryTurn value) =>
        new JsonObject
        {
            ["turn_number"] = value.TurnNumber,
            ["outcome"] = OutcomeName(value.Outcome),
            ["transcript"] = value.Transcript,
            ["clarification"] = value.Clarification,
            ["assistant_message"] = value.AssistantMessage,
            ["remainder_override_confirmed"] =
                value.RemainderOverrideConfirmed
        };

    private static bool TryOptionalString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryOutcome(
        string? value,
        out RecoveryOutcome outcome)
    {
        outcome = value switch
        {
            "recommit" => RecoveryOutcome.Recommit,
            "behavior_clarification" => RecoveryOutcome.BehaviorClarification,
            "end_early" => RecoveryOutcome.EndEarly,
            "continue_session" => RecoveryOutcome.ContinueSession,
            "additional_coaching" => RecoveryOutcome.AdditionalCoaching,
            "unclear_response" => RecoveryOutcome.UnclearResponse,
            "no_response" => RecoveryOutcome.NoResponse,
            _ => (RecoveryOutcome)(-1)
        };
        return Enum.IsDefined(outcome);
    }

    private static string SnakeCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
}

internal sealed record RecoveryConversationDecision(
    RecoveryOutcome Outcome,
    string? Clarification,
    string? AssistantMessage,
    bool RemainderOverrideConfirmed);
