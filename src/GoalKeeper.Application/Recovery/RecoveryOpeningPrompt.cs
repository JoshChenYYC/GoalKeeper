namespace GoalKeeper.Application.Recovery;

public static class RecoveryOpeningPrompt
{
    private const int MaximumDeviationDescriptionLength = 320;
    private const int MaximumEvidenceSummaryLength = 480;

    public static string Create(RecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var duration = ApproximateDuration(request.DisputedInterval.Duration);
        var deviation = Bounded(
            request.Intervention.DeviationDescription,
            MaximumDeviationDescriptionLength);
        var evidence = Bounded(
            request.Intervention.EvidenceSummary,
            MaximumEvidenceSummaryLength);
        var accountabilityMessage = AccountabilityMessageFactory.Resolve(request);

        return string.Concat(
            "This is an AI-generated voice. ",
            accountabilityMessage,
            " GoalKeeper based this interruption on ",
            duration,
            " of limited camera observations associated with ",
            deviation,
            ". This interpretation may be wrong. Evidence summary: ",
            evidence,
            " What happened?");
    }

    private static string ApproximateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "less than a minute";
        }

        var minutes = Math.Max(
            1,
            (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes == 1 ? "about 1 minute" : $"about {minutes} minutes";
    }

    private static string Bounded(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : string.Concat(value.AsSpan(0, maximumLength - 1), "…");
}
