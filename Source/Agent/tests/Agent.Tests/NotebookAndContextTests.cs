using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>The notebook (§10.5): the companion's memory, with code-level guarantees.</summary>
public class NotebookTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-agent-tests", Guid.NewGuid().ToString("N"));

    public NotebookTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void A_new_notebook_has_every_section()
    {
        var store = new NotebookStore(_directory, "advent");
        var text = store.Read();

        foreach (var section in NotebookStore.RequiredSections)
        {
            Assert.Contains("## " + section, text);
        }
    }

    [Fact]
    public void Writing_repairs_missing_sections()
    {
        var store = new NotebookStore(_directory, "advent");

        // A careless rewrite by the model drops most of the structure.
        store.Write("# Notebook — advent\n\n## Key facts\n- the troll wants treasure\n");

        var text = store.Read();
        Assert.Contains("the troll wants treasure", text);
        foreach (var section in NotebookStore.RequiredSections)
        {
            Assert.Contains("## " + section, text);
        }
    }

    [Fact]
    public void A_truncated_rewrite_never_replaces_a_complete_notebook()
    {
        var store = new NotebookStore(_directory, "moonmist");
        store.AppendPlayerCorrection("the gate opened for blue");
        var kept = store.Read();

        // What a reply cut off inside the block looks like: the opening tag, and none of
        // the sections the model had not reached yet.
        var fragment = "# Notebook — moonmist\n\n## Current goals and plan\n- ask Vivien about the letter";
        Assert.False(NotebookStore.HasEverySection(fragment));
        Assert.False(store.TryWriteRewrite(fragment, truncated: true));

        // The whole notebook is still there, correction included (issue #18).
        Assert.Equal(kept, store.Read());

        // A complete rewrite is written as usual. The new content goes into a section the
        // template already wrote: a rewrite that repeated a heading would have the duplicate
        // dropped (its own test below), so the fixture must not accidentally lean on that.
        var complete = NotebookStore.Template("moonmist")
            .Replace("_(nothing yet)_", "- the adder was in the desk");
        Assert.True(store.TryWriteRewrite(complete, truncated: false));
        Assert.Contains("the adder was in the desk", store.Read());
    }

    [Fact]
    public void Player_corrections_are_appended_and_kept()
    {
        var store = new NotebookStore(_directory, "advent");
        store.AppendPlayerCorrection("the password was friend, not friendy");

        var text = store.Read();
        Assert.Contains("## Player corrections", text);
        Assert.Contains("the password was friend, not friendy", text);

        // And they survive a later rewrite.
        store.Write("## Key facts\n- something else\n");
        Assert.Contains("the password was friend, not friendy", store.Read());
    }

    [Fact]
    public void A_correction_that_drifted_by_a_word_is_still_one_correction()
    {
        // Measured live on 2026-09-16: the same correction appeared three and four times over,
        // several cut off mid-sentence ("…remembered from playing years ago: go to the"). The
        // harness captured each one once; what multiplied them was copies inside rewrites, which
        // an exact-match dedupe cannot recognise (05_memory.md (`M1`–`M39`)).
        var store = new NotebookStore(_directory, "wishbringer");
        store.AppendPlayerCorrection("go to the graveyard, show the gravedigger the envelope");

        store.Write(
            "# Notebook — wishbringer\n\n## Player corrections\n"
            + "- (player, 2026-09-16 22:31Z) go to the graveyard, show the\n"
            + "- (player) go to the graveyard, show the gravedigger the envelope.\n"
            + "- the coal is in the cellar\n");

        var text = store.Read();
        var occurrences = text.Split("the gravedigger the envelope").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("go to the graveyard, show the gravedigger the envelope", text);
        // The harness's own copy — the one with the timestamp — is the survivor.
        Assert.Contains("(player, ", text);
        // A plain bullet the model wrote under that heading is content, not a duplicate.
        Assert.Contains("the coal is in the cellar", text);
    }

    [Fact]
    public void A_section_name_with_heading_marks_still_finds_its_section()
    {
        // Measured live on 2026-09-16: a rewrite arrived with a section called "## Wish-props",
        // heading mark and all, and the mark travelled into the update event and the file. The
        // model meant the section called "Wish-props" (05_memory.md (`M1`–`M39`)).
        var store = new NotebookStore(_directory, "wishbringer");
        store.Write(NotebookStore.Template("wishbringer").Replace("## Map notes", "## Wish-props\n\n_(nothing yet)_\n\n## Map notes"));

        Assert.Equal("Wish-props", NotebookStore.NormaliseHeading("  ##  Wish-props  "));
        Assert.True(store.UpdateSection("## Wish-props", "- the conch asks for a prisoner"));
        Assert.Contains("- the conch asks for a prisoner", store.Section("Wish-props")!);
        // The heading itself was not duplicated into the file.
        Assert.DoesNotContain("## ## Wish-props", store.Read());
    }

    [Fact]
    public void Empty_required_sections_are_reported_rather_than_guessed_at()
    {
        // Measured with the real 2026-09-16 notebook: "Current goals and plan" and "Discarded"
        // sat at "_(nothing yet)_" at the close of an evening whose content the model had written
        // into sections of its own. That is measured here and never parsed (05_memory.md (`M1`–`M39`)).
        var template = NotebookStore.Template("advent");
        Assert.Equal(NotebookStore.RequiredSections.Length, NotebookStore.EmptyRequiredSections(template).Count);

        var filled = template.Replace("## Key facts\n\n_(nothing yet)_", "## Key facts\n\n- the grate is locked");
        var empty = NotebookStore.EmptyRequiredSections(filled);
        Assert.DoesNotContain("Key facts", empty);
        Assert.Contains("Current goals and plan", empty);

        // But the warning built on it is scoped, because two of the nine are empty for a reason:
        // nothing has been discarded, and the player may not have corrected anybody yet. Measured
        // 2026-10-01 on a fresh game — `sections: [Player corrections, Discarded]` at a rewrite that
        // had lost nothing, which is a line a reader learns to skip.
        Assert.DoesNotContain("Player corrections", NotebookStore.EmptySectionsWorthReporting(template));
        Assert.DoesNotContain("Discarded", NotebookStore.EmptySectionsWorthReporting(template));
        Assert.Contains("Current goals and plan", NotebookStore.EmptySectionsWorthReporting(template));
        Assert.Contains("Current goals and plan", NotebookStore.EmptySectionsWorthReporting(filled));
    }

    [Theory]
    [InlineData("actually the password was friend")]
    [InlineData("remember, the troll wants the sceptre")]
    [InlineData("no, you're wrong about the grate")]
    [InlineData("I told you the lamp is in the attic")]
    public void Corrections_are_recognised(string line)
    {
        Assert.True(NotebookStore.LooksLikeCorrection(line));
    }

    [Theory]
    [InlineData("let's go north")]
    [InlineData("what did the troll say?")]
    [InlineData("nice one")]
    public void Ordinary_lines_are_not_corrections(string line)
    {
        Assert.False(NotebookStore.LooksLikeCorrection(line));
    }

    [Fact]
    public void The_notebook_is_bounded_by_its_budget()
    {
        var store = new NotebookStore(_directory, "advent", tokenBudget: 1000);
        Assert.False(store.OverBudget(store.Read()));

        store.Write("## Key facts\n" + new string('x', 5000));
        Assert.True(store.OverBudget(store.Read()));
    }

    [Fact]
    public void Notebooks_are_per_game()
    {
        var advent = new NotebookStore(_directory, "advent");
        var witness = new NotebookStore(_directory, "witness");
        advent.Write("## Key facts\n- advent only\n");

        Assert.Contains("advent only", advent.Read());
        Assert.DoesNotContain("advent only", witness.Read());
    }
}

