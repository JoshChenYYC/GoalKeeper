using GoalKeeper.Domain;

namespace GoalKeeper.Domain.Tests;

public sealed class EvidenceEpisodePolicyTests
{
    [Fact]
    public void Policy_builds_a_bounded_ordered_episode_from_authoritative_references()
    {
        var sessionId = Guid.NewGuid();
        var deviationId = Guid.NewGuid();
        var first = ObservationReference.Create("00000000-0000-0000-0000-000000000001",
            sessionId, TimeSpan.FromSeconds(1));
        var middle = ObservationReference.Create("00000000-0000-0000-0000-000000000002",
            sessionId, TimeSpan.FromSeconds(2));
        var latest = ObservationReference.Create("00000000-0000-0000-0000-000000000003",
            sessionId, TimeSpan.FromSeconds(3));

        var result = new EvidenceEpisodePolicy().Create(
            sessionId,
            DeviationReference.Listed(deviationId),
            first,
            latest,
            [first, latest],
            [middle]);

        Assert.Equal([first, middle, latest], result.Episode.Observations);
        Assert.Equal([first, latest], result.KeyObservations);
        Assert.Equal([middle], result.ContradictoryObservations);
    }

    [Fact]
    public void Policy_rejects_reordered_cross_session_out_of_interval_and_unbounded_references()
    {
        var sessionId = Guid.NewGuid();
        var first = Reference(sessionId, 2);
        var latest = Reference(sessionId, 3);
        var policy = new EvidenceEpisodePolicy();

        Assert.Throws<DomainRuleViolationException>(() => policy.Create(
            sessionId,
            DeviationReference.Unlisted("Unlisted"),
            latest,
            first,
            [first],
            []));
        Assert.Throws<DomainRuleViolationException>(() => policy.Create(
            sessionId,
            DeviationReference.Unlisted("Unlisted"),
            first,
            latest,
            [Reference(Guid.NewGuid(), 2)],
            []));
        Assert.Throws<DomainRuleViolationException>(() => policy.Create(
            sessionId,
            DeviationReference.Unlisted("Unlisted"),
            first,
            latest,
            [Reference(sessionId, 4)],
            []));
        Assert.Throws<DomainRuleViolationException>(() => policy.Create(
            sessionId,
            DeviationReference.Unlisted("Unlisted"),
            first,
            latest,
            Enumerable.Range(0, 5).Select(index => Reference(sessionId, 2 + index * 0.1)),
            []));
    }

    private static ObservationReference Reference(Guid sessionId, double seconds) =>
        ObservationReference.Create(
            Guid.NewGuid().ToString("D"),
            sessionId,
            TimeSpan.FromSeconds(seconds));
}
