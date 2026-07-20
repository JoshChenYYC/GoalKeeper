using System.Text.RegularExpressions;

namespace GoalKeeper.Application.Reasoning;

public static partial class AccountabilityMessagePolicy
{
    private static readonly string[] ForbiddenTerms =
    [
        "idiot",
        "moron",
        "stupid",
        "worthless",
        "pathetic",
        "loser",
        "dumb",
        "incompetent",
        "useless",
        "ugly",
        "brainless",
        "lazy",
        "fuck",
        "shit",
        "damn",
        "bitch",
        "slut",
        "retard"
    ];

    public static bool IsAcceptable(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > ReasoningLimits.MaximumAccountabilityMessageLength ||
            message.Any(char.IsControl))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        if (ForbiddenTerms.Any(term =>
                Regex.IsMatch(
                    normalized,
                    $@"\b{Regex.Escape(term)}\w*\b",
                    RegexOptions.CultureInvariant)))
        {
            return false;
        }

        return !ThreatPattern().IsMatch(normalized) &&
               !DefinitiveFailurePattern().IsMatch(normalized) &&
               SentenceEndingPattern().Count(message) <= 2;
    }

    [GeneratedRegex(
        @"\b(i('| a)?m going to|i will|we('| wi)?ll|you deserve to).{0,24}\b(hurt|kill|harm|destroy)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ThreatPattern();

    [GeneratedRegex(
        @"\b(you('| a)?re going to|you will|you('| wi)?ll)\s+(definitely\s+)?fail\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex DefinitiveFailurePattern();

    [GeneratedRegex(@"[.!?]+(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndingPattern();
}
