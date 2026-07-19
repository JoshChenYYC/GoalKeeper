namespace GoalKeeper.Web.Presentation;

public static class OnboardingReturnPath
{
    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            value[0] != '/' ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Relative, out _))
        {
            return null;
        }

        return value;
    }
}
