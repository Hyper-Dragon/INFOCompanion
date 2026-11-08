using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// The classification table of §10.3. These are the cases that decide how a player's
/// line is read — and, since the directive fast path was removed, every one of them
/// reaches the companion. The reason is what the log and the read-back carry.
/// </summary>
public class ClassificationTests
{
    [Theory]
    // Bare game commands: read as commands, and sent to the companion like any other line.
    [InlineData("north")]
    [InlineData("n")]
    [InlineData("look")]
    [InlineData("l")]
    [InlineData("inventory")]
    [InlineData("take lamp")]
    [InlineData("take the brass lantern")]
    [InlineData("open mailbox")]
    [InlineData("unlock the door with the key")]
    [InlineData("put the coin in the slot")]
    [InlineData("go north")]
    [InlineData("examine the painting")]
    [InlineData("  north  ")]
    public void Bare_commands_reach_the_companion(string line)
    {
        // A command-shaped line used to be typed into the game with no model call. That fast
        // path was a hangover from the initial designs: the player who wants to drive the
        // parser has the `.`/`/direct` form, and a line typed to the companion is the
        // companion's to read. What the tables still do is name the line, so the log can say a
        // command-shaped line arrived.
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Chat, result.Kind);
        Assert.True(Vocabulary.WasACommand(result.Reason), result.Reason);
    }

    [Theory]
    [InlineData("can we go north")]
    [InlineData("let's try the grate")]
    [InlineData("lets check the cellar")]
    [InlineData("we should look behind the painting")]
    [InlineData("i want to try the door")]
    [InlineData("how about we head down")]
    [InlineData("maybe we take the lamp")]
    public void Plans_are_steers(string line)
    {
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Steer, result.Kind);
    }

    [Theory]
    [InlineData("what did the troll say?")]
    [InlineData("where are we?")]
    [InlineData("how do we get past the troll")]
    [InlineData("did we try that already")]
    [InlineData("is the grate locked")]
    [InlineData("why did that happen")]
    public void Questions_are_questions(string line)
    {
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Question, result.Kind);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("??")]
    [InlineData("!")]
    [InlineData("...")]
    public void A_line_with_no_word_in_it_is_not_a_question(string line)
    {
        // The 2026-09-25 UAT: a bare "?" is someone prodding a companion they think has gone
        // quiet, and the classifier read the question mark as a question — so the harness bought a
        // standing order with it and the errand's goal was recorded as "?".
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Chat, result.Kind);
        Assert.Equal("no words", result.Reason);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("yep")]
    [InlineData("ok")]
    [InlineData("go on then")]
    [InlineData("try it")]
    [InlineData("do it")]
    [InlineData("fine")]
    [InlineData("go ahead")]
    [InlineData("sure")]
    public void Approval_is_a_grant(string line)
    {
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Grant, result.Kind);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("wait")]
    [InlineData("stop")]
    [InlineData("don't")]
    [InlineData("hold on")]
    [InlineData("not yet")]
    [InlineData("no thanks")]
    [InlineData("cancel")]
    public void Refusal_is_a_veto(string line)
    {
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Veto, result.Kind);
    }

    [Theory]
    [InlineData("the troll is still there")]
    [InlineData("that painting is odd")]
    [InlineData("I remember playing this as a kid")]
    [InlineData("hmm")]
    [InlineData("nice one")]
    public void Everything_else_is_conversation(string line)
    {
        var result = UtteranceClassifier.Classify(line);
        Assert.Equal(UtteranceClass.Chat, result.Kind);
    }

    [Fact]
    public void A_long_answer_beginning_with_yes_is_not_a_grant()
    {
        // Approval has to be short to count; otherwise a sentence that happens to
        // open with "yes" would silently approve something.
        var result = UtteranceClassifier.Classify("yes but the troll said the password was friend");
        Assert.NotEqual(UtteranceClass.Grant, result.Kind);
    }

    [Fact]
    public void Command_words_are_read_as_a_plan_or_as_a_command()
    {
        // "let's go north" is a plan; "go north" is a command. Both reach the model now — the
        // difference is the reading, not the route.
        Assert.Equal(UtteranceClass.Steer, UtteranceClassifier.Classify("let's go north").Kind);
        Assert.True(Vocabulary.WasACommand(UtteranceClassifier.Classify("go north").Reason));
        Assert.True(Vocabulary.WasACommand(UtteranceClassifier.Classify("go up").Reason));
    }

    [Theory]
    // Issue #40, from the UAT session. The first one was typed into Wishbringer, which
    // answered `(This story doesn't know the word "-.")` — the player's remark to the
    // companion landed on the game's screen and cost them both a turn. Every line here
    // begins with a word the medium uses, and none of them is a command.
    [InlineData("wait - the trun is rotten to the core.  whats inside the trunk")]
    [InlineData("wait whats this")]
    [InlineData("look, the troll has gone")]
    [InlineData("open it and see, but tell me first")]
    [InlineData("north is right but only after we light the lamp")]
    [InlineData("go on then, take the lamp with you this time please")]
    public void Prose_that_opens_with_a_verb_is_not_a_command(string line)
    {
        // A short veto or grant is a fair reading of some of these ("wait whats this"), and that
        // is fine — the claim is only that prose is not read as a command for the parser.
        var result = UtteranceClassifier.Classify(line);
        Assert.False(Vocabulary.WasACommand(result.Reason), $"{result.Kind}: {result.Reason}");
    }

    [Fact]
    public void A_command_holding_a_word_the_story_never_heard_is_not_a_command()
    {
        // The strongest test available, and the reason the dictionary is passed in: no
        // amount of shape-checking catches a short sentence that happens to open with a
        // verb. A story only understands its own words (issue #40).
        var wishbringer = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "go", "north", "south", "look", "take", "the", "trunk", "branch", "lamp",
        };

        var chatting = UtteranceClassifier.Classify("go check the trunk", knowsWord: wishbringer.Contains);
        Assert.Equal(UtteranceClass.Chat, chatting.Kind);
        Assert.Equal("a word the story does not know", chatting.Reason);

        // And a real command containing only words the story knows is still named as one — and
        // still reaches the companion.
        var commanding = UtteranceClassifier.Classify("take the trunk", knowsWord: wishbringer.Contains);
        Assert.Equal(UtteranceClass.Chat, commanding.Kind);
        Assert.Equal("every word is in the story's dictionary", commanding.Reason);
        Assert.True(Vocabulary.WasACommand(commanding.Reason));
    }

    // -- the five lines of 2026-09-29 that the harness got wrong (04_companion.md §10.3 (`C44`–`C47`)) --

    [Fact]
    public void A_long_instruction_reads_as_a_brief_rather_than_as_conversation()
    {
        // Verbatim from the window. Both were `too long for a command` → chat — the two most important
        // instructions of the evening — and the model rescued both: *"that's a job, and I missed it
        // reading it as talk"*. Per `F19` it may correct the harness; it should not have to.
        //
        // Fixed by the phrase table and not by a shape test, because the shape test read two lines of
        // their own UATs as briefs too. See the guard below, which is the other half of the decision.
        var job = UtteranceClassifier.Classify(
            "no - this is the new job - examine everything in the game room and see what we can find");
        var back = UtteranceClassifier.Classify(
            "sheesh...get us back to our room but stop if we find something new");

        Assert.Equal(UtteranceClass.Steer, job.Kind);
        Assert.Equal("brief phrase", job.Reason);
        Assert.Equal(UtteranceClass.Steer, back.Kind);
    }

    [Fact]
    public void The_two_lines_the_shape_test_broke_are_still_conversation()
    {
        // The pair that decided this: neither carries a brief phrase, and both name a thing to do, so
        // no shape test can tell them from the briefs above.
        Assert.Equal(
            UtteranceClass.Chat,
            UtteranceClassifier.Classify("wait - the trun is rotten to the core.  whats inside the trunk").Kind);
        Assert.Equal(
            UtteranceClass.Chat,
            UtteranceClassifier.Classify("I remember playing this as a kid").Kind);
    }

    [Fact]
    public void Prose_that_happens_to_be_long_is_still_conversation()
    {
        // Length alone is not the test, or an evening of chatty paragraphs becomes a series of
        // briefs. Held beside the defect above: whatever fixes it must not touch this.
        var prose = UtteranceClassifier.Classify(
            "I was thinking about the way the poodle looked at us when we came in through the door and "
            + "whether that means anything at all, because it did not seem frightened");

        Assert.Equal(UtteranceClass.Chat, prose.Kind);
    }

    [Fact]
    public void An_order_wearing_a_question_mark_is_not_settled_by_the_mark()
    {
        // `can we take a gun` has always read as a plan. `can you get us back…?` is one word of shape
        // apart and read as a question, so the mark decided it and an instruction became chat.
        var back = UtteranceClassifier.Classify(
            "can you get us back to our bedroom and see if the game will let us go to sleep yet?");
        var gun = UtteranceClassifier.Classify("can we take a gun");

        Assert.Equal(UtteranceClass.Steer, back.Kind);
        Assert.Equal("order phrased as a question", back.Reason);
        Assert.Equal(UtteranceClass.Steer, gun.Kind);
    }

    [Fact]
    public void A_suggestion_is_not_read_as_a_question_because_it_opens_with_one()
    {
        // `what about the desk draws` came back as `question opener` → chat, and the model had to
        // rescue it as a suggestion. It is the same shape as `what if we…`, which has always read as
        // a plan, and the two now agree.
        Assert.Equal(UtteranceClass.Steer, UtteranceClassifier.Classify("what about the desk draws").Kind);
        Assert.Equal(UtteranceClass.Steer, UtteranceClassifier.Classify("what about the table pockets").Kind);
    }

    [Fact]
    public void The_storys_own_dictionary_can_name_a_command_in_its_own_language()
    {
        // §4.17. The English tables are a fast path, not the law: a French player of a French
        // story types "prends la lampe", and every word of the line being one the parser knows
        // is the signal — the opener list has never heard of `prends` and never will. The line
        // reaches the companion with that reading attached; nothing is typed into the game.
        var french = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prends", "la", "lampe", "nord", "regarde",
        };

        var order = UtteranceClassifier.Classify("prends la lampe", knowsWord: french.Contains);
        Assert.Equal(UtteranceClass.Chat, order.Kind);
        Assert.Equal("every word is in the story's dictionary", order.Reason);

        var direction = UtteranceClassifier.Classify("nord", knowsWord: french.Contains);
        Assert.Equal(UtteranceClass.Chat, direction.Kind);
        Assert.True(Vocabulary.WasACommand(direction.Reason));

        // …and a line the dictionary cannot vouch for is talk rather than a command.
        var talk = UtteranceClassifier.Classify("attends une seconde", knowsWord: french.Contains);
        Assert.False(Vocabulary.WasACommand(talk.Reason), talk.Reason);
    }

    [Fact]
    public void The_dictionary_is_optional_and_its_absence_still_names_a_command()
    {
        // The dictionary is a courtesy the core may not have managed to read (it comes out
        // of the story file, and not every one can be parsed). Passing nothing must leave a
        // command named as one — the reading is the log's and the read-back's, and no line is
        // silenced by a dictionary that is missing.
        var north = UtteranceClassifier.Classify("north");
        Assert.Equal(UtteranceClass.Chat, north.Kind);
        Assert.True(Vocabulary.WasACommand(north.Reason), north.Reason);

        var unlock = UtteranceClassifier.Classify("unlock the grate with the keys");
        Assert.True(Vocabulary.WasACommand(unlock.Reason), unlock.Reason);
    }

    [Theory]
    [InlineData("burn the candle")]
    [InlineData("attack the troll")]
    [InlineData("drink the potion")]
    [InlineData("bribe the troll")]
    [InlineData("give the coin to the troll")]
    [InlineData("throw the axe")]
    [InlineData("break the mirror")]
    public void Risky_actions_are_recognised(string action)
    {
        Assert.True(Vocabulary.IsRiskyAction(action), action);
    }

    [Theory]
    [InlineData("look")]
    [InlineData("take lamp")]
    [InlineData("open mailbox")]
    [InlineData("read leaflet")]
    [InlineData("inventory")]
    [InlineData("north")]
    [InlineData("examine grate")]
    // Issue #29: these were all flagged as risky, and none of them are consequences —
    // they are how one plays a text adventure. Every one of them bought the player a
    // consent prompt for something they had already asked for.
    [InlineData("push the door")]
    [InlineData("pull the lever")]
    [InlineData("press the red button")]
    [InlineData("climb the tree")]
    [InlineData("jump the gap")]
    [InlineData("drop the lamp")]
    [InlineData("fill the bottle")]
    [InlineData("rub the lamp")]
    [InlineData("unlock the gate")]
    // Issue #23: `light lamp` was flagged too, so the companion proposed it and waited for a
    // grant it could not get — the run stopped in the dark at turn eighteen. Making light is
    // the one action these stories require; it is not a consequence.
    [InlineData("light the lamp")]
    [InlineData("light lamp")]
    [InlineData("ignite the torch")]
    public void Ordinary_actions_are_not_flagged(string action)
    {
        Assert.False(Vocabulary.IsRiskyAction(action), action);
    }

    [Theory]
    [InlineData("look", RiskLevel.Low)]
    [InlineData("north", RiskLevel.Low)]
    [InlineData("read leaflet", RiskLevel.Low)]
    [InlineData("take the bone", RiskLevel.Medium)]
    [InlineData("open the grate", RiskLevel.Medium)]
    [InlineData("light lamp", RiskLevel.Medium)]
    [InlineData("give bone to poodle", RiskLevel.High)]
    [InlineData("burn the letter", RiskLevel.High)]
    public void Every_act_has_a_class(string act, RiskLevel expected)
    {
        // The class is what the player's standing level is compared against (§4.7), so the
        // three boundaries — looking, handling, and what cannot be taken back — are pinned
        // here rather than left to whichever verb list was edited last.
        Assert.Equal(expected, Vocabulary.RiskOf(act));
    }
}
