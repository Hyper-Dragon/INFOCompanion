using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// The parser-vocabulary knowledge of §10.4 — <b>help text for the model</b>.
/// <para>
/// It began as a gate: that live session sent <c>follow stream</c> and <c>downstream</c> to
/// Adventure and was told <i>"That's not a verb I recognise."</i> twice, so acts absent from the
/// dictionary were refused before a turn was spent. That was wrong the moment the game asked a
/// question instead of waiting for a command — no dictionary holds the <c>1</c> a chapter menu
/// wants — so the refusing moved out (`ActPolicyTests`) and what is left is what the model is
/// told: the words this parser accepts, and the ones it has refused.
/// </para>
/// </summary>
public class ParserVocabularyTests
{
    /// <summary>A handful of Adventure's real dictionary words, from its story file.</summary>
    private static readonly string[] AdventuresWords =
        ["look", "east", "north", "take", "lamp", "inventory", "quit", "stream", "open", "mailbox"];

    private static ParserFeedback WithAdventure()
    {
        var feedback = new ParserFeedback();
        feedback.SetKnownVocabulary(AdventuresWords, resolution: 9);
        return feedback;
    }

    [Fact]
    public void the_dictionary_is_a_known_word_list_and_nothing_more()
    {
        var feedback = WithAdventure();

        Assert.True(feedback.Knows("east"));
        Assert.True(feedback.Knows("lamp"));
        // A word this parser does not hold is simply not known — which is a fact to tell the
        // model, not a reason to refuse its act.
        Assert.False(feedback.Knows("follow"));
    }

    [Fact]
    public void with_no_dictionary_nothing_is_narrowed()
    {
        // The degraded path, and the common one for a story whose header cannot be read: no
        // word list means nothing is filtered out of what the model is told.
        var feedback = new ParserFeedback();

        Assert.False(feedback.HasKnownVocabulary);
        Assert.True(feedback.Knows("follow"));
        Assert.Equal(0, feedback.KnownWords);
    }

    [Fact]
    public void a_v3_game_matches_the_word_the_way_the_interpreter_does()
    {
        // The Witness stores six characters, so "inventory" is stored as "invent": the note
        // must not tell the model that word is missing when the game itself accepts it.
        var feedback = new ParserFeedback();
        feedback.SetKnownVocabulary(["invent", "examin", "follow", "north"], resolution: 6);

        Assert.True(feedback.Knows("inventory"));
        Assert.True(feedback.Knows("examine"));
        Assert.True(feedback.Knows("follow"));
        Assert.False(feedback.Knows("invex"));
    }

    [Fact]
    public void a_v4_game_keeps_the_whole_word()
    {
        var feedback = new ParserFeedback();
        feedback.SetKnownVocabulary(["inventory"], resolution: 9);

        Assert.True(feedback.Knows("inventory"));
        Assert.False(feedback.Knows("invent"));
    }

    [Fact]
    public void the_game_being_played_decides_so_a_word_is_never_globally_banned()
    {
        // `follow` is refused by Adventure and known by The Witness. Nothing may remember that
        // across a game change, in either direction.
        var feedback = WithAdventure();
        Assert.False(feedback.Knows("follow"));

        feedback.SetKnownVocabulary(["follow", "invent", "north"], resolution: 6);
        Assert.True(feedback.Knows("follow"));
    }

    [Fact]
    public void setting_a_new_vocabulary_forgets_the_previous_game()
    {
        var feedback = WithAdventure();
        feedback.NoteRejected("chase");
        Assert.Contains("chase", feedback.Rejected);

        feedback.SetKnownVocabulary(["invent"], resolution: 6);

        Assert.DoesNotContain("chase", feedback.Rejected, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, feedback.KnownWords);
    }

    [Fact]
    public void the_prompt_note_offers_the_actions_this_game_accepts()
    {
        var note = WithAdventure().ContextNote();

        Assert.NotNull(note);
        Assert.Contains("10 words", note);
        Assert.Contains("east", note);
        Assert.Contains("inventory", note);
        // And it must not offer a word this parser would refuse.
        Assert.DoesNotContain("follow", note);
        Assert.DoesNotContain("swim", note);
        Assert.Contains("Never invent a verb", note);
    }

    [Fact]
    public void the_prompt_note_names_the_words_this_parser_has_no_word_for()
    {
        var feedback = WithAdventure();
        feedback.NoteRejected("chase");
        feedback.NoteAccepted("east");

        var note = feedback.ContextNote();

        Assert.NotNull(note);
        Assert.Contains("no word for", note);
        Assert.Contains("chase", note);
        Assert.Contains("accepted", note);
    }