/// <summary>Context assembly (§10.2).</summary>
public class ContextAssemblerTests
{
    private static ContextAssembler NewAssembler(int turns = 40, int budget = 24000) =>
        new(new AgentConfig { TranscriptTurns = turns, TranscriptTokenBudget = budget }, "PERSONA");

    [Fact]
    public void The_message_order_is_system_notebook_transcript_latest()
    {
        var transcript = new Transcript();
        transcript.AddPlayerLine("can we go north");
        transcript.AddGameTurn("north", "You are in a forest.", 1, byCompanion: true);
        transcript.AddCompanion("ok, going north");

        var assembled = NewAssembler().Assemble("## Key facts\n- a lantern", transcript.Items, "player said: hello");

        Assert.Equal("system", assembled.Messages[0].Role);
        Assert.Contains("PERSONA", assembled.Messages[0].Content);
        Assert.Contains("a lantern", assembled.Messages[0].Content);
        Assert.Contains("## How you answer", assembled.Messages[0].Content);
        Assert.Contains("`type`", assembled.Messages[0].Content);

        Assert.Equal("user", assembled.Messages[1].Role);
        Assert.Contains("can we go north", assembled.Messages[1].Content);

        Assert.Equal("user", assembled.Messages[2].Role);
        Assert.Contains("You are in a forest.", assembled.Messages[2].Content);

        // The companion's own lines are the assistant's turns.
        Assert.Equal("assistant", assembled.Messages[3].Role);
        Assert.Contains("ok, going north", assembled.Messages[3].Content);

        Assert.Equal("player said: hello", assembled.Messages[^1].Content);
        Assert.DoesNotContain("outside your view", assembled.Messages[^1].Content);
    }

