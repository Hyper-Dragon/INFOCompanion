using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// Where a reply stands: the run book it is read from, the two things read off a turn's own text,
/// and the per-turn block that carries them into every reply (05_memory.md §2 (`M9.2`)).
/// <para>
/// The block exists because of a measured evening. On 2026-09-29 a companion with a working map and
/// sixty-nine turns of record could not say which room it was in: nothing in the prompt stated it,
/// so the fact was available by asking and was never asked for. These tests are about the fact being
/// in front of it rather than within reach of it.
/// </para>
/// </summary>
public class SituationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-situation-tests", Guid.NewGuid().ToString("N"));

    public SituationTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private void Book(string json)
    {
        Directory.CreateDirectory(Path.Combine(_state, "runs"));
        File.WriteAllText(Path.Combine(_state, "runs", "advent.json"), json);
    }

    private void Record(params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        File.WriteAllLines(ObservationLog.PathFor(_state, "advent"), lines);
    }

    // -- the run book (02_game_boundary.md §6 (`G27`–`G34`)) -------------------------------------

    [Fact]
    public void A_game_nobody_has_played_has_no_attempt_to_name()
    {
        Assert.Null(RunBook.Current(_state, "advent"));
    }

    [Fact]
    public void The_book_names_the_attempt_and_the_fork()
    {
        Book("""{"current":{"number":2,"started":"2026-09-29T00:41:00Z","generation":3}}""");

        var attempt = RunBook.Current(_state, "advent");

        Assert.NotNull(attempt);
        Assert.Equal(2, attempt!.Value.Number);
        Assert.Equal(3, attempt.Value.Generation);
    }

    [Fact]
    public void An_attempt_that_has_ended_says_nothing_rather_than_naming_the_last_one()
    {
        // The core takes `current` off the book when the player switches game, and keeps the number
        // in the history. A companion told it is on attempt 2 of a game it has finished will reason
        // about a world that is not the one on the screen.
        Book("""{"current":null,"history":[{"number":1},{"number":2}]}""");

        Assert.Null(RunBook.Current(_state, "advent"));
    }

    [Fact]
    public void A_half_written_book_says_nothing()
    {
        Book("""{"current":{"number":2,"gen""");

        Assert.Null(RunBook.Current(_state, "advent"));
    }

    [Fact]
    public void A_book_that_does_not_record_the_fork_does_not_invent_one()
    {
        // Zero is the record's own word for *this field predates me*. Defaulting it to the first
        // generation would claim every row written before the fork was stamped came from fork one.
        Book("""{"current":{"number":1,"started":"2026-09-29T00:41:00Z"}}""");

        var attempt = RunBook.Current(_state, "advent");

        Assert.Equal(1, attempt!.Value.Number);
        Assert.Equal(0, attempt.Value.Generation);
    }

    // -- what the story printed, and what the player typed (02_game_boundary.md §8 (`G45`)) -------

    [Fact]
    public void The_room_is_named_by_the_first_line_that_is_a_name()
    {
        var output = "The door closes.\n\nOutside Cottage\nA path leads north.";

        Assert.Equal("Outside Cottage", TurnReading.RoomName(output));
    }

    [Fact]
    public void A_prompt_or_a_remark_is_not_a_name()
    {
        // All three of these were real labels on Wishbringer's record, from a rule that took the
        // shortest line. A map labelled by a machine that cannot tell a sign from a remark is worse
        // than an unlabelled one, because it reads as a fact.
        var output = "What next?\nYou can't go that way.\npostcards.";

        Assert.Equal(string.Empty, TurnReading.RoomName(output));
    }

    [Fact]
    public void A_turn_with_no_name_answers_with_nothing()
    {
        Assert.Equal(string.Empty, TurnReading.RoomName("Taken.\n\nOK."));
        Assert.Equal(string.Empty, TurnReading.RoomName(string.Empty));
    }

    [Fact]
    public void The_second_half_of_a_wrapped_aside_is_not_a_name_either()
    {
        // Measured 2026-10-01, Wishbringer: the world document held a place called `as you go up.)` —
        // the tail of an aside whose first line was too long for the length rule, so the fragment that
        // was left passed every test a name has to pass. A closing bracket is the same evidence as an
        // opening one.
        var output = "(The path twists and turns and climbs a long way up the face of the cliff,\n"
            + "as you go up.)\n\nSteep Trail\n";

        Assert.Equal("Steep Trail", TurnReading.RoomName(output));

        // And the shape on its own, with nothing else on the screen, is no name at all rather than a
        // fragment of one.
        Assert.Equal(string.Empty, TurnReading.RoomName("as you go up.)\n"));
        Assert.Equal(string.Empty, TurnReading.RoomName("and it ends here]\n"));
    }

    [Theory]
    [InlineData("examine drawers", "drawers")]
    [InlineData("x drawers", "drawers")]
    [InlineData("X THE CABINET", "THE CABINET")]
    [InlineData("look at the desk", "the desk")]
    [InlineData("search under the bed", "under the bed")]
    [InlineData("feel the wall", "the wall")]
    [InlineData("examine", "")]
    [InlineData("look", "")]
    [InlineData("look at", "")]
    [InlineData("look around", "")]
    [InlineData("go north", "")]
    [InlineData("take lamp", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void What_was_examined_comes_out_of_the_command(string? input, string expected)
    {
        Assert.Equal(expected, TurnReading.LastExamined(input));
    }

    // -- the citation (05_memory.md §2 (`M9`)) --------------------------------------------------

    [Fact]
    public void A_recalled_turn_says_which_room_it_was_read_at()
    {
        // The same direction, two rooms: "east" out of the gallery and "east" out of the corridor
        // are different moves, and a record that prints only the command makes them look alike.
        Record(
            """{"turn":645,"input":"east","output":"Office.","fingerprint":"Office#1","location":222}""",
            """{"turn":12,"input":"east","output":"Bedroom.","fingerprint":"Bedroom#2"}""");

        var matches = ObservationLog.Matches(_state, "advent", "east");

        Assert.NotNull(matches);
        Assert.Equal(222L, matches![0].Location);
        // A row written before the room was recorded says nothing rather than "room 0".
        Assert.Null(matches[1].Location);

        var text = ObservationLog.Search(_state, "advent", "east");

        Assert.NotNull(text);
        Assert.Contains("in room 222", text!);
        Assert.DoesNotContain("in room 0", text!);
    }

    // -- the block ------------------------------------------------------------------------------

    [Fact]
    public void The_situation_rides_in_the_turn_rather_than_the_standing_prompt()
    {
        var assembler = new ContextAssembler(new AgentConfig(), "PERSONA");

        var assembled = assembler.Assemble(
            "# Notebook",
            [],
            "player said: east",
            situation: "You are in room 222, which the story printed as \"the first-floor corridor\".");

        var turn = assembled.Messages[^1].Content;
        Assert.Contains("## Where this reply stands", turn);
        Assert.Contains("room 222", turn);

        // It changes with every move, and the system message has to stay byte-identical or every
        // token cached behind it is repainted on every call.
        Assert.DoesNotContain("Where this reply stands", assembled.Messages[0].Content);
    }

    [Fact]
    public void No_situation_means_no_block_rather_than_an_empty_heading()
    {
        var assembler = new ContextAssembler(new AgentConfig(), "PERSONA");

        var assembled = assembler.Assemble("# Notebook", [], "player said: east");

        Assert.DoesNotContain("Where this reply stands", assembled.Messages[^1].Content);
    }

    [Fact]
    public void The_same_state_asks_for_the_same_tokens()
    {
        // A block that renders differently for the same facts would repaint the transcript's prefix
        // on every turn and cost more than it saves.
        var assembler = new ContextAssembler(new AgentConfig(), "PERSONA");
        const string situation = "You are in room 54; the story has not printed a name for it.";

        var first = assembler.Assemble("# Notebook", [], "player said: east", situation: situation);
        var second = assembler.Assemble("# Notebook", [], "player said: east", situation: situation);

        Assert.Equal(first.Messages[^1].Content, second.Messages[^1].Content);
    }
}
