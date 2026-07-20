using GoalKeeper.Application.Reasoning;

namespace GoalKeeper.Application.Recovery;

public static class RecoveryOpeningPrompt
{
    public const string CheckInQuestion =
        "So, why aren't you focused right now?";

    public static string Create(RecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Create(AccountabilityMessageFactory.Resolve(request));
    }

    public static string Create(string accountabilityMessage)
    {
        if (!AccountabilityMessagePolicy.IsAcceptable(accountabilityMessage))
        {
            throw new ArgumentException(
                "The accountability message is not safe for playback.",
                nameof(accountabilityMessage));
        }

        return string.Concat(
            "This is an AI-generated voice. ",
            accountabilityMessage,
            " ",
            CheckInQuestion);
    }
}
