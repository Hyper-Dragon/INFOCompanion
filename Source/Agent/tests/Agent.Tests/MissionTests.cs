using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// Missions — a delegated intent with a budget (§iteration 01 §3). This replaced the
/// step cap, which could not tell `look` from `burn candle` and so stopped the harmless
/// wandering while leaving the dangerous actions to the risk rule.
/// </summary>
public class MissionTests
{
    [Fact]
    public void A_mission_remembers_its_goal_and_budget()
    {
        var mission = new Mission("scout all open locations and report back", budget: 8, source: "delegated");

        Assert.Equal("scout all open locations and report back", mission.Goal);
        Assert.Equal(8, mission.Budget);
        Assert.Equal("delegated", mission.StartedBy);
        Assert.True(mission.Active);
        Assert.Equal(0, mission.StepsTaken);
        Assert.Equal(8, mission.StepsRemaining);
        Assert.False(mission.BudgetExhausted);
    }

    [Fact]
    public void Steps_are_counted_and_the_budget_is_a_hard_stop()
    {
        var mission = new Mission("map it out", budget: 3, source: "delegated");

        mission.NoteStepTaken("east");
        mission.NoteStepTaken("north");
        Assert.False(mission.BudgetExhausted);
        Assert.Equal(1, mission.StepsRemaining);

        mission.NoteStepTaken("west");
        Assert.True(mission.BudgetExhausted);
        Assert.Equal(0, mission.StepsRemaining);

        // Over-running does not go negative, and never blocks the report.
        mission.NoteStepTaken("south");
        Assert.Equal(0, mission.StepsRemaining);
    }

    [Fact]
    public void Finishing_records_why_and_stops_it()
    {
        var mission = new Mission("scout", budget: 4, source: "delegated");
        Assert.True(mission.Active);

        mission.Finish("the companion reported back");

        Assert.False(mission.Active);
        Assert.Equal("the companion reported back", mission.FinishReason);
    }

    [Fact]
    public void Findings_are_kept_once_each_for_the_report()
    {
        var mission = new Mission("scout", budget: 4, source: "delegated");

        mission.NoteFinding("open forest, valley to one side");
        mission.NoteFinding("open forest, valley to one side"); // a loop should not repeat it
        mission.NoteFinding("  ");
        mission.NoteFinding("a stream runs south");

        Assert.Equal(["open forest, valley to one side", "a stream runs south"], mission.Findings);
    }

    [Fact]
    public void A_budget_of_zero_or_less_is_still_a_budget()
    {
        // Defensive: a bad config value must not produce a mission that can never act
        // and never report.
        var mission = new Mission("scout", budget: 0, source: "delegated");

        Assert.Equal(1, mission.Budget);
        Assert.False(mission.BudgetExhausted);
    }

    [Fact]
    public void The_brief_tells_it_what_it_is_doing_and_where_it_is_up_to()
    {
        var mission = new Mission("scout the open exits", budget: 4, source: "delegated");
        mission.NoteStepTaken("east");
        mission.NoteFinding("a stream runs south");

        var brief = mission.Brief();

        Assert.Contains("scout the open exits", brief);
        Assert.Contains("a stream runs south", brief);
        // The two endings are named as calls, because that is what they are now. The brief used to
        // promise that "a reply that moves nothing ends the errand", and the repair then
        // contradicted it by asking again — measured 2026-09-21: 167 announced-but-unmade moves,
        // 168 repair calls, 20.5% of the evening's spend. The repair's words are the ones the model
        // obeys, so the brief now says the same thing as the harness does.
        Assert.Contains("has to carry the move itself", brief);
        Assert.Contains("`report_back`", brief);
        Assert.DoesNotContain("moves nothing ends the errand", brief);
        // It is a reminder of the situation, not an order, so it stays in character.
        Assert.DoesNotContain("You must", brief);
    }

