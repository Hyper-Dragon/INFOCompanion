using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The companion as a persistent friend (issues #27 and #22): what it remembers about the
/// player across games, and what the core watched it move between.
/// <para>
/// The point of these tests is the promise, not the plumbing: the memory must outlive a
/// game, it must never be mistaken for a fact about the game in front of you, and a player
/// correction about themselves must survive anything the model does to the file.
/// </para>
/// </summary>
public class PersistentMemoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-memory-tests", Guid.NewGuid().ToString("N"));

    public PersistentMemoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private PersistentMemory NewMemory() => new(_directory, tokenBudget: 900);

    /// <summary>Write a file straight in, for the shapes the model produces itself.</summary>
    private void WriteMemoryFile(string markdown)
    {
        var path = Path.Combine(_directory, "notebook", "_companion.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
    }

    [Fact]
    public void A_new_memory_has_every_section_and_says_nothing()
    {
        var memory = NewMemory();
        var text = memory.Read();

        foreach (var section in PersistentMemory.Sections)
        {
            Assert.Contains("## " + section, text);
        }

        // Nothing to say means nothing in the prompt: a first evening costs no tokens, and
        // an empty memory cannot be read as "nothing is known about the player".
        Assert.Null(memory.ReadForPrompt());
    }

    [Fact]
    public void It_survives_a_game_change_because_it_is_not_a_game_file()
    {
        // The file is named for the companion, not the game, so nothing about a switch can
        // reach it. This is the structural half of "a persistent friend".
        var memory = NewMemory();
        memory.Write("# Memories\n\n## Who the player is\n\n- Enjoys being asked before anything risky.\n");

        Assert.Equal(
            Path.Combine(_directory, "notebook", "_companion.md"),
            memory.FilePath);
        Assert.DoesNotContain("advent", memory.FilePath.Replace(_directory, string.Empty));

        Assert.NotNull(memory.ReadForPrompt());
    }

    [Fact]
    public void A_correction_from_the_player_survives_a_rewrite()
    {
        // "The player outranks the companion" is a promise, so it is code rather than
        // prompt: a model that forgets to copy this section cannot lose it.
        var memory = NewMemory();
        memory.Write("# Memories\n\n## Who the player is\n\n- Plays slowly and likes it.\n");
        memory.AppendPlayerCorrection("I have played Wishbringer before, don't spoil it");

        // The model rewrites everything and drops the corrections section entirely.
        memory.Write("# Memories\n\n## Who the player is\n\n- Plays slowly.\n\n## Evenings we have had\n\n- Wishbringer, September.\n");

        var after = memory.Read();
        Assert.Contains("I have played Wishbringer before", after);
        Assert.Contains("## Player corrections", after);
    }

    [Fact]
    public void A_memory_written_as_a_preamble_still_reaches_the_prompt()
    {
        // Measured live: the model's first attempt at the memories file was a short paragraph
        // *above* the section headings, with every section left as "_(nothing yet)_". An
        // earlier version of the check looked for bullet lines and threw the whole thing away
        // — a memory the companion cannot read is not a memory.
        var memory = NewMemory();
        WriteMemoryFile(
            "# Memories\n\nPlaying Wishbringer together; the player corrects me directly and is right to.\n\n"
            + string.Join("\n\n", PersistentMemory.Sections.Select(s => $"## {s}\n\n_(nothing yet)_\n")));

        var forPrompt = memory.ReadForPrompt();

        Assert.NotNull(forPrompt);
        Assert.Contains("the player corrects me directly", forPrompt);
    }

    [Fact]
    public void An_untouched_skeleton_says_nothing_at_all()
    {
        var memory = NewMemory();
        WriteMemoryFile(PersistentMemory.Template());

        Assert.Null(memory.ReadForPrompt());
    }

    [Fact]
    public void A_memory_too_long_for_the_prompt_is_trimmed_and_says_so()
    {
        var memory = new PersistentMemory(_directory, tokenBudget: 20);
        memory.Write("# Memories\n\n## Who the player is\n\n- " + new string('x', 4000) + "\n");

        var forPrompt = memory.ReadForPrompt();

        Assert.NotNull(forPrompt);
        Assert.Contains("trimmed to fit", forPrompt);
        Assert.True(memory.OverBudget(memory.Read()));
    }

    [Fact]
    public void The_memories_reach_the_prompt_labelled_as_recollections()
    {
        var memory = NewMemory();
        memory.Write("# Memories\n\n## Who the player is\n\n- Enjoys a slow, thorough play style.\n");

        var config = new AgentConfig();
        var context = new ContextAssembler(config, "You are a companion.");
        var assembled = context.Assemble(
            notebook: "# Notebook — advent",
            history: [],
            latestEvent: "player said: hello",
            constraint: null,
            memories: memory.ReadForPrompt());

        var system = assembled.Messages[0].Content;

        Assert.Contains("What you remember about the player", system);
        Assert.Contains("a slow, thorough play style", system);
        // The honesty rule has to be in the same breath as the memories, or they read as
        // facts about the game in front of you — which is how a memory becomes a spoiler.
        Assert.Contains("not facts about the game", system);
    }

    [Fact]
    public void An_empty_turn_record_stays_out_of_the_prompt()
    {
        var context = new ContextAssembler(new AgentConfig(), "You are a companion.");
        var assembled = context.Assemble("# Notebook — advent", [], "nothing");

        Assert.DoesNotContain("What the core watched", assembled.Messages[0].Content);
    }
}

/// <summary>The core's turn record, as the companion reads it (issue #22).</summary>
public class ObservationLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-observation-tests", Guid.NewGuid().ToString("N"));

    public ObservationLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private void Write(params string[] lines)
    {
        var path = ObservationLog.PathFor(_directory, "advent");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void The_record_lives_where_the_core_writes_it()
    {
        Assert.Equal(
            Path.Combine(_directory, "observations", "advent.jsonl"),
            ObservationLog.PathFor(_directory, "advent"));
    }

    [Fact]
    public void A_turn_reads_as_the_command_and_what_came_back()
    {
        Write(
            """{"turn":1,"input":"take lamp","output":"Taken.","fingerprint":"End of road#aaaa1111"}""");

        var text = ObservationLog.Recent(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("turn 1", text);
        Assert.Contains("take lamp", text);
        Assert.Contains("Taken.", text);
        Assert.Contains("End of road#aaaa1111", text);
    }

    [Fact]
    public void The_story_speaking_by_itself_is_not_a_command()
    {
        Write(
            """{"turn":4,"input":"","output":"Time passes...","fingerprint":"End of road#aaaa1111"}""");

        var text = ObservationLog.Recent(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("(none)", text);
        Assert.Contains("Time passes", text);
    }

    [Fact]
    public void A_command_the_game_answered_with_nothing_says_so()
    {
        // Adventure does this constantly: `west` went somewhere and the game did not say
        // where. The record must not let that read as a failure.
        Write("""{"turn":5,"input":"west","output":"","fingerprint":""}""");

        var text = ObservationLog.Recent(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("the game printed nothing", text);
    }

    [Fact]
    public void The_block_never_claims_to_know_where_anything_is()
    {
        Write(
            """{"turn":1,"input":"north","output":"Well house","fingerprint":"Well house#bbbb2222"}""");

        var text = ObservationLog.Recent(_directory, "advent")!;

        Assert.Contains("not a claim about where anything is", text);
        Assert.DoesNotContain("->", text);
    }

    [Fact]
    public void Nothing_recorded_means_nothing_in_the_prompt()
    {
        Assert.Null(ObservationLog.Recent(_directory, "advent"));
        Assert.Null(ObservationLog.Recent(_directory, ""));
    }

    [Fact]
    public void A_half_written_line_is_skipped_rather_than_losing_the_record()
    {
        Write(
            """{"turn":1,"input":"north","output":"Well house","fingerprint":"Well house#bbbb2222"}""",
            """{"turn":2,"in""");

        var text = ObservationLog.Recent(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("Well house", text);
    }

    [Fact]
    public void Only_the_most_recent_are_shown_and_the_count_is_honest()
    {
        var lines = Enumerable
            .Range(1, 80)
            .Select(i => $$"""{"turn":{{i}},"input":"look","output":"Room{{i}}","fingerprint":"Room{{i}}#11112222"}""")
            .ToArray();
        Write(lines);

        var text = ObservationLog.Recent(_directory, "advent", limit: 10);

        Assert.NotNull(text);
        Assert.Contains("the 10 most recent of 80", text);
        Assert.Contains("Room80", text);
        Assert.DoesNotContain("Room1\"", text);
    }

    [Fact]
    public void A_search_brings_back_the_reach_the_caller_asks_for()
    {
        // How many turns `recall` returns and how much of each it quotes are configuration
        // (`recall_max_hits`, `recall_quote_chars`): this pins that the caller's numbers are
        // the ones used, so a deployment can widen its own view of the record without a
        // rebuild.
        Write(
            """{"turn":1,"input":"look","output":"A joystick in a cabinet, with a long row of dials beside it.","fingerprint":"Arcade#11112222"}""",
            """{"turn":2,"input":"push joystick","output":"The joystick does nothing at all, whatever you do to it.","fingerprint":"Arcade#11112222"}""");

        var text = ObservationLog.Search(_directory, "advent", "joystick", limit: 1, quoteChars: 20);

        Assert.NotNull(text);
        Assert.Contains("turn 2", text);
        Assert.DoesNotContain("turn 1", text);
        Assert.Contains("\u2026", text);
    }

    // -- which attempt a memory came from (05_memory.md §2 (`M9`–`M11`)) ---------------------------------------

    [Fact]
    public void A_recall_from_an_earlier_attempt_is_labelled_as_one()
    {
        // Measured before this existed: an observations file mixes attempts and the same `look`
        // came back labelled both turn 1 and turn 13, which the companion noticed and said out
        // loud. Knowledge survives an attempt — that is why a playbook works at all — but a belief
        // from a dead timeline quoted as though it were current is how a companion walks a route
        // its own run has already disproved.
        Write(
            """{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa","run":1,"generation":1}""",
            """{"turn":13,"input":"look","output":"A joystick in a cabinet.","fingerprint":"Arcade#bbbb","run":3,"generation":1}""");

        var text = ObservationLog.Search(_directory, "advent", "look", run: 3, generation: 1);

        Assert.NotNull(text);
        Assert.Contains("attempt 1, an earlier attempt", text);
        Assert.Contains("this is attempt 3", text);
        // The hit from the attempt in hand is quoted plainly, with no label of its own.
        Assert.DoesNotContain("turn 13 (", text);
        Assert.Contains("turn 13:", text);
    }

    [Fact]
    public void A_recall_from_before_a_reload_says_the_world_has_moved_since()
    {
        // A restore forks the world, not the attempt (05_memory.md §2 (`M9`–`M11`)). What the companion loses is
        // the right to claim where it is — so a citation from before the fork says which branch it
        // came from rather than reading as this line's history.
        Write(
            """{"turn":4,"input":"north","output":"Narrow Trail.","fingerprint":"Narrow Trail#cccc","run":2,"generation":1}""",
            """{"turn":9,"input":"south","output":"Narrow Trail.","fingerprint":"Narrow Trail#cccc","run":2,"generation":2}""");

        var text = ObservationLog.Search(_directory, "advent", "Narrow Trail", run: 2, generation: 2);

        Assert.NotNull(text);
        Assert.Contains("before the world was reloaded", text);
        Assert.Contains("generation 2", text);
    }

    [Fact]
    public void A_turn_from_before_runs_were_stamped_is_not_claimed_as_this_attempt()
    {
        // The records on disk predate both stamps. Zero is a fact about the record, and the honest
        // reading of it is "not this one" rather than assuming it came from the attempt in hand.
        Write("""{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa"}""");

        var text = ObservationLog.Search(_directory, "advent", "Forest", run: 4, generation: 1);

        Assert.NotNull(text);
        Assert.Contains("unrecorded attempt", text);
    }

    [Fact]
    public void A_turn_from_before_forks_were_stamped_says_that_rather_than_reading_as_this_branch()
    {
        // The live case the day this was written: every row already on disk carries a run (added
        // earlier) and no generation. Silence about the branch would read as a claim that the turn
        // is from where the companion is standing now, which is the one thing a fork makes unknown.
        Write("""{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa","run":2}""");

        var text = ObservationLog.Search(_directory, "advent", "Forest", run: 2, generation: 1);

        Assert.NotNull(text);
        Assert.Contains("does not say which branch", text);
        Assert.DoesNotContain("before the world was reloaded", text);
    }

    [Fact]
    public void Without_an_attempt_to_compare_against_nothing_is_claimed()
    {
        // The core has no bookkeeping for some sessions and sends zero. Saying "not this one" then
        // would be the guess this whole change exists to remove, so nothing is labelled at all.
        Write("""{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa","run":9}""");

        var text = ObservationLog.Search(_directory, "advent", "Forest");

        Assert.NotNull(text);
        Assert.DoesNotContain("earlier attempt", text);
        Assert.Contains("turn 1:", text);
    }

    [Fact]
    public void The_digest_says_which_attempt_it_is_and_labels_the_rest()
    {
        // The same rule for the block the companion reads every turn: it is the same file, and it
        // mixes the same attempts.
        Write(
            """{"turn":1,"input":"look","output":"Forest.","fingerprint":"Forest#aaaa","run":1,"generation":1}""",
            """{"turn":20,"input":"look","output":"Forest again.","fingerprint":"Forest#aaaa","run":2,"generation":1}""");

        var text = ObservationLog.Recent(_directory, "advent", run: 2, generation: 1);

        Assert.NotNull(text);
        Assert.Contains("this is attempt 2", text);
        Assert.Contains("attempt 1, an earlier attempt", text);
        Assert.DoesNotContain("turn 20 (", text);
    }

    // -- the window the model missed (04_companion.md §10 (`C56`–`C60`)) -------------------------------------------

    [Fact]
    public void The_window_reads_forward_from_the_turn_the_game_had_reached()
    {
        // What the catch-up needs is not a search: it is everything after the point the companion
        // stopped being able to see, oldest first, so the evening reads as a story rather than as a
        // pile of hits.
        Write(
            """{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa","run":3,"generation":2}""",
            """{"turn":2,"input":"north","output":"Narrow Trail.","fingerprint":"Narrow Trail#bbbb","run":3,"generation":2}""",
            """{"turn":3,"input":"south","output":"A joystick in a cabinet.","fingerprint":"Arcade#cccc","run":3,"generation":2}""");

        var text = ObservationLog.Since(_directory, "advent", afterTurn: 1, limit: 10, run: 3, generation: 2);

        Assert.NotNull(text);
        Assert.Contains("after turn 1, oldest first", text);
        Assert.Contains("Narrow Trail", text);
        Assert.Contains("A joystick", text);
        // The turn the companion was already standing on is behind the window, not in it.
        Assert.DoesNotContain("You are in a Forest", text);
        Assert.True(
            text.IndexOf("Narrow Trail", StringComparison.Ordinal)
                < text.IndexOf("A joystick", StringComparison.Ordinal));
        Assert.Contains("this is attempt 3, generation 2", text);
    }

    [Fact]
    public void A_window_with_nothing_after_that_turn_is_not_a_window()
    {
        // Null, not an empty paragraph: the caller's whole decision — is a catch-up pass worth a
        // model call — is made of this being null, and a header with no turns under it would read
        // to the model as a window it has to explain.
        Write("""{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa"}""");

        Assert.Null(ObservationLog.Since(_directory, "advent", afterTurn: 1, limit: 10));
        Assert.Null(ObservationLog.Since(_directory, "advent", afterTurn: 9, limit: 10));
        Assert.Null(ObservationLog.Since(_directory, "no-such-game", afterTurn: 0, limit: 10));
        Assert.Null(ObservationLog.Since(_directory, "advent", afterTurn: 0, limit: 0));
    }

    [Fact]
    public void A_window_does_not_reach_back_into_an_earlier_attempt()
    {
        // A restart numbers turns from one again, so a window measured by number alone would pick
        // up the whole of the attempt before this one: the companion would come back from an outage
        // believing it had just played an evening it never played.
        Write(
            """{"turn":4,"input":"west","output":"A dead end in the maze.","fingerprint":"Maze#aaaa","run":1,"generation":1}""",
            """{"turn":4,"input":"west","output":"A joystick in a cabinet.","fingerprint":"Arcade#bbbb","run":3,"generation":1}""");

        var text = ObservationLog.Since(_directory, "advent", afterTurn: 0, limit: 10, run: 3, generation: 1);

        Assert.NotNull(text);
        Assert.Contains("A joystick", text);
        Assert.DoesNotContain("the maze", text);
    }

    [Fact]
    public void A_window_says_when_it_is_only_the_first_part_of_what_was_missed()
    {
        // The cap is a real cap, and the count is honest about it: a companion told about the first
        // two of nine turns, with no sign that there were nine, would come back believing the
        // evening ended where the window does.
        Write(
            """{"turn":2,"input":"north","output":"First.","fingerprint":"F#1111"}""",
            """{"turn":3,"input":"north","output":"Second.","fingerprint":"F#1111"}""",
            """{"turn":4,"input":"north","output":"Third.","fingerprint":"F#1111"}""");

        var text = ObservationLog.Since(_directory, "advent", afterTurn: 1, limit: 2);

        Assert.NotNull(text);
        Assert.Contains("(the first 2 of 3 you missed)", text);
        Assert.Contains("First.", text);
        Assert.Contains("Second.", text);
        Assert.DoesNotContain("Third.", text);
    }
}
