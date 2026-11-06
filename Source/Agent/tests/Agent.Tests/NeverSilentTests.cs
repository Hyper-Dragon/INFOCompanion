using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// A turn never ends with nothing (04_companion.md §5 (`C29`–`C41`)).
/// <para>
/// The evening of 2026-09-29 had a 2m45s silence in it, during which the player typed *"are you
/// lost?"* and a bare `?`. Four `?`s in that window cost two to five model calls each and not one of
/// them was answered, because the line reached the model as *"carry on"* and the model had nothing to
/// say. The prod is the player asking whether anybody is there, and the answer is three facts the
/// harness already holds.
/// </para>
/// </summary>
public class NeverSilentTests
{
    private const string Welcome =
        """{"v":1,"type":"welcome","seq":1,"ts":"now","payload":{"game_title":"Wishbringer","game_stem":"wishbringer","turn_count":0,"transcript_tail":[],"agent_name":"Quill","run":1,"generation":1,"resumed":false,"resumed_from":null}}""";

    private static string Prod =>
        """{"v":1,"type":"chat","seq":900,"ts":"now","payload":{"speaker":"player","text":"?"}}""";

    private static string PlayerLine(string text) =>
        "{\"v\":1,\"type\":\"chat\",\"seq\":901,\"ts\":\"now\",\"payload\":{"
        + "\"speaker\":\"player\",\"text\":\"" + text + "\"}}";

