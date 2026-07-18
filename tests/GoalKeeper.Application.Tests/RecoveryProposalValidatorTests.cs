using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Application.Tests;

public sealed class RecoveryProposalValidatorTests
{
    [Fact]
    public void Every_allowed_single_turn_outcome_has_a_deterministic_valid_proposal()
    {
        foreach (var outcome in Enum.GetValues<RecoveryOutcome>())
        {
            var request = RecoveryTestData.Request(RecoveryTestData.DefaultTranscript(outcome));
            var proposal = RecoveryTestData.Proposal(outcome, request);

            var valid = Assert.IsType<ValidRecoveryProposal>(
                RecoveryProposalValidator.Validate(request, proposal));

            Assert.Same(proposal, valid.Proposal);
        }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("intervention")]
    [InlineData("turn")]
    public void Stale_or_out_of_order_proposals_are_rejected(string stalePart)
    {
        var request = RecoveryTestData.Request();
        var proposal = RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request);
        proposal = stalePart switch
        {
            "session" => proposal with { SessionId = Guid.NewGuid() },
            "version" => proposal with { SessionVersion = request.SessionVersion - 1 },
            "intervention" => proposal with { InterventionId = Guid.NewGuid() },
            "turn" => proposal with { TurnNumber = request.NextTurnNumber + 1 },
            _ => proposal
        };

        var invalid = Assert.IsType<InvalidRecoveryProposal>(
            RecoveryProposalValidator.Validate(request, proposal));

