using Agent.Core.Config;

namespace Agent.Tests;

/// <summary>
/// The personality file's two halves (04_companion.md §11 (`C61`–`C67`)).
/// <para>
/// Worth testing because two processes read the same file with the same rule — the daemon, which
/// puts the name in the banner and on the companion's speech, and the agent, which puts it in the
/// prompt. If the two disagree, the companion answers to one name and introduces itself as
/// another, and nothing in either process can see that it has happened.
/// </para>
/// </summary>
public class PersonaTests
{
    [Fact]
    public void A_personality_declares_its_own_name()
    {
        var (_, name) = Persona.Split("---\nname: James\n---\n\n# Who you are\n\nYou are James.\n");

        Assert.Equal("James", name);
    }

    [Fact]
    public void The_front_matter_never_reaches_the_model()
    {
        // A file that opens with a name and a couple of rules is the shape of a system prompt.
        var (body, _) = Persona.Split("---\nname: James\n---\n\n# Who you are\n");

        Assert.StartsWith("# Who you are", body.TrimStart());
        Assert.DoesNotContain("name:", body);
    }

    [Fact]
    public void Quotes_around_the_name_are_not_part_of_it()
    {
        Assert.Equal("James", Persona.NameOf("---\nname: \"James\"\n---\nbody\n"));
        Assert.Equal("James", Persona.NameOf("---\nname:  'James'  \n---\nbody\n"));
    }

    [Fact]
    public void A_personality_that_declares_nothing_is_still_a_personality()
    {
        // No front matter at all, and an empty one. Both are legal: a name is a convenience,
        // not a requirement, and the companion falls back to whatever it was configured as.
        const string plain = "# Who you are\n\nYou are somebody.\n";
        Assert.Null(Persona.NameOf(plain));
        Assert.Equal(plain, Persona.Split(plain).Body);

        var (body, name) = Persona.Split("---\n---\n\n# Who you are\n");
        Assert.Null(name);
        Assert.Contains("# Who you are", body);
    }

    [Fact]
    public void An_unclosed_fence_does_not_eat_the_character()
    {
        // A missing fence is a typo. Losing the whole personality to it is a worse outcome than
        // a stray `---` in the prompt.
        const string typo = "---\nname: James\n\n# Who you are\n\nYou are James, and you noticed.\n";

        var (body, name) = Persona.Split(typo);

        Assert.Equal("James", name);
        Assert.Contains("you noticed", body);
    }
}