    [Fact]
    public void The_notebook_block_names_the_canonical_sections_that_are_still_empty()
    {
        // The nudge was generic until 2026-09-21 — "keep the sections it came with current" — and
        // three of the nine were still at "(nothing yet)" at the close of an eighteen-hour evening
        // whose content was excellent and sat in sections of the model's own. The harness has
        // measured which are empty since it started measuring them; naming them is the difference
        // between a hint and a nudge, and it still demands nothing (05_memory.md (`M1`–`M39`)).
        var template = NotebookStore.Template("advent");
        var system = NewAssembler().Assemble(template, new Transcript().Items, "next").Messages[0].Content;

        Assert.Contains("still empty", system);
        Assert.Contains("Key facts", system);
        Assert.Contains("Map notes", system);
    }

    [Fact]
    public void A_notebook_with_nothing_empty_under_its_canonical_headings_is_not_nagged_about()
    {
        var filled = NotebookStore.Template("advent").Replace("_(nothing yet)_", "- something true");
        var system = NewAssembler().Assemble(filled, new Transcript().Items, "next").Messages[0].Content;

        Assert.DoesNotContain("still empty", system);
    }

    [Fact]
    public void The_contract_says_that_answering_the_game_is_acting()
    {
        // Measured live: Border Zone opens with "Which chapter would you like to play: 1, 2,
        // 3, or (R)estore?" and the answer *is* the line — type `1` and the game starts. A
        // contract that only allows compass directions and verb-noun pairs tells the companion
        // it may not do the one thing the game is asking for, and the driver is the companion,
        // not the core.
        var assembled = NewAssembler().Assemble("", new Transcript().Items, "next");
        var system = assembled.Messages[0].Content;

        Assert.Contains("the answer *is* the line", system);
        // While the rule it must not weaken stays put: inventing verbs is the failure this
        // gate exists for.
        Assert.Contains("Never invent a verb", system);
    }

    [Fact]
    public void A_game_turn_shows_what_was_asked_and_what_came_back()
    {
        var transcript = new Transcript();
        transcript.AddGameTurn("open grate", "The grate is rusted shut.", 4, byCompanion: false);

        var assembled = NewAssembler().Assemble("", transcript.Items, "next");

        Assert.Contains("open grate", assembled.Messages[1].Content);
        Assert.Contains("The grate is rusted shut.", assembled.Messages[1].Content);
    }