    /// <summary>
    /// The `…it accepts: …` line, which is the list the model reads first.
    /// </summary>
    private static string TheAcceptedLine(string note)
    {
        var start = note.IndexOf("it accepts: ", StringComparison.Ordinal);
        Assert.True(start >= 0, "the note names no accepted actions");
        var end = note.IndexOf('\n', start);
        return note[start..(end < 0 ? note.Length : end)];
    }

    [Fact]
    public void a_word_unknown_verdict_is_about_the_word_the_game_named()
    {
        // Measured 2026-09-25, one evening of Moonmist: `examine sliding doors` was answered
        // `[I don't know the word "doors."]` and the verdict was recorded against `examine`. Five
        // good verbs went onto the list that way — examine, take, open, ask and say — and the list
        // is rebuilt from the store at every load, so the companion kept reading it as fact.
        var feedback = WithAdventure();
        const string said = "[I don't know the word \"lamp.\"]";

        feedback.NoteAttempt(
            ParserFeedback.TheAttempt("take lamp", said, ParserFeedback.LooksLikeWordUnknown(said)));

        Assert.Contains("lamp", feedback.Rejected);
        Assert.DoesNotContain("take", feedback.Rejected);

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.Contains("no word for: lamp", note);
        Assert.DoesNotContain("lamp", TheAcceptedLine(note));
    }

    [Fact]
    public void a_verdict_that_names_no_word_is_still_about_the_lines_verb()
    {
        // The other half of the rule, unchanged: `That's not a verb I recognise.` names nothing,
        // so the line's own verb is what the verdict is about — and an apostrophe in `That's` is
        // not a quoted word.
        var feedback = WithAdventure();
        const string said = "That's not a verb I recognise.";

        Assert.Null(ParserFeedback.NamedWord(said));

        feedback.NoteAttempt(
            ParserFeedback.TheAttempt("swim upstream", said, ParserFeedback.LooksLikeWordUnknown(said)));

        Assert.Contains("swim", feedback.Rejected);
    }

    [Fact]
    public void the_note_never_names_one_word_in_both_lists()
    {
        // The two lists come from different things — the game's dictionary, and the verdicts met in
        // play — so they can disagree, and this is the shape of the disagreement that cost the
        // companion thirty-two thinking blocks in one evening: `ask, examine, open, say` offered as
        // accepted actions in one line and named as words the parser has no word for in the next.
        var feedback = WithAdventure();
        const string said = "[I don't know the word \"east.\"]";

        feedback.NoteAttempt(
            ParserFeedback.TheAttempt("east", said, ParserFeedback.LooksLikeWordUnknown(said)));

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.Contains("no word for: east", note);
        Assert.DoesNotContain("east", TheAcceptedLine(note));
    }

    [Fact]
    public void a_refusal_about_the_object_never_takes_the_verb_off_the_table()
    {
        // The 2026-09-21 session in one assertion. `get bone` came back as "You can't see any
        // bone here!" — a remark about the bone — and reading that as a verdict on `get` is what
        // had the companion working around five perfectly good verbs for sixteen hours.
        var feedback = WithAdventure();
        const string said = "You can't see any bone here!";

        var isNews = feedback.NoteAttempt(
            ParserFeedback.TheAttempt("get bone", said, ParserFeedback.LooksLikeWordUnknown(said)));

        Assert.True(isNews);
        Assert.DoesNotContain("get", feedback.Rejected);

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.DoesNotContain("no word for", note);
        // What it says instead is what happened, in the game's own words.
        Assert.Contains("get bone", note);
        Assert.Contains(said, note);
        Assert.Contains("This is history, not a rule", note);
    }

    [Fact]
    public void an_invented_verb_is_still_steered_away_from()
    {
        // The falsifier 02_game_boundary.md §4.3 (`G24`) asks for: splitting the verdicts must not
        // turn the note into "anything goes". A parser with no word for `follow` still says so.
        var feedback = WithAdventure();
        const string said = "That's not a verb I recognise.";

        feedback.NoteAttempt(
            ParserFeedback.TheAttempt("follow stream", said, ParserFeedback.LooksLikeWordUnknown(said)));

        Assert.Contains("follow", feedback.Rejected);

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.Contains("no word for: follow", note);
        Assert.Contains(said, note);
    }

