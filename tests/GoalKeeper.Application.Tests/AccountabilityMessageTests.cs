using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Application.Tests;

public sealed class AccountabilityMessageTests
{
    [Theory]
    [InlineData("Put the phone down and get back to the work.", true)]
    [InlineData("", false)]
    [InlineData("You are an idiot.", false)]
    [InlineData("You will definitely fail.", false)]
    [InlineData("I will hurt you if you keep scrolling.", false)]
    [InlineData("Stop. Put it down. Get back to work.", false)]
    public void Provider_message_policy_rejects_unbounded_or_hostile_content(
        string message,
        bool accepted)
    {
        Assert.Equal(accepted, AccountabilityMessagePolicy.IsAcceptable(message));
    }

    [Fact]
    public void Provider_message_policy_enforces_the_280_character_limit()
    {
        Assert.True(AccountabilityMessagePolicy.IsAcceptable(new string('a', 280)));
        Assert.False(AccountabilityMessagePolicy.IsAcceptable(new string('a', 281)));
    }

    [Fact]
    public void Legacy_fallback_is_stable_bounded_and_does_not_invent_stakes()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var first = AccountabilityMessageFactory.Create(
            id,
            "Sort receipts for the archive",
            "looking at a phone");
        var second = AccountabilityMessageFactory.Create(
            id,
            "Sort receipts for the archive",
            "looking at a phone");

        Assert.Equal(first, second);
        Assert.InRange(first.Length, 1, 280);
        Assert.Contains("“Sort receipts for the archive", first);
        Assert.DoesNotContain("OA", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deadline", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you will fail", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_fallback_quotes_and_bounds_arbitrary_goal_titles()
    {
        var message = AccountabilityMessageFactory.Create(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            string.Concat("A title with “quotes”\r\n", new string('x', 500)),
            new string('y', 500));

        Assert.InRange(message.Length, 1, 280);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\n', message);
        Assert.Contains('“', message);
        Assert.Contains('”', message);
    }

    [Fact]
    public void Legacy_fallback_does_not_echo_hostile_user_context()
    {
        var message = AccountabilityMessageFactory.Create(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            "You are an idiot",
            "damn phone");

        Assert.True(AccountabilityMessagePolicy.IsAcceptable(message));
        Assert.DoesNotContain("idiot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("damn", message, StringComparison.OrdinalIgnoreCase);
    }
}