        Assert.Contains(
            invalid.Failure.Issues,
            issue => issue.Code is
                RecoveryValidationErrorCode.StaleSession or
                RecoveryValidationErrorCode.StaleVersion or
                RecoveryValidationErrorCode.StaleIntervention or
                RecoveryValidationErrorCode.InvalidTurnOrder);
    }

    [Fact]
    public void Missing_proposal_identities_are_rejected()
    {
        var request = RecoveryTestData.Request();
        var proposal = RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request);

        AssertIssue(
            proposal with { SessionId = Guid.Empty },
            request,
            RecoveryValidationErrorCode.InvalidIdentity);
        AssertIssue(
            proposal with { InterventionId = Guid.Empty },
            request,
            RecoveryValidationErrorCode.InvalidIdentity);
    }

    [Fact]
    public void Undefined_and_disallowed_outcomes_are_rejected()
    {
        var request = RecoveryTestData.Request(
            allowedOutcomes: [RecoveryOutcome.Recommit]);
        var undefined = RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request) with
        {
            Outcome = (RecoveryOutcome)999
        };
        var disallowed = RecoveryTestData.Proposal(
            RecoveryOutcome.EndEarly,
            request,
            transcript: request.CurrentTranscript);

        AssertIssue(undefined, request, RecoveryValidationErrorCode.InvalidOutcome);
        AssertIssue(disallowed, request, RecoveryValidationErrorCode.OutcomeNotAllowed);
    }

    [Fact]
    public void Outcome_specific_required_and_forbidden_fields_are_enforced()
    {
        var clarificationRequest = RecoveryTestData.Request("This was report work.");
        var missingClarification = RecoveryTestData.Proposal(
            RecoveryOutcome.BehaviorClarification,
            clarificationRequest) with
        {
            Clarification = null
        };
        var coachingRequest = RecoveryTestData.Request("Please coach me.");
        var missingCoaching = RecoveryTestData.Proposal(
            RecoveryOutcome.AdditionalCoaching,
            coachingRequest) with
        {
            AssistantMessage = null
        };
        var recommitRequest = RecoveryTestData.Request("I will continue.");
        var unexpected = RecoveryTestData.Proposal(
            RecoveryOutcome.Recommit,
            recommitRequest) with
        {
            Clarification = "Not valid here.",
            RemainderOverrideConfirmed = true
        };

        AssertIssue(
            missingClarification,
            clarificationRequest,
            RecoveryValidationErrorCode.MissingField);
        AssertIssue(missingCoaching, coachingRequest, RecoveryValidationErrorCode.MissingField);
        AssertIssue(unexpected, recommitRequest, RecoveryValidationErrorCode.UnexpectedField);
    }

    [Fact]
    public void Transcript_timing_and_metadata_are_authoritatively_validated()
    {
        var request = RecoveryTestData.Request("I will continue.");
        var proposal = RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request);

        AssertIssue(
            proposal with { Transcript = "Changed by adapter." },
            request,
            RecoveryValidationErrorCode.InvalidValue);
        AssertIssue(
            proposal with { Transcript = null },
            request,
            RecoveryValidationErrorCode.MissingField);
        AssertIssue(
            proposal with { Timing = null },
            request,
            RecoveryValidationErrorCode.MissingField);
        AssertIssue(
            proposal with
            {
                Timing = new RecoveryTurnTiming(
                    request.RequestedAtUtc.AddSeconds(1),
                    request.RequestedAtUtc)
            },
            request,
            RecoveryValidationErrorCode.InvalidTiming);
        AssertIssue(
            proposal with { Metadata = null },
            request,
            RecoveryValidationErrorCode.MissingField);
        AssertIssue(
            proposal with
            {
                Metadata = RecoveryTestData.Metadata() with { SchemaVersion = 2 }
            },
            request,
            RecoveryValidationErrorCode.InvalidMetadata);
        AssertIssue(
            proposal with
            {
                Metadata = RecoveryTestData.Metadata() with { RequestId = "bad\nid" }
            },
            request,
            RecoveryValidationErrorCode.InvalidMetadata);
        AssertIssue(
            proposal with
            {
                Metadata = RecoveryTestData.Metadata() with { Latency = TimeSpan.FromMilliseconds(-1) }
            },
            request,
            RecoveryValidationErrorCode.InvalidMetadata);
    }

    [Fact]
    public void Coaching_cap_uses_persisted_turns_across_retries_and_adapter_instances()
    {
        var persistedTurns = new[]
        {
            RecoveryTestData.Turn(1, RecoveryOutcome.AdditionalCoaching),
            RecoveryTestData.Turn(2, RecoveryOutcome.AdditionalCoaching)
        };
        var request = RecoveryTestData.Request(
            "Please give me more coaching.",
            turns: persistedTurns,
            maximumCoachingTurns: 2);
        var proposal = RecoveryTestData.Proposal(RecoveryOutcome.AdditionalCoaching, request);

        var firstAttempt = RecoveryProposalValidator.Validate(request, proposal);
        var retryWithFreshValidatorCall = RecoveryProposalValidator.Validate(request, proposal);

        AssertIssue(firstAttempt, RecoveryValidationErrorCode.CoachingCapReached);
        AssertIssue(retryWithFreshValidatorCall, RecoveryValidationErrorCode.CoachingCapReached);

        var beforeCap = RecoveryTestData.Request(
            "Please give me more coaching.",
            turns: [persistedTurns[0]],
            maximumCoachingTurns: 2);
        Assert.IsType<ValidRecoveryProposal>(
            RecoveryProposalValidator.Validate(
                beforeCap,
                RecoveryTestData.Proposal(RecoveryOutcome.AdditionalCoaching, beforeCap)));
    }

    [Fact]
    public void Invalid_proposal_cannot_be_converted_to_a_persisted_turn()
    {
        var request = RecoveryTestData.Request();
        var invalid = RecoveryProposalValidator.Validate(
            request,
            RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request) with
            {
                SessionVersion = request.SessionVersion - 1
            });

        Assert.IsType<InvalidRecoveryProposal>(invalid);
        Assert.DoesNotContain(
            typeof(RecoveryTurnFactory).GetMethods(),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(InvalidRecoveryProposal)));
    }

    private static void AssertIssue(
        RecoveryProposal proposal,
        RecoveryRequest request,
        RecoveryValidationErrorCode expectedCode) =>
        AssertIssue(RecoveryProposalValidator.Validate(request, proposal), expectedCode);

    private static void AssertIssue(
        RecoveryProposalValidation validation,
        RecoveryValidationErrorCode expectedCode)
    {
        var invalid = Assert.IsType<InvalidRecoveryProposal>(validation);
        Assert.Contains(invalid.Failure.Issues, issue => issue.Code == expectedCode);
    }
}