    [Fact]
    public void A_game_turn_the_companion_typed_is_attributed_to_it()
    {
        // The transcript is cleared on a fork, and the companion reads its own typed lines back
        // from it. Rendered as "the game was asked", its own `restore` looked like somebody
        // else's command: in the 2026-10-05 UAT it told the player "it wasn't me" about a restore
        // it had made itself, and re-restored chasing a world it thought had been loaded by
        // somebody else. A line the companion typed is named as its own.
        var transcript = new Transcript();
        transcript.AddGameTurn("restore", "Okay, done.", 82, byCompanion: true);
        transcript.AddGameTurn("wait", "Time passes.", 83, byCompanion: false);

        var assembled = NewAssembler().Assemble("", transcript.Items, "next");

        Assert.Contains("you typed: restore", assembled.Messages[1].Content);
        Assert.Contains("the game was asked: wait", assembled.Messages[2].Content);
    }

    [Fact]
    public void Truncation_drops_the_oldest_turns_first_and_never_the_notebook()
    {
        var transcript = new Transcript();
        for (var i = 1; i <= 30; i++)
        {
            transcript.AddGameTurn($"command {i}", $"response {i}", i, byCompanion: false);
        }

        var assembled = NewAssembler(turns: 40, budget: 200).Assemble("## Key facts\n- the password is friend", transcript.Items, "latest");

        Assert.Contains("the password is friend", assembled.Messages[0].Content);
        Assert.True(assembled.ItemsDropped > 0, "oldest turns should be dropped");
        Assert.Contains("latest", assembled.Messages[^1].Content);

        // Whatever survived is verbatim, and it is the most recent.
        var joined = string.Join("\n", assembled.Messages.Select(m => m.Content));
        Assert.DoesNotContain("response 1\n", joined);
        Assert.Contains("response 30", joined);
    }

    [Fact]
    public void The_recent_floor_widens_the_window_beyond_the_turn_cap()
    {
        var transcript = new Transcript();
        for (var i = 1; i <= 10; i++)
        {
            transcript.AddGameTurn($"command {i}", $"response {i}", i, byCompanion: false);
        }

        var assembled = NewAssembler(turns: 3).Assemble("", transcript.Items, "latest");

        // The floor keeps the newest five items whatever the turn cap says (05_memory.md §2 (`M9`–`M11`)); the five it does not need are given back, oldest first.
        Assert.Equal(5, assembled.ItemsIncluded);
        Assert.Equal(5, assembled.ItemsDropped);
        var joined = string.Join("\n", assembled.Messages.Select(m => m.Content));
        Assert.Contains("response 10", joined);
        Assert.DoesNotContain("response 5\n", joined);
    }

    [Fact]
    public void Every_player_line_is_kept_even_when_the_budget_cannot_hold_the_floor()
    {
        var transcript = new Transcript();
        transcript.AddPlayerLine("actually the coin is no good for the arcade");
        for (var i = 1; i <= 40; i++)
        {
            transcript.AddGameTurn($"command {i}", new string('x', 400) + $" {i}", i, byCompanion: false);
        }

        var assembled = NewAssembler(turns: 40, budget: 100).Assemble("", transcript.Items, "latest");

        var joined = string.Join("\n", assembled.Messages.Select(m => m.Content));
        Assert.Contains("the coin is no good for the arcade", joined);
        Assert.Contains("command 40", joined);
        Assert.DoesNotContain("command 1\n", joined);
        Assert.True(assembled.ItemsDropped > 0, "the oldest turns should be given back");
        Assert.True(assembled.OverBudget, "the floor alone is over budget and that is reported, not hidden");
    }