    [Fact]
    public void The_brief_says_nothing_about_steps_or_budgets()
    {
        // The bound is real and still enforced; it is simply not the companion's subject. It used to
        // open with "this is step 2 of at most 4", and the vocabulary came back out of its mouth in
        // front of the player (issue #36) — so the words are gone from everything it is told, and the
        // place that carries them is the harness's own log.
        var midway = new Mission("scout the open exits", budget: 4, source: "delegated");
        midway.NoteStepTaken("east");
        Assert.DoesNotContain("step 2 of", midway.Brief());
        Assert.DoesNotContain("budget", midway.Brief(), StringComparison.OrdinalIgnoreCase);

        var spent = new Mission("scout", budget: 1, source: "delegated");
        spent.NoteStepTaken("east");
        Assert.True(spent.BudgetExhausted, "the bound is still enforced");
        Assert.DoesNotContain("budget", spent.Brief(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_spent_budget_asks_for_a_report_rather_than_a_plan_to_continue()
    {
        var mission = new Mission("scout", budget: 1, source: "delegated");
        mission.NoteStepTaken("east");

        var brief = mission.Brief();

        Assert.Contains("as much of this as is worth doing", brief);
        Assert.Contains("`report_back`", brief);
        Assert.DoesNotContain("Take the next step", brief);
    }

    [Fact]
    public void A_declaration_made_while_answering_the_player_is_the_player_s_errand()
    {
        // The route is not the owner (04_companion.md §6 (`C42.3`, `C42.6`)). The `errand` tool's own
        // description says it is for "a job your friend has handed you … and not for something you
        // decided to do yourself", so the declaration is the same act as the line that earns a move
        // — read by the model instead of by the tables — and the goal is the player's either way.
        var declared = new Mission(
            "get the bone and take it to town",
            budget: 8,
            source: "declared by the companion",
            declaredOnPass: "player-line");
        Assert.True(declared.FromThePlayer, "a declaration on the player's pass is their errand");
        Assert.Equal("declared by the companion", declared.StartedBy);

        // A job it took up on a pass where the player was not speaking is its own account, and that
        // is the one the small bound is for (C42.5).
        var itsOwnIdea = new Mission(
            "go and see what is down the well",
            budget: 8,
            source: "declared by the companion",
            declaredOnPass: "idle-nudge");
        Assert.False(itsOwnIdea.FromThePlayer);
    }

    [Fact]
    public void The_route_is_not_what_decides_the_owner()
    {
        // Measured 2026-09-30: the three lines the classifier read as "too long for a command" — the
        // evening's three real jobs — all arrived as declarations and were charged the companion's
        // own bound, so all three ended at exactly 8/8 mid-plan, the last one step short of the
        // fountain, while three throwaway questions the tables could read were given the player's.
        Assert.True(Mission.ThePlayerSetTheGoal("declared by the companion", "player-line"));
        Assert.True(Mission.ThePlayerSetTheGoal("declared by the companion", "suggestion"));
        Assert.True(Mission.ThePlayerSetTheGoal("declared by the companion", "handover"));
        Assert.True(Mission.ThePlayerSetTheGoal("the player's instruction", null));
        Assert.True(Mission.ThePlayerSetTheGoal("delegated by the player", null));

        Assert.False(Mission.ThePlayerSetTheGoal("declared by the companion", null));
        Assert.False(Mission.ThePlayerSetTheGoal("declared by the companion", "idle-nudge"));
        Assert.False(Mission.ThePlayerSetTheGoal("declared by the companion", "mission-step"));
    }
}

/// <summary>
/// Did the player ask for something to be done? (issues #31 and #41)
/// <para>
/// Every line in the second theory is from the UAT session, and every one of them became an
/// errand: together they are thirteen of that evening's twenty-six, and the companion said so
/// itself, four turns running — <i>"the harness has interpreted it as an errand"</i>.
/// </para>
/// </summary>
public class InstructionTests
{
    [Theory]
    // A plan, a question about a course of action, an explicit hand-over, an order with the
    // person named in it, and an imperative the classifier counts as conversation.
    [InlineData("can we get back to the town by following the river")]
    [InlineData("what are the exits from here?")]
    [InlineData("scout the open exits")]
    [InlineData("keep going")]
    [InlineData("your job is to map the village")]
    [InlineData("you work the troll while i grab a coffee BRB")]
    [InlineData("try the post office then back here")]
    [InlineData("get us to the cliff bottom")]
    [InlineData("scout")]
    public void Asking_for_something_is_an_instruction(string line)
    {
        Assert.True(
            UtteranceClassifier.ReadsAsAnInstruction(line),
            $"`{line}` asks for something to be done");
    }

    [Theory]
    [InlineData("good morning")]
    [InlineData("booody hel this is confusing")]
    [InlineData("oh - we reloaded the save - thats why the b ranch is back")]
    [InlineData("and now we are back at the troll! FFS")]
    [InlineData("nice work - im glad you remembered the route because I didnt")]
    [InlineData("good spot - west it is")]
    [InlineData("phew - im glad we finally got that.  This game is supposed to be easy")]
    [InlineData("wait whats this")]
    [InlineData("hmm")]
    public void Talking_to_a_friend_is_not(string line)
    {
        Assert.False(
            UtteranceClassifier.ReadsAsAnInstruction(line),
            $"`{line}` is talk, and talk must not become a standing order");
    }

    [Fact]
    public void One_word_is_a_command_not_an_errand()
    {
        // `north` is an instruction-shaped word and the classifier names it a command; either way
        // it never becomes an errand, because nobody hands over a multi-step job in one word.
        Assert.False(UtteranceClassifier.ReadsAsAnInstruction("north"));
        Assert.False(UtteranceClassifier.ReadsAsAnInstruction("look"));
    }

    [Fact]
    public void A_punctuation_mark_is_neither_a_question_nor_an_errand()
    {
        // Measured 2026-09-25: `?` typed at a companion that had gone quiet became an errand three
        // times in one evening, was taken a step, and was called in with no speech at all.
        Assert.False(UtteranceClassifier.ReadsAsAnInstruction("?"));
        Assert.False(UtteranceClassifier.ReadsAsAnInstruction("??"));
    }
}

/// <summary>
/// Handing over an errand, and the "have you considered…" nudge (§iteration 01 §3–4).
/// </summary>
public class DelegationTests
{
    [Theory]
    [InlineData("your job at the moment is to scout all open locations and then hopefully we will have an idea what to do")]
    [InlineData("can you wander around a bit and make up a map")]
    [InlineData("scout the open exits and report back")]
    [InlineData("i want you to explore the woods")]
    [InlineData("keep going")]
    [InlineData("carry on, then tell me what you found")]
    [InlineData("go and find out whether the stream leads anywhere")]
    public void An_errand_is_recognised(string line)
    {
        Assert.True(UtteranceClassifier.IsDelegation(line), $"`{line}` hands over an errand");
    }

    [Theory]
    [InlineData("go north")]           // the player taking a step, not delegating one
    [InlineData("look")]
    [InlineData("take lamp")]
    [InlineData("open the mailbox")]
    [InlineData("inventory")]
    [InlineData("what do you make of this place?")]
    [InlineData("yes")]
    public void A_command_or_a_question_is_not_an_errand(string line)
    {
        Assert.False(UtteranceClassifier.IsDelegation(line), $"`{line}` is not an errand");
    }

    [Theory]
    [InlineData("don't you think we should check the stream?")]
    [InlineData("have you considered going back to the valley?")]
    [InlineData("what do you reckon to the woods?")]
    [InlineData("maybe we should take the lamp")]
    [InlineData("i wonder if the grate opens")]
    public void A_suggestion_is_recognised(string line)
    {
        Assert.True(UtteranceClassifier.IsSuggestion(line), $"`{line}` is thinking aloud");
    }

    [Theory]
    [InlineData("go north")]
    [InlineData("take lamp")]
    [InlineData("what is in the stream?")]
    public void A_command_or_a_plain_question_is_not_a_suggestion(string line)
    {
        Assert.False(UtteranceClassifier.IsSuggestion(line));
    }

    [Fact]
    public void A_long_affirmation_is_consent_only_when_something_is_waiting()
    {
        const string Line = "yes - keep going.  your job at the moment is to scout all open locations";

        // Without anything pending, a long line is conversation — that used to be the
        // whole problem: the companion never learned it had been given an errand.
        Assert.Equal(UtteranceClass.Chat, UtteranceClassifier.Classify(Line).Kind);

        // With a proposal waiting on an answer, it is consent.
        var withPending = UtteranceClassifier.Classify(Line, hasPendingProposal: true);
        Assert.Equal(UtteranceClass.Grant, withPending.Kind);
        Assert.Contains("with something pending", withPending.Reason);

        // …and it is still an errand, either way.
        Assert.True(UtteranceClassifier.IsDelegation(Line));
    }

    [Fact]
    public void A_short_affirmation_is_consent_regardless()
    {
        Assert.Equal(UtteranceClass.Grant, UtteranceClassifier.Classify("yes").Kind);
        Assert.Equal(UtteranceClass.Grant, UtteranceClassifier.Classify("go on then").Kind);
        Assert.Equal(UtteranceClass.Veto, UtteranceClassifier.Classify("no").Kind);
    }

    [Fact]
    public void A_long_refusal_is_a_veto_when_something_is_waiting()
    {
        var veto = UtteranceClassifier.Classify(
            "no - I don't want to bribe the troll, let's look around first",
            hasPendingProposal: true);

        Assert.Equal(UtteranceClass.Veto, veto.Kind);
    }
}
