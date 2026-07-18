using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Application.Tests;

public sealed class DeterministicTextRecoveryFakeTests
{
    [Fact]
    public async Task Fake_scripts_every_single_turn_outcome_in_order()
    {
        var outcomes = Enum.GetValues<RecoveryOutcome>();
        var steps = outcomes.Select(outcome =>
        {
            var request = RecoveryTestData.Request(RecoveryTestData.DefaultTranscript(outcome));
            return RecoveryFakeStep.Return(
                new RecoveryProposalResponse(RecoveryTestData.Proposal(outcome, request)));
        });
        var fake = new DeterministicTextRecoveryFake(steps);

        foreach (var outcome in outcomes)
        {
            var request = RecoveryTestData.Request(RecoveryTestData.DefaultTranscript(outcome));
            var response = Assert.IsType<RecoveryProposalResponse>(
                await fake.ProposeAsync(request));
            Assert.Equal(outcome, response.Proposal.Outcome);
        }

        Assert.Equal(outcomes.Length, fake.Requests.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.ProposeAsync(RecoveryTestData.Request()));
    }

    [Fact]
    public async Task Fake_scripts_repeated_coaching_clarification_and_silence()
    {
        var coachingRequest = RecoveryTestData.Request("Please help.");
        var clarificationRequest = RecoveryTestData.Request("This supports the report.");
        var silenceRequest = RecoveryTestData.Request(null);
        var fake = new DeterministicTextRecoveryFake(
        [
            RecoveryFakeStep.Return(
                new RecoveryProposalResponse(
                    RecoveryTestData.Proposal(RecoveryOutcome.AdditionalCoaching, coachingRequest))),
            RecoveryFakeStep.Return(
                new RecoveryProposalResponse(
                    RecoveryTestData.Proposal(RecoveryOutcome.AdditionalCoaching, coachingRequest))),
            RecoveryFakeStep.Return(
                new RecoveryProposalResponse(
                    RecoveryTestData.Proposal(
                        RecoveryOutcome.BehaviorClarification,
                        clarificationRequest))),
            RecoveryFakeStep.Return(
                new RecoveryProposalResponse(
                    RecoveryTestData.Proposal(RecoveryOutcome.NoResponse, silenceRequest)))
        ]);

        Assert.Equal(
            RecoveryOutcome.AdditionalCoaching,
            (await Proposal(fake, coachingRequest)).Outcome);
        Assert.Equal(
            RecoveryOutcome.AdditionalCoaching,
            (await Proposal(fake, coachingRequest)).Outcome);
        Assert.Equal(
            RecoveryOutcome.BehaviorClarification,
            (await Proposal(fake, clarificationRequest)).Outcome);
        Assert.Equal(
            RecoveryOutcome.NoResponse,
            (await Proposal(fake, silenceRequest)).Outcome);
    }

    [Fact]
    public async Task Fake_scripts_stale_results_and_typed_failures_without_domain_mutation()
    {
        var request = RecoveryTestData.Request();
        var stale = RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request) with
        {
            SessionVersion = request.SessionVersion - 1
        };
        var failure = new RecoveryFailureResponse(
            RecoveryFailureCategory.ProviderUnavailable,
            RecoveryTestData.Metadata());
        var fake = new DeterministicTextRecoveryFake(
        [
            RecoveryFakeStep.Return(new RecoveryProposalResponse(stale)),
            RecoveryFakeStep.Return(failure)
        ]);

        var staleResponse = Assert.IsType<RecoveryProposalResponse>(
            await fake.ProposeAsync(request));
        Assert.IsType<InvalidRecoveryProposal>(
            RecoveryProposalValidator.Validate(request, staleResponse.Proposal));
        Assert.Same(failure, await fake.ProposeAsync(request));
    }

    [Fact]
    public async Task Delayed_cancelled_and_thrown_steps_are_deterministic()
    {
        var request = RecoveryTestData.Request();
        var expected = new RecoveryProposalResponse(
            RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request));
        var exception = new InvalidOperationException("scripted Recovery failure");
        var fake = new DeterministicTextRecoveryFake(
        [
            RecoveryFakeStep.Delayed(expected),
            RecoveryFakeStep.Cancelled(),
            RecoveryFakeStep.Throw(exception)
        ]);

        var delayed = fake.ProposeAsync(request);
        Assert.False(delayed.IsCompleted);
        Assert.Equal(1, fake.PendingDelayCount);
        fake.ReleaseNextDelay();
        Assert.Same(expected, await delayed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.ProposeAsync(request));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => fake.ProposeAsync(request));
        Assert.Same(exception, actual);
    }

    [Fact]
    public async Task Pre_cancelled_calls_do_not_consume_scripts_or_record_requests()
    {
        var request = RecoveryTestData.Request();
        var expected = new RecoveryProposalResponse(
            RecoveryTestData.Proposal(RecoveryOutcome.Recommit, request));
        var fake = new DeterministicTextRecoveryFake([RecoveryFakeStep.Return(expected)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fake.ProposeAsync(request, cancellation.Token));

        Assert.Empty(fake.Requests);
        Assert.Same(expected, await fake.ProposeAsync(request));
    }

    private static async Task<RecoveryProposal> Proposal(
        DeterministicTextRecoveryFake fake,
        RecoveryRequest request) =>
        (Assert.IsType<RecoveryProposalResponse>(await fake.ProposeAsync(request))).Proposal;
}
