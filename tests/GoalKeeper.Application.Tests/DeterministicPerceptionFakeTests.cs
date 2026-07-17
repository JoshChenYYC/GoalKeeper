using GoalKeeper.Application.Perception;

namespace GoalKeeper.Application.Tests;

public sealed class DeterministicPerceptionFakeTests
{
    [Fact]
    public async Task Fake_scripts_valid_invalid_and_failed_results_in_order()
    {
        var success = new PerceptionSuccess(Observation(), PerceptionContractTests.Metadata());
        var invalid = new PerceptionInvalid(
            new ObservationValidationFailure(
                [new("$", ObservationValidationErrorCode.MalformedJson, "Malformed.")]),
            PerceptionContractTests.Metadata());
        var failed = new PerceptionFailure(
            PerceptionFailureCategory.Network,
            PerceptionContractTests.Metadata());
        var fake = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Return(success),
            PerceptionFakeStep.Return(invalid),
            PerceptionFakeStep.Return(failed)
        ]);

        Assert.Same(success, await fake.ObserveAsync(Request()));
        Assert.Same(invalid, await fake.ObserveAsync(Request()));
        Assert.Same(failed, await fake.ObserveAsync(Request()));
        Assert.Equal(3, fake.Requests.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.ObserveAsync(Request()));
    }

    [Fact]
    public async Task Delayed_step_waits_for_an_explicit_deterministic_release()
    {
        var expected = new PerceptionSuccess(Observation(), PerceptionContractTests.Metadata());
        var fake = new DeterministicPerceptionFake([PerceptionFakeStep.Delayed(expected)]);

        var operation = fake.ObserveAsync(Request());

        Assert.False(operation.IsCompleted);
        Assert.Equal(1, fake.PendingDelayCount);
        fake.ReleaseNextDelay();
        Assert.Same(expected, await operation);
        Assert.Equal(0, fake.PendingDelayCount);
    }

    [Fact]
    public async Task Delayed_and_scripted_cancellation_are_observable_without_wall_clock_waits()
    {
        var fake = new DeterministicPerceptionFake(
        [
            PerceptionFakeStep.Delayed(
                new PerceptionSuccess(Observation(), PerceptionContractTests.Metadata())),
            PerceptionFakeStep.Cancelled()
        ]);
        using var cancellation = new CancellationTokenSource();

        var delayed = fake.ObserveAsync(Request(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayed);
        Assert.Equal(0, fake.PendingDelayCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.ObserveAsync(Request()));
    }

    [Fact]
    public async Task Pre_cancelled_calls_do_not_consume_steps_or_record_requests()
    {
        var expected = new PerceptionSuccess(Observation(), PerceptionContractTests.Metadata());
        var fake = new DeterministicPerceptionFake([PerceptionFakeStep.Return(expected)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fake.ObserveAsync(Request(), cancellation.Token));

        Assert.Empty(fake.Requests);
        Assert.Same(expected, await fake.ObserveAsync(Request()));
    }

    [Fact]
    public async Task Thrown_failure_is_deterministic_and_preserves_the_scripted_exception()
    {
        var expected = new InvalidOperationException("scripted adapter failure");
        var fake = new DeterministicPerceptionFake([PerceptionFakeStep.Throw(expected)]);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.ObserveAsync(Request()));

        Assert.Same(expected, actual);
    }

    private static PerceptionRequest Request() =>
        new(new byte[] { 0xff, 0xd8, 0x01, 0xff, 0xd9 });

    private static Observation Observation() =>
        new(
            ObservationSchemaVersions.V1,
            new ImageQuality(ImageQualityValue.Adequate, []),
            new PeopleCount(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
            ["laptop"],
            [
                new VisibleCue(
                    VisibleCueSubject.VisiblePerson,
                    VisibleCueKind.Posture,
                    VisibleCueState.Observed,
                    VisualSupport.Direct,
                    "person seated",
                    "upper body is upright",
                    [])
            ]);
}
