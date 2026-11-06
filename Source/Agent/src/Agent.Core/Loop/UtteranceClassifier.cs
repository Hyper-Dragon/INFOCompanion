namespace Agent.Core.Loop;

/// <summary>How the companion should treat a player line (§10.3).</summary>
public enum UtteranceClass
{
    /// <summary>A plan to carry out over several steps.</summary>
    Steer,

    /// <summary>A question to answer from the transcript and notebook.</summary>
    Question,

    /// <summary>Approval of a pending proposal.</summary>
    Grant,

    /// <summary>Cancellation of a pending or in-flight action.</summary>
    Veto,

    /// <summary>Ordinary conversation; no action implied.</summary>
    Chat,
}

/// <summary>The classifier's verdict, with the rule that produced it.</summary>
public sealed record Classification(UtteranceClass Kind, string Reason);

/// <summary>
/// Rules-first, model-fallback classification (§10.3).
/// <para>
/// The rules exist to catch the common cases for free. Anything they do not
/// recognise becomes <see cref="UtteranceClass.Chat"/>, which means it goes to the
/// model — and the model, being in the middle of a conversation, is well placed to
/// work out what was meant. That is the fallback: ambiguity is handled by the same
/// call that would have happened anyway.
/// </para>
/// <para>
/// Every line reaches the model now. A line the tables read as a command is Chat like any
/// other, with the reading kept in its reason; nothing is typed into the game on the player's
/// behalf. The player who wants to drive the parser has the `.`/`/direct` form (issue #24).
/// </para>
/// </summary>
public static class UtteranceClassifier
{
    /// <summary>Acknowledgements are only credible when they are short.</summary>
    private const int ShortPhraseWords = Config.ConfigDefaults.ShortPhraseWords;

    /// <summary>
    /// What a player says when they are handing over an errand rather than typing a
    /// command (§iteration 01 §3). Deliberately narrow: "go north" is a command and
    /// stays a command, while "your job is to scout the open exits" is a delegation.
    /// </summary>
    private static readonly string[] ErrandPhrases =
    [
        "scout", "recon", "wander", "sweep", "map it out", "make a map", "make up a map",
        "explore the", "explore this", "look around a", "poke around", "your job", "your task",
        "i want you to", "i'd like you to", "id like you to", "keep going", "carry on",
        "report back", "find out", "work out", "check the whole", "go and",
    ];

    /// <summary>
    /// Errand phrasing that delegates even when the line opens with a word the game knows:
    /// "go and find out whether the stream leads anywhere" is a person asking for
    /// investigation, not a command any parser will accept.
    /// </summary>
    private static readonly string[] UnmistakableErrandPhrases =
    [
        "your job", "your task", "i want you to", "i'd like you to", "id like you to",
        "go and", "scout", "recon", "wander", "sweep", "map it out", "make a map",
        "make up a map", "report back", "keep going", "carry on", "find out", "work out",
    ];

    /// <summary>
    /// Nudges of the "have you considered…" kind (§iteration 01 §4): the player is
    /// thinking aloud at the companion, expecting it to weigh the idea and update its
    /// own notes, not to obey or ignore it.
    /// </summary>
    private static readonly string[] SuggestionPhrases =
    [
        "don't you think", "dont you think", "do you think", "have you considered",
        "what do you reckon", "maybe we should", "maybe we could", "shouldn't we",
        "shouldnt we", "would it be better", "what about", "how about we",
        "i wonder if", "don't you reckon", "dont you reckon",
    ];

