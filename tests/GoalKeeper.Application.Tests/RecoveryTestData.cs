using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

internal static class RecoveryTestData
{
    internal static readonly Guid SessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid ContractId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    internal static readonly Guid GoalId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    internal static readonly Guid InterventionId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    internal static readonly Guid DeviationId = Guid.Parse("50000000-0000-0000-0000-000000000005");
    internal static readonly DateTimeOffset RequestedAtUtc =
        new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    internal static RecoveryRequest Request(
        string? transcript = "I am ready to continue.",
        long sessionVersion = 7,
        IReadOnlyList<RecoveryTurn>? turns = null,
        IReadOnlyList<RecoveryOutcome>? allowedOutcomes = null,
        int maximumCoachingTurns = 3) =>
        new(
            SessionId,
            sessionVersion,
            new RecoveryContractContext(
                ContractId,
                GoalId,
                "Finish the quarterly report",
                "Draft and verify the final report.",
                TimeSpan.FromMinutes(50),
                ReasoningMode.ProfileOnly,
                Sensitivity.Balanced),
            new RecoveryInterventionContext(
                InterventionId,
                DeviationId,
                "Sustained attention to a phone",
                "Phone-directed posture remained visible across three observations.",
                "The cited pattern may conflict with the confirmed Goal.",
                RequestedAtUtc.AddMinutes(-1),
                "The phone has had its turn. Put it down and finish the report you chose."),
            new RecoveryDisputedInterval(
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(12)),
            [
                new RecoveryOverrideContext(
                    Guid.Parse("60000000-0000-0000-0000-000000000006"),
                    DeviationId,
                    "Sustained attention to a phone",
                    "Authenticator use is allowed.",
                    RequestedAtUtc.AddMinutes(-5))
            ],
            allowedOutcomes ?? Enum.GetValues<RecoveryOutcome>(),
            turns ?? [],
            transcript,
            RequestedAtUtc,
            new RecoveryRequestOptions(maximumCoachingTurns));

    internal static RecoveryProposal Proposal(
        RecoveryOutcome outcome,
        RecoveryRequest? request = null,
        string? transcript = null,
        string? clarification = null,
        string? assistantMessage = null,
        bool remainderOverrideConfirmed = false)
    {
        request ??= Request(transcript ?? DefaultTranscript(outcome));
        transcript ??= request.CurrentTranscript;

        return new(
            request.SessionId,
            request.SessionVersion,
            request.Intervention.InterventionId,
            request.NextTurnNumber,
            outcome,
            transcript,
            clarification ?? (outcome == RecoveryOutcome.BehaviorClarification
                ? "The phone was used to approve the report upload."
                : null),
            assistantMessage ?? (outcome switch
            {
                RecoveryOutcome.AdditionalCoaching => "Choose one small report section to finish next.",
                RecoveryOutcome.UnclearResponse => "Could you clarify whether you want to continue or end early?",
                _ => null
            }),
            remainderOverrideConfirmed,
            new RecoveryTurnTiming(
                request.RequestedAtUtc,
                request.RequestedAtUtc.AddMilliseconds(40)),
            Metadata());
    }

    internal static RecoveryMetadata Metadata() =>
        new(
            "scripted-text",
            "deterministic-v1",
            "recovery-v1",
            RecoverySchemaVersions.V1,
            TimeSpan.FromMilliseconds(40),
            "recovery-request-123");

    internal static RecoveryTurn Turn(
        int turnNumber,
        RecoveryOutcome outcome,
        long sessionVersion = 7)
    {
        var transcript = DefaultTranscript(outcome);
        return new(
            Guid.Parse($"70000000-0000-0000-0000-{turnNumber:D12}"),
            SessionId,
            sessionVersion,
            InterventionId,
            turnNumber,
            outcome,
            transcript,
            outcome == RecoveryOutcome.BehaviorClarification ? "This behavior supports the Goal." : null,
            outcome switch
            {
                RecoveryOutcome.AdditionalCoaching => "Take the next small step.",
                RecoveryOutcome.UnclearResponse => "Please clarify your choice.",
                _ => null
            },
            false,
            new RecoveryTurnTiming(
                RequestedAtUtc.AddMinutes(-2).AddSeconds(turnNumber),
                RequestedAtUtc.AddMinutes(-2).AddSeconds(turnNumber).AddMilliseconds(20)),
            Metadata());
    }

    internal static string? DefaultTranscript(RecoveryOutcome outcome) =>
        outcome switch
        {
            RecoveryOutcome.Recommit => "I will return to the report.",
            RecoveryOutcome.BehaviorClarification => "I used the phone to approve the report upload.",
            RecoveryOutcome.EndEarly => "I need to end this Focus Session.",
            RecoveryOutcome.ContinueSession => "I explicitly want to continue.",
            RecoveryOutcome.AdditionalCoaching => "Help me choose the next small step.",
            RecoveryOutcome.UnclearResponse => "I am not sure.",
            RecoveryOutcome.NoResponse => null,
            _ => "unsupported"
        };
}