    [Fact]
    public void the_history_is_bounded_and_keeps_the_newest_attempts()
    {
        var feedback = WithAdventure();
        for (var i = 0; i < 10; i++)
        {
            var said = $"You can't see any thing{i} here!";
            feedback.NoteAttempt(ParserFeedback.TheAttempt($"look at thing{i}", said, false));
        }

        Assert.Equal(6, feedback.Attempts.Count);

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.Contains("thing9", note);
        Assert.DoesNotContain("thing3", note);
    }

    [Fact]
    public void repeating_the_same_line_is_not_news()
    {
        // The bool is what stops the store growing by a line every time the same thing is tried.
        var feedback = WithAdventure();
        var attempt = new ParserAttempt("get bone", "You can't see any bone here!", WordUnknown: false);

        Assert.True(feedback.NoteAttempt(attempt));
        Assert.False(feedback.NoteAttempt(attempt));
    }

    [Fact]
    public void with_nothing_learned_there_is_no_note_to_send()
    {
        // The prompt stays exactly as the spec describes it until there is something
        // the game itself has told us.
        Assert.Null(new ParserFeedback().ContextNote());
    }

    [Fact]
    public void the_verb_of_a_command_is_read_the_way_a_player_types_it()
    {
        Assert.Equal("take", ParserFeedback.Verb("take lamp"));
        Assert.Equal("north", ParserFeedback.Verb("  NORTH  "));
        Assert.Equal("look", ParserFeedback.Verb("look."));
        Assert.Equal("east", ParserFeedback.Verb("east,"));
        Assert.Null(ParserFeedback.Verb(""));
        Assert.Null(ParserFeedback.Verb("   "));
        Assert.Null(ParserFeedback.Verb(null));
    }

    [Fact]
    public void what_a_game_says_when_it_refuses_a_command_is_recognised()
    {
        // The exact words from the live run, and the neighbouring phrasings other
        // Infocom games use.
        Assert.True(ParserFeedback.LooksLikeRejection("That's not a verb I recognise."));
        Assert.True(ParserFeedback.LooksLikeRejection("I don't know the word \"downstream\"."));
        Assert.True(ParserFeedback.LooksLikeRejection("I beg your pardon?"));
        Assert.True(ParserFeedback.LooksLikeRejection("I don't understand that sentence."));

        Assert.False(ParserFeedback.LooksLikeRejection("You are in a valley."));
        Assert.False(ParserFeedback.LooksLikeRejection("Taken."));
        Assert.False(ParserFeedback.LooksLikeRejection(""));
    }

    [Fact]
    public void only_a_word_verdict_is_a_verdict()
    {
        // Both are refusals; only one of them is about the verb. This split is the whole of
        // §4.3, and getting it wrong is what recorded five good words as refused in one evening.
        Assert.True(ParserFeedback.LooksLikeWordUnknown("That's not a verb I recognise."));
        Assert.True(ParserFeedback.LooksLikeWordUnknown("I don't know the word \"downstream\"."));

        Assert.False(ParserFeedback.LooksLikeWordUnknown("You can't see any bone here!"));
        Assert.False(ParserFeedback.LooksLikeWordUnknown("I beg your pardon?"));
        Assert.False(ParserFeedback.LooksLikeWordUnknown("I don't understand that sentence."));
        // A refusal is still a refusal, either way round.
        Assert.True(ParserFeedback.LooksLikeRejection("You can't see any bone here!"));
    }

    [Fact]
    public void the_attempt_is_the_line_and_the_sentence_that_came_back()
    {
        // The sentence, not the screen: a refusal is one line, and a framed turn in the prompt is
        // noise the model has already read.
        var attempt = ParserFeedback.TheAttempt(
            "get bone",
            "You are in an open grave.\nYou can't see any bone here!\n>",
            wordUnknown: false);

        Assert.Equal("get bone", attempt.Typed);
        Assert.Equal("You can't see any bone here!", attempt.GameSaid);
        Assert.False(attempt.WordUnknown);
    }

    [Fact]
    public void the_generic_action_list_is_the_union_of_verbs_and_directions()
    {
        var actions = Vocabulary.ActionWords.ToList();

        Assert.Contains("take", actions);
        Assert.Contains("east", actions);
        Assert.Contains("follow", actions);
        // Deduplicated: the verb list contains "wait" and "z" more than once.
        Assert.Equal(actions.Count, actions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Sorted, because the note is shown to a model.
        Assert.Equal(actions.OrderBy(word => word, StringComparer.Ordinal), actions);
    }
}