    [Fact]
    public void When_the_view_is_clipped_the_companion_is_told()
    {
        var transcript = new Transcript();
        for (var i = 1; i <= 30; i++)
        {
            transcript.AddGameTurn($"command {i}", $"response {i}", i, byCompanion: false);
        }

        var assembled = NewAssembler(budget: 200).Assemble("", transcript.Items, "latest");

        Assert.True(assembled.ItemsDropped > 0);
        Assert.Contains("outside your view", assembled.Messages[^1].Content);
    }

    [Fact]
    public void The_fixed_prompt_has_its_own_budget_and_is_reported_not_trimmed()
    {
        var assembler = new ContextAssembler(new AgentConfig { FixedTokenBudget = 100 }, "PERSONA");
        var assembled = assembler.Assemble(new string('n', 4000), [], "latest");

        Assert.True(assembled.OverFixedBudget);
        Assert.True(assembled.FixedTokens > 100);
        Assert.Contains(new string('n', 4000), assembled.Messages[0].Content);
    }

    [Fact]
    public void Token_estimates_grow_with_the_context()
    {
        var transcript = new Transcript();
        transcript.AddGameTurn("look", "You are in a forest.", 1, byCompanion: false);

        var small = NewAssembler().Assemble("", transcript.Items, "hello");
        var large = NewAssembler().Assemble("", transcript.Items, new string('x', 4000));

        Assert.True(large.EstimatedTokens > small.EstimatedTokens);
        Assert.True(small.EstimatedTokens > 0);
    }
}

/// <summary>The sectioned notebook and the record search (05_memory.md (`M1`–`M39`)).</summary>
public class NotebookSectionAndRecallTests
{
    [Fact]
    public void A_section_edit_leaves_every_other_section_alone()
    {
        var store = new NotebookStore(
            Path.Combine(Path.GetTempPath(), "infocompanion-section-tests", Guid.NewGuid().ToString("N")),
            "advent");
        store.Write("## Key facts\n\n- a lantern\n\n## Open questions\n\n- the grate\n");

        Assert.Equal("- the grate", store.Section("open questions"));
        Assert.True(store.UpdateSection("Open questions", "- the grate is rusted shut\n- where is the key?"));

        var text = store.Read().Replace("\r\n", "\n");
        Assert.Contains("a lantern", text);
        Assert.Contains("- the grate is rusted shut\n", text);
        Assert.Contains("where is the key?", text);
        Assert.DoesNotContain("\n- the grate\n", text);
        Assert.Null(store.Section("no such heading anywhere"));
    }

    [Fact]
    public void A_duplicated_section_is_dropped_to_the_first_copy()
    {
        var store = new NotebookStore(
            Path.Combine(Path.GetTempPath(), "infocompanion-dedupe-tests", Guid.NewGuid().ToString("N")),
            "advent");
        store.Write("## Key facts\n\n- first copy\n\n## Map notes\n\n- the good one\n\n## Map notes\n\n- the stale one\n");

        var text = store.Read().Replace("\r\n", "\n");
        Assert.Equal(1, text.Split("## Map notes").Length - 1);
        Assert.Contains("the good one", text);
        Assert.DoesNotContain("the stale one", text);
    }

    [Fact]
    public void Recall_finds_a_turn_by_a_word_the_screen_said()
    {
        var state = Path.Combine(Path.GetTempPath(), "infocompanion-recall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(state, "observations"));
        File.WriteAllLines(
            Path.Combine(state, "observations", "advent.jsonl"),
            [
                """{"turn":1,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa"}""",
                """{"turn":2,"input":"push joystick","output":"Nothing exciting happens.","fingerprint":"Video Arcade#bbbb"}""",
            ]);

        var found = ObservationLog.Search(state, "advent", "joystick");
        Assert.NotNull(found);
        Assert.Contains("push joystick", found);
        Assert.Contains("Nothing exciting happens", found);
        Assert.DoesNotContain("Forest", found);

        Assert.Null(ObservationLog.Search(state, "advent", "unobtainium"));
    }
}