    /// <summary>
    /// Classify a player line.
    /// <para>
    /// <paramref name="hasPendingProposal"/> matters: with something actually waiting on
    /// an answer, a <em>long</em> reply that opens with an affirmation is consent. Without
    /// it, "yes - keep going, your job is to scout the open locations" was classified as
    /// idle conversation and the companion never learned it had been given an errand.
    /// </para>
    /// <para>
    /// <paramref name="knowsWord"/>, when the story's dictionary has been read, is what tells
    /// a one-word command from a sentence that merely opens with a verb (issue #40).
    /// </para>
    /// </summary>
    public static Classification Classify(
        string? line,
        bool hasPendingProposal = false,
        Func<string, bool>? knowsWord = null,
        int shortPhraseWords = ShortPhraseWords,
        int maxCommandWords = Vocabulary.MaxCommandWords)
    {
        var text = (line ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new Classification(UtteranceClass.Chat, "empty");
        }

        // A line with no word in it is not a question, whoever typed the question mark. "?" is
        // somebody prodding a companion they think has gone quiet, and the answer to it is speech:
        // measured 2026-09-25, a bare "?" became the *goal of an errand* three times in one evening
        // (`mission_start` with `goal: "?"`, then a step taken and the errand called in with no
        // speech at all), which is a standing order bought with a punctuation mark.
        if (!text.Any(char.IsLetter))
        {
            return new Classification(UtteranceClass.Chat, "no words");
        }

        var lowered = text.ToLowerInvariant();
        var words = lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var isShort = words.Length <= shortPhraseWords;

        // 1. Approval and refusal. Short lines are always credible; a long line is
        //    credible only when the player is answering something that is pending.
        if ((isShort || hasPendingProposal) && StartsWithPhrase(lowered, Vocabulary.GrantPhrases))
        {
            return new Classification(
                UtteranceClass.Grant,
                isShort ? "grant phrase" : "grant phrase with something pending");
        }

        if ((isShort || hasPendingProposal) && StartsWithPhrase(lowered, Vocabulary.VetoPhrases))
        {
            return new Classification(
                UtteranceClass.Veto,
                isShort ? "veto phrase" : "veto phrase with something pending");
        }

        // 2. A question mark is unambiguous, so it decides first — **except** when the line is an
        //    order wearing one. `can you get us back to our bedroom and see if the game will let us
        //    go to sleep yet?` was classified `question mark` → chat on 2026-09-29, while
        //    `can we take a gun`, one word of shape apart, matched `steer phrase`. Two rules that
        //    disagree about one shape settle it by accident, so the delegation forms are checked
        //    first and both readings agree again.
        if (StartsWithPhrase(lowered, Vocabulary.DelegationPhrases))
        {
            return new Classification(
                UtteranceClass.Steer,
                text.EndsWith('?') ? "order phrased as a question" : "delegation phrase");
        }

        if (text.EndsWith('?'))
        {
            return new Classification(UtteranceClass.Question, "question mark");
        }

        // 3. Plans phrased as "how about…", "can we…", "let's…". This comes before
        //    the question openers, because several of them ("how about", "what if")
        //    are plans rather than questions.
        if (StartsWithPhrase(lowered, Vocabulary.SteerPhrases))
        {
            return new Classification(UtteranceClass.Steer, "steer phrase");
        }

        // 4. Questions by opener.
        if (StartsWithPhrase(lowered, Vocabulary.QuestionPhrases))
        {
            return new Classification(UtteranceClass.Question, "question opener");
        }

        // 5. A complete game command. This is no longer a route of its own: a line that read as
        //    one used to be typed into the game with no model call, and that fast path is gone —
        //    the player who wants to drive the parser has the `.`/`/direct` form, and a line
        //    typed to the companion is the companion's to read. The check stays because the
        //    reason names the line, for the log and for the read-back: "every word is in the
        //    story's dictionary" is the trace that a command-shaped line went to the model
        //    rather than the parser (issue #40). It comes *after* the conversational checks on
        //    purpose: "go north" is a command, "we should go north" is a plan; ordinary prose
        //    still just reads as `conversation`.
        if (Vocabulary.IsBareCommand(text, out var reason, knowsWord, maxCommandWords))
        {
            return new Classification(UtteranceClass.Chat, reason);
        }

        // 6. **The long-line rule**, and it is a phrase table rather than a shape (§10.3, `C47.3`). A
        //    line refused for being `too long for a command` used to be filed as chat, and on
        //    2026-09-29 that swallowed the two most important instructions of the evening — *"no - this
        //    is the new job - examine everything in the game room…"* and *"sheesh…get us back to our
        //    room but stop if we find something new"* — both of which the model then rescued out of a
        //    conversation.
        //
        //    The first attempt was *"long, names something to do, asks nothing ⇒ directive"*. It fixed
        //    both measured lines and broke two that came from their own UATs (`wait - the trun is
        //    rotten to the core.  whats inside` and `I remember playing this as a kid`), because both
        //    do name a thing to do. A brief carries phrasing a remark does not, so this is an
        //    **inclusion test over phrases that were actually written**: a line carrying none of them
        //    stays conversation, which is the safe direction for this to be wrong in.
        //
        //    Steer and not a command sent to the game: a brief is a job that takes steps, so the
        //    companion reads it and decides.
        if (Vocabulary.BriefPhrases.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal)))
        {
            return new Classification(UtteranceClass.Steer, "brief phrase");
        }

        return new Classification(
            UtteranceClass.Chat,
            Vocabulary.WasCommandShaped(reason) ? reason : "conversation");
    }

    /// <summary>
    /// Is the player handing over an errand rather than typing a command?
    /// <para>
    /// A line that the game itself would understand is left alone: "go north" is the
    /// player taking a step, not delegating one.
    /// </para>
    /// </summary>
    public static bool IsDelegation(string? line)
    {
        var text = (line ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var lowered = text.ToLowerInvariant();
        if (!ErrandPhrases.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal)))
        {
            return false;
        }

        // A line the game itself would understand is the player taking a step, not
        // delegating one — unless the phrasing is unmistakeably an errand.
        if (Vocabulary.IsBareCommand(text, out _)
            && !UnmistakableErrandPhrases.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Did the player ask for something to be done? (issues #31 and #41)
    /// <para>
    /// Distinct from <see cref="Classify"/>, which answers "may this line go straight to the
    /// parser?". A line can be a perfectly good instruction for a companion and no kind of game
    /// command at all: <i>"try the post office then back here"</i> contains <c>try</c>, which the
    /// classifier counts as a conversational marker, and <i>"you work the troll while I get a
    /// coffee"</i> is an order with somebody else's name in it.
    /// </para>
    /// <para>
    /// Used to decide whether a player's line becomes the work in hand. Measured over one evening:
    /// of twenty-six errands, thirteen were started from lines like <i>"good morning"</i> (three
    /// moves), <i>"booody hel this is confusing"</i> (seven) and <i>"oh - we reloaded the save"</i>
    /// (eight), because the rule was "the player's line produced a move, so it must have been an
    /// instruction". A greeting is not an instruction, and being lost is the opposite of one.
    /// </para>
    /// <para>
    /// The shapes that survive are the ones that point at something. Everything else a friend says
    /// — a greeting, a complaint, a discovery, a joke — is talk, and talk can still produce a move:
    /// the companion is free to act on its own initiative. What it does not do is take a standing
    /// order to keep making them.
    /// </para>
    /// </summary>
    public static bool ReadsAsAnInstruction(string? line)
    {
        var text = (line ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        // A plan, or a question about a course of action: "can we try the branch in the river",
        // "what are the exits from here?". The classifier's own two kinds, reused rather than
        // restated, so the two rules cannot drift apart.
        if (Classify(text).Kind is UtteranceClass.Steer or UtteranceClass.Question)
        {
            return true;
        }

        // An explicit hand-over: "your job is to…", "scout the open exits", "keep going".
        if (IsDelegation(text))
        {
            return true;
        }

        var first = words[0].Trim('.', ',', '!', '?', ':', '"', '\'');

        // One word is not an errand: nobody hands over a multi-step job in a single word.
        // Whether it is a command for the game is the classifier's question, and the companion
        // is the one that reads it now that nothing is typed into the parser on the player's
        // behalf.
        if (words.Length == 1)
        {
            return false;
        }

        // An order with the person named in it: "you work the troll while I get a coffee".
        if (first is "you" or "u")
        {
            return true;
        }

        // A line that opens with a verb the medium uses — unless it is prose wearing a verb's
        // coat. "try the post office then back here" is an instruction; "wait whats this" is
        // somebody talking, and the word that gives it away is the one a parser would never see.
        if (Vocabulary.StartsWithCommandWord(first) && !words.Any(Vocabulary.IsProseWord))
        {
            return true;
        }

        return ImperativeOpeners.Contains(first, StringComparer.Ordinal);
    }

    /// <summary>
    /// Imperatives the classifier counts as *conversation*, and why the list is this short.
    /// <para>
    /// <c>try</c> is in <see cref="Vocabulary.ConversationalMarkers"/> because the phrases it
    /// usually starts — "try to…", "try and…" — are plans rather than orders, and that is right
    /// for deciding whether a line may go straight to a parser. It is wrong for deciding whether a
    /// person asked for something: <i>"try the post office then back here"</i> is as much an order
    /// as "go north", and it is the shape a player actually uses when they want two things done.
    /// </para>
    /// <para>
    /// Kept to one word on purpose. Every other imperative worth naming is already a verb the
    /// medium uses (a verb the parser knows), so this list only has to hold the exceptions — and a
    /// list that does not grow is a list that cannot become a per-player table of phrases.
    /// </para>
    /// </summary>
    private static readonly string[] ImperativeOpeners = ["try"];

    /// <summary>Is the player thinking aloud at the companion (§iteration 01 §4)?</summary>
    public static bool IsSuggestion(string? line)
    {
        var lowered = (line ?? string.Empty).Trim().ToLowerInvariant();
        if (lowered.Length == 0)
        {
            return false;
        }

        return SuggestionPhrases.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal));
    }

    private static bool StartsWithPhrase(string lowered, string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (lowered.Equals(phrase, StringComparison.Ordinal))
            {
                return true;
            }

            if (lowered.StartsWith(phrase + " ", StringComparison.Ordinal)
                || lowered.StartsWith(phrase + ",", StringComparison.Ordinal)
                || lowered.StartsWith(phrase + ".", StringComparison.Ordinal)
                || lowered.StartsWith(phrase + "!", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
