using System.Text;

namespace GoalKeeper.Application.Recovery;

public static class AccountabilityMessageFactory
{
    private static readonly Func<string, string, string>[] Templates =
    [
        (goal, deviation) =>
            $"You chose “{goal},” and “{deviation}” is a strange way to follow through. Drop the distraction and get back to the next useful action.",
        (goal, deviation) =>
            $"Do you want to make progress on “{goal},” or keep choosing “{deviation}”? Pick one, then act like you meant it.",
        (goal, deviation) =>
            $"“{goal}” is still the job. “{deviation}” can wait; get back to what you said mattered.",
        (goal, deviation) =>
            $"Quick reality check: “{deviation}” is not moving “{goal}” forward. Put it aside and resume the plan."
    ];

    public static string Create(
        Guid interventionId,
        string goalTitle,
        string deviationDescription)
    {
        var goal = Bounded(goalTitle, 92);
        var deviation = Bounded(deviationDescription, 92);
        var bytes = interventionId.ToByteArray();
        var index = BitConverter.ToUInt32(bytes, 0) % Templates.Length;
        var result = Templates[index](goal, deviation);
        result = result.Length <= RecoveryLimits.MaximumAccountabilityMessageLength
            ? result
            : string.Concat(
                result.AsSpan(
                    0,
                    RecoveryLimits.MaximumAccountabilityMessageLength - 1),
                "…");
        return GoalKeeper.Application.Reasoning.AccountabilityMessagePolicy
            .IsAcceptable(result)
            ? result
            : "That visible choice is not part of the plan. Put the distraction aside and get back to the next useful action.";
    }

    public static string Resolve(RecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(
            request.Intervention.InterventionId,
            request.Contract.GoalTitle,
            request.Intervention.DeviationDescription,
            request.Intervention.AccountabilityMessage);
    }

    public static string Resolve(
        Guid interventionId,
        string goalTitle,
        string deviationDescription,
        string? candidate) =>
        GoalKeeper.Application.Reasoning.AccountabilityMessagePolicy
            .IsAcceptable(candidate)
            ? candidate!
            : Create(interventionId, goalTitle, deviationDescription);

    private static string Bounded(string value, int maximumLength)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character is '“' or '”' ? '"' : character);
            previousWasWhitespace = false;
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }
}
