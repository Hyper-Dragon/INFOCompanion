using Agent.Core.Config;
using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// Who the companion is told it is, and who it is told it is sitting next to.
/// <para>
/// Both are harness facts rather than character ones, and the pronouns are the reason this has
/// tests: a name like "Peter" is not evidence of anything, and a model left to work a pronoun out
/// will eventually work out the wrong one about somebody. The rule is stated in full rather than
/// implied.
/// </para>
/// </summary>
public class IdentityTests
{
    private static string Prompt(params (string Person, string Value)[] settings)
    {
        var context = new ContextAssembler(new AgentConfig(), "PERSONA");
        foreach (var (person, value) in settings)
        {
            switch (person)
            {
                case "agent":
                    context.AgentName = value;
                    break;
                case "agent-pronouns":
                    context.AgentPronouns = value;
                    break;
                case "player":
                    context.PlayerName = value;
                    break;
                case "player-pronouns":
                    context.PlayerPronouns = value;
                    break;
            }
        }

        return context.Assemble("# Notebook", [], "player said: hello").Messages[0].Content;
    }

    [Fact]
    public void The_names_and_pronouns_are_stated_rather_than_left_to_be_worked_out()
    {
        var prompt = Prompt(
            ("agent", "Alice"),
            ("agent-pronouns", "she/her"),
            ("player", "Peter"),
            ("player-pronouns", "he/him"));

        Assert.Contains("You are Alice (she/her)", prompt);
        Assert.Contains("called Peter (he/him)", prompt);
    }

    [Fact]
    public void The_companion_is_told_not_to_guess_at_anybodys_gender()
    {
        var prompt = Prompt(("player", "Peter"));

        Assert.Contains("Do not guess at anybody's gender", prompt);
        Assert.Contains("do not infer one from a name", prompt);
        Assert.Contains("use their name", prompt);
    }

    [Fact]
    public void With_no_pronouns_configured_it_says_nothing_about_them()
    {
        // Empty is a real answer, not a missing one: a prompt that names a pronoun gives the
        // model something to get wrong, and a person who has not stated one has not stated one.
        var first = Prompt(("agent", "Alice"), ("player", "Peter")).Split('\n')[0];

        Assert.Contains("You are Alice.", first);
        Assert.Contains("called Peter,", first);
        Assert.DoesNotContain("(", first);
    }
}