    private static string StatusTurn(ulong n, string status) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{"
        + "\"input\":\"look\",\"output\":\"You are in a room.\",\"elapsed_ms\":4,\"turn\":" + n
        + ",\"awaiting_command\":true,\"machinery\":false,"
        + status
        + "}}";

    [Fact]
    public async Task A_prod_is_answered_without_waking_the_model()
    {
        // The whole requirement in one pair of assertions: the player gets an answer, and it costs
        // nothing. A model call is the thing that cannot be guaranteed — it can be slow, it can be
        // cut off, and it can answer with silence, which is what happened four times.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(StatusTurn(7, "\"location\":180,\"score\":0,\"moves\":19"));
        seat.Say(Prod);

        var said = await harness.UntilAsync(text => text.Contains("room 180"));

        Assert.Contains("Here.", said);
        Assert.Contains("room 180", said);
        Assert.Equal(0, llm.Requests.Count);
    }

    [Fact]
    public async Task The_prod_answer_says_when_it_cannot_say_where_it_is()
    {
        // A story that keeps its own status line reports no room, and the answer says that rather
        // than inventing one — the same rule as everything else the harness says out loud.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(StatusTurn(7, "\"location\":null,\"score\":null,\"moves\":null"));
        seat.Say(Prod);

        var said = await harness.UntilAsync(text => text.Contains("has not said which room"));

        Assert.Contains("Here.", said);
        Assert.Contains("has not said which room", said);
        Assert.Equal(0, llm.Requests.Count);
    }

    [Fact]
    public async Task A_pass_that_says_nothing_and_moves_nothing_does_not_leave_the_room_silent()
    {
        // The other half of the same evening (`C16.1`): there, the harness was asked and answered it
        // itself; here nobody was asked, the model was woken, and it answered with nothing at all.
        // The room gets one plain line from the harness rather than going quiet.
        // An empty reply: no speech, no tool call, nothing. The default `Reply()` carries a line, so
        // this has to say so explicitly — a scripted reply that "says something" would test nothing.
        var llm = new ScriptedLlm().Reply(string.Empty);
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(StatusTurn(7, "\"location\":180,\"score\":0,\"moves\":19"));

        // A brief and not a remark: `open the gate` is a line that names a thing to do, so the harness
        // types it into the game and no model pass happens at all — which is why the line here is the
        // one the player actually typed into the silence.
        seat.Say(PlayerLine("are you lost?"));

        var said = await harness.UntilAsync(text => text.Contains("Nothing came of that"));

        Assert.Contains("Nothing came of that, and nothing moved.", said);
        Assert.NotEqual(0, llm.Requests.Count);
    }

    [Fact]
    public async Task A_greeting_is_a_line_and_not_a_notebook()
    {
        // Measured 2026-10-01, on a real sitting: the player sat down, and the join pass spent four
        // calls — three rounds of retrieval and then a notebook rewrite that wrote 747 tokens and left
        // two required sections empty — and greeted nobody. The first thing the player read was the
        // harness's "Nothing came of that, and nothing moved." The notebook was genuinely stale (the
        // sitting had just been restored into a new generation), which is the point: it is another
        // pass's work, and a greeting that does it has done nothing for the person waiting.
        // The claim here is the surface, because the surface is what can be enforced rather than asked
        // for: the greeting can look and it can speak, and it cannot write.
        var llm = new ScriptedLlm().Reply("Evening, Peter. We're at the cliff edge, and I have my bearings.");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(
            """{"v":1,"type":"system","seq":20,"ts":"now","payload":{"event":"player_connected","detail":"the player is at the keyboard"}}""");

        var said = await harness.UntilAsync(text => text.Contains("Evening, Peter."));

        Assert.Contains("Evening, Peter.", said);
        Assert.NotEmpty(llm.Options);

        var tools = llm.Options[0]!.Tools!.Select(tool => tool.Name).ToList();
        Assert.DoesNotContain(CompanionTools.WriteNotes, tools);
        Assert.DoesNotContain(CompanionTools.UpdateNotes, tools);
        Assert.DoesNotContain(CompanionTools.WriteMemories, tools);
        Assert.DoesNotContain(CompanionTools.MetaNote, tools);
        Assert.DoesNotContain(CompanionTools.Type, tools);
        Assert.DoesNotContain(CompanionTools.PressKey, tools);

        // What is left is what a greeting is for: what the companion knows, and the one thing it can
        // say to somebody else.
        Assert.Contains(CompanionTools.Recall, tools);
        Assert.Contains(CompanionTools.Map, tools);
        Assert.Contains(CompanionTools.Ask, tools);

        // And the pass is told what it is, because a model that finds a tool missing writes a note
        // about the harness rather than saying hello.
        Assert.Contains("Someone has just sat down", llm.Requests[0][^1].Content);
    }

    [Fact]
    public async Task A_pass_that_asks_the_player_gets_no_plain_line()
    {
        // `C16.1`'s third way a pass owes nothing: a question is delivery. Measured 2026-10-02 — of
        // the five plain lines one sitting wrote, **three arrived under a live `ask`**, with the
        // harness waiting for an answer to the question it had just denied. The guard asked a
        // different question about the same reply: `NothingCameOfTheRepair` has counted `ask` as a
        // call of consequence since it was written, and the guard looked only at speech and at a
        // command, so `ask` fell between the two.
        //
        // Every reply is an ask on purpose. The guard fires per pass, and the harness runs passes a
        // test did not ask for — two player lines produced six here — so scripting one ask and
        // leaving the rest to the queue's default would be testing the queue rather than the rule:
        // the empty default was itself producing the plain line, for passes that had nothing to do
        // with the claim.
        var llm = new ScriptedLlm();
        for (var i = 0; i < 8; i++)
        {
            llm.Reply(
                string.Empty,
                calls: [ScriptedLlm.Tool(CompanionTools.Ask, "question", "North or down?")]);
        }

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(StatusTurn(7, "\"location\":180,\"score\":0,\"moves\":19"));

        // Both lines are questions, because a line the tables read as a command is typed into the
        // game and wakes nobody — and a wake-up is what this test needs.
        seat.Say(PlayerLine("which way now?"));
        seat.Say(PlayerLine("so what now?"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 4);

        Assert.True(llm.Requests.Count >= 4, $"the passes ran: {llm.Requests.Count}");
        Assert.True(
            !harness.Output.Contains("Nothing came of that"),
            $"passes: {llm.Requests.Count}{Environment.NewLine}{harness.Output}");
    }

    [Fact]
    public async Task A_pass_that_owes_a_line_is_not_offered_the_notebook_rewrite()
    {
        // `C70`. Measured 2026-10-02: two of a sitting's five plain lines were a pass that rewrote the
        // notebook and said nothing — one a repair, one an unprompted pass — so the only thing the
        // player read was the harness's own sentence. The greeting already had this rule (`C16.2`,
        // measured 2026-10-01); it is the same rule, said once instead of once per pass.
        var llm = new ScriptedLlm().Reply("Right, listening.");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(StatusTurn(7, "\"location\":180,\"score\":0,\"moves\":19"));
        seat.Say(PlayerLine("are you lost?"));

        await harness.UntilAsync(text => text.Contains("Right, listening."));

        // Every pass this scenario produces owes the player a line — there is no maintenance pass in
        // six turns and no greeting, because nobody connected — so none of them may be offered a
        // whole-file write.
        Assert.NotEmpty(llm.Options);
        foreach (var options in llm.Options)
        {
            var names = options!.Tools!.Select(tool => tool.Name).ToList();
            Assert.DoesNotContain(CompanionTools.WriteNotes, names);
            Assert.DoesNotContain(CompanionTools.WriteMemories, names);
        }

        // What is *not* taken away, because the rule is about the whole-file write rather than about
        // the notebook: a section write is cheap, it does not invalidate the whole prefix, and keeping
        // the notes current is ordinary play. And the move tools stay, because the line and the step
        // are the job.
        var first = llm.Options[0]!.Tools!.Select(tool => tool.Name).ToList();
        Assert.Contains(CompanionTools.UpdateNotes, first);
        Assert.Contains(CompanionTools.Type, first);
    }
}
