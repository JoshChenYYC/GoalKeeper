using System.Reflection;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

public sealed class ReasoningContractTests
{
    [Fact]
    public void Request_is_bounded_and_has_no_room_image_surface()
    {
        var properties = typeof(ReasoningRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var prohibited = new[] { "image", "jpeg", "bytes", "snapshotpath", "rawbody" };

        Assert.DoesNotContain(properties,
            property => prohibited.Any(value =>
                property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(properties,
            property => property.PropertyType == typeof(byte[]) ||
                        property.PropertyType == typeof(ReadOnlyMemory<byte>));
        Assert.Equal(12, ReasoningLimits.RecentObservations);
        Assert.Equal(4, EvidenceEpisodePolicy.DefaultMaximumKeyObservations);
        Assert.Equal(4, EvidenceEpisodePolicy.DefaultMaximumContradictoryObservations);
    }

    [Fact]
    public async Task Deterministic_fake_scripts_every_controller_scenario_without_network()
    {
        var deviationId = Guid.NewGuid();
        var invalidId = Guid.NewGuid();
        var expected = new InvalidOperationException("scripted");
        var fake = new DeterministicReasoningFake(
        [
            ReasoningFakeStep.Continue(),
            ReasoningFakeStep.ListedIntervention(deviationId),
            ReasoningFakeStep.ExploratoryIntervention("Unlisted behavior"),
            ReasoningFakeStep.StaleResult(),
            ReasoningFakeStep.InvalidReferences(invalidId),
            ReasoningFakeStep.Throw(expected)
        ]);
        var request = Request(deviationId);

        Assert.Equal(
            ReasoningDecision.ContinueObserving,
            Assert.IsType<ReasoningSuccess>(await fake.EvaluateAsync(request)).Proposal.Decision);
        var listed = Assert.IsType<ReasoningSuccess>(await fake.EvaluateAsync(request));
        Assert.Equal(deviationId, listed.Proposal.Intervention!.ListedDeviationId);
        var exploratory = Assert.IsType<ReasoningSuccess>(await fake.EvaluateAsync(request));
        Assert.Equal("Unlisted behavior", exploratory.Proposal.Intervention!.UnlistedDescription);
        var stale = Assert.IsType<ReasoningSuccess>(await fake.EvaluateAsync(request));
        Assert.NotEqual(request.SessionVersion, stale.Proposal.SessionVersion);
        var invalid = Assert.IsType<ReasoningSuccess>(await fake.EvaluateAsync(request));
        Assert.Contains(invalidId, invalid.Proposal.Intervention!.KeyObservationIds);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.EvaluateAsync(request)));
        Assert.Equal(6, fake.Requests.Count);
    }

    private static ReasoningRequest Request(Guid deviationId)
    {
        var sessionId = Guid.NewGuid();
        var observation = new ReasoningObservation(
            Guid.NewGuid(),
            3,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(2),
            new Observation(
                ObservationSchemaVersions.V1,
                new(ImageQualityValue.Adequate, []),
                new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
                ["laptop"],
                []));
        return new(
            sessionId,
            3,
            FocusSessionState.Focusing,
            new(
                Guid.NewGuid(),
                "Write",
                null,
                TimeSpan.FromMinutes(25),
                [new(deviationId, "Phone", VisualObservability.Observable)],
                ReasoningMode.Exploratory,
                Sensitivity.Balanced),
            [],
            [],
            [],
            [],
            [],
            observation,
            [observation]);
    }
}
