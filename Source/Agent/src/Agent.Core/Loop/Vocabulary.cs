namespace Agent.Core.Loop;

/// <summary>
/// The small built-in vocabulary behind rules-first classification (§10.3) and the
/// stakes check (§10.4 rule 3).
/// <para>
/// This list exists so that the common case costs nothing: a bare game command is
/// recognised without a model call, which is what makes "playing through the
/// companion" feel like direct play for short commands.
/// </para>
/// </summary>
public static class Vocabulary
{
    /// <summary>Compass directions and their abbreviations.</summary>
    public static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "north", "south", "east", "west", "up", "down",
        "northeast", "northwest", "southeast", "southwest",
        "ne", "nw", "se", "sw", "n", "s", "e", "w", "u", "d",
        "in", "out", "inside", "outside", "enter", "exit",
    };

    /// <summary>Everyday adventure verbs that are safe to pass straight through.</summary>
    public static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "look", "l", "examine", "x", "inspect", "inventory", "i",
        "go", "walk", "run", "wait", "z",
        "take", "get", "grab", "drop", "put", "insert",
        "open", "close", "read", "listen", "smell", "search", "find",
        "turn", "push", "pull", "press", "move",
        "wear", "remove", "eat", "drink", "taste", "touch", "rub", "feel",
        "wait", "z", "again", "g", "score", "verbose", "brief", "superbrief",
        "help", "version", "save", "restore", "quit", "restart",
        "climb", "jump", "swim", "leave", "board", "tie", "untie",
        "light", "burn", "ignite", "blow", "wave", "shake", "throw",
        "attack", "kill", "hit", "strike", "give", "show", "ask", "tell", "say",
        "pray", "sing", "dance", "sleep", "wake", "fill", "empty", "pour", "dig",
        "count", "knock", "break", "smash", "cut", "buy", "sell", "pay", "bribe",
        "steal", "lock", "unlock", "offer", "unlock", "tie", "follow", "push",
        "wear", "polish", "clean", "wipe", "kiss", "pet", "ride", "row", "sail",
    };

    /// <summary>Single letters that are game commands rather than conversation.</summary>
    public static readonly HashSet<string> SingleLetterCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "s", "e", "w", "u", "d", "l", "x", "i", "g", "z",
    };

    /// <summary>
    /// Actions with real consequences: destructive, irreversible, expensive, or
    /// socially costly. These require the player's blessing first (§10.4).
    /// <para>
    /// Membership is about *consequence*, not grammar — <c>open mailbox</c> and
    /// <c>take lamp</c> are ordinary, while <c>burn candle</c>, <c>bribe troll</c>
    /// and <c>press the red button</c> are not. Over-flagging makes the companion
    /// tiresome; under-flagging makes it reckless, so this list errs slightly
    /// towards asking.
    /// </para>
    /// <para>
    /// It used to err a great deal further than slightly (issue #29): <c>push</c>,
    /// <c>pull</c>, <c>press</c>, <c>climb</c>, <c>jump</c>, <c>drop</c>, <c>fill</c> and
    /// <c>rub</c> were all on it, and those are not consequences — they are how one plays a
    /// text adventure. Every one of them bought the player a consent prompt for something
    /// they had already asked for, which is the mechanical half of "shall I do this or
    /// that?". What is left is spending, breaking, destroying, attacking, offering, and
    /// things that cannot be undone.
    /// </para>
    /// <para>
    /// <c>light</c> and <c>ignite</c> came off it after the same idea bit again (issue #23):
    /// the companion proposed <c>light lamp</c> — the one action every one of these stories
    /// requires before it will let you go anywhere interesting — and then sat waiting for a
    /// grant from an empty room, in the dark, for ever. A verb that names the *necessary*
    /// move is not a risk, whatever it does to the object. <c>burn</c> stays: setting fire to
    /// something is destruction, and destruction is what this list is for.
    /// </para>
    /// </summary>
    public static readonly HashSet<string> RiskyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "attack", "kill", "hit", "strike", "punch", "stab", "shoot", "throw",
        "burn", "break", "smash", "cut", "destroy",
        "drink", "eat", "taste", "give", "pay", "bribe", "offer", "sell", "buy",
        "pour", "steal",
    };

    /// <summary>
    /// Acts that can always be done without asking: looking, reading, listening — and moving.
    /// <para>
    /// A wrong look costs nothing and a wrong step costs a turn, and neither is worth stopping
    /// a friend over: a gate that asks before travel is a gate that asks about every footstep,
    /// which is the failure the <see cref="RiskyVerbs"/> docstring describes from the other
    /// side.
    /// </para>
    /// </summary>
    public static readonly HashSet<string> SafeVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "look", "l", "examine", "x", "inspect", "inventory", "i", "read", "listen",
        "smell", "search", "find", "wait", "z", "again", "g", "score",
        "verbose", "brief", "superbrief", "help", "version",
        "go", "walk", "run",
    };

    /// <summary>Words that make a line conversational rather than a command.</summary>
    public static readonly HashSet<string> ConversationalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "we", "us", "our", "let's", "lets", "should", "could", "would", "can",
        "maybe", "perhaps", "please", "if", "about", "why", "how", "what", "do",
        "you", "your", "me", "my", "i", "i'm", "im", "think", "wonder", "suppose",
        "together", "shall", "might", "want", "like", "need", "try",
        // Clause words: a sentence with a subordinate clause is a person talking, and no
        // parser will accept it. Measured: "ask for a description" was sent to the game.
        "then", "whether", "because", "while", "until", "before", "after", "when",
    };

    /// <summary>Short acknowledgements that approve a pending proposal (§10.3 grant).</summary>
    public static readonly string[] GrantPhrases =
    [
        "yes", "yeah", "yep", "y", "ok", "okay", "k", "sure", "go on", "go on then",
        "do it", "try it", "fine", "go ahead", "please do", "i agree", "agreed",
        "sounds good", "lets do it", "let's do it", "make it so", "aye", "carry on",
    ];

    /// <summary>Short refusals that cancel a pending action (§10.3 veto).</summary>
    public static readonly string[] VetoPhrases =
    [
        "no", "nope", "nah", "wait", "stop", "don't", "dont", "hold on", "not yet",
        "cancel", "leave it", "no thanks", "no thank you", "forget it", "abort",
        "hang on", "never mind", "nevermind", "no way",
    ];

    /// <summary>Conversational openers that mean a plan rather than a command (§10.3 steer).</summary>
    public static readonly string[] SteerPhrases =
    [
        "can we", "could we", "shall we", "should we", "let's", "lets", "let us",
        "i want to", "i'd like to", "id like to", "we should", "we could",
        "how about", "what about", "what if", "maybe we", "why don't we", "why dont we",
        "try to", "try and", "do you want to", "i think we", "i reckon",
        "any chance we", "shall i", "should i", "want to",
    ];

    /// <summary>
    /// Ways of giving an order that are phrased as a request to somebody (§10.3 steer).
    /// <para>
    /// The same shape as <see cref="SteerPhrases"/> and deliberately separate from
    /// <see cref="QuestionPhrases"/>, which is where three of them used to live: `can you get us back
    /// to our room?` and `can we take a gun` are one shape, and while the rules disagreed the question
    /// mark settled it — which is how the most important instruction of 2026-09-29 was filed as chat.
    /// </para>
    /// </summary>
    public static readonly string[] DelegationPhrases =
    [
        "can you", "could you", "would you", "will you", "can we", "could we",
        "i need you to", "i want you to", "i'd like you to", "id like you to",
    ];

    /// <summary>
    /// Phrases that mark a **brief** — an instruction too long to be a command and too much of a job to
    /// be a remark (§10.3, and the defect `C47.3` names).
    /// <para>
    /// A phrase table rather than a shape test, and that is the whole decision. The first attempt was
    /// *"long, names a thing to do, asks nothing ⇒ a brief"*, and it read the two instructions of
    /// 2026-09-29 correctly while also reading two lines of their own UATs as briefs — `wait - the trun
    /// is rotten to the core.  whats inside` and `I remember playing this as a kid`, both of which do
    /// name a thing to do. Those two carry none of these phrases. What separates a brief from a remark
    /// is a reading, and a reading that has to be argued from evidence is a list.
    /// </para>
    /// </summary>
    public static readonly string[] BriefPhrases =
    [
        "the new job", "this is the job",
        "get us back", "get me back", "take us back", "take me back",
        "see what we can", "see what you can",
        "stop if", "but stop if",
    ];

    /// <summary>Question openers (§10.3 question).</summary>
    public static readonly string[] QuestionPhrases =    [
        "what", "where", "how", "did", "is", "are", "do you", "does", "why", "who",
        "when", "can you", "could you", "would you", "have we", "has it", "was",
        "were", "which", "remember", "any idea",
    ];

    /// <summary>Is the first word a game verb or direction?</summary>
    public static bool StartsWithCommandWord(string word) =>
        Directions.Contains(word) || Verbs.Contains(word);

    /// <summary>
    /// Every word this companion might plausibly issue as a command, sorted, for
    /// intersecting with a game's own dictionary (§10.4).
    /// <para>
    /// This is the generic list, not the game's: the point of the intersection is to
    /// find out which of these a particular parser will actually accept.
    /// </para>
    /// </summary>
    public static IEnumerable<string> ActionWords =>
        Directions.Concat(Verbs).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(word => word, StringComparer.Ordinal);

    /// <summary>
    /// Does this line look like one complete game command?
    /// </summary>
    /// <param name="maxCommandWords">
    /// The longest line that may still be a command; <c>max_command_words</c> in configuration,
    /// passed in by the caller so the loop's own bound is the one in force.
    /// </param>
    public static bool IsBareCommand(
        string line,
        out string reason,
        Func<string, bool>? knowsWord = null,
        int maxCommandWords = MaxCommandWords)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            reason = "empty";
            return false;
        }

        // Longest a command gets in practice. "unlock the grate with the keys" is six; a
        // sentence that happens to open with a verb runs to ten or more (issue #40).
        if (words.Length > maxCommandWords)
        {
            reason = "too long for a command";
            return false;
        }

        if (ReadsAsProse(line))
        {
            reason = "reads as a sentence";
            return false;
        }

        if (words.Any(word => ConversationalMarkers.Contains(word)))
        {
            reason = "conversational marker";
            return false;
        }

        if (words.Any(word => ProseParticles.Contains(Bare(word))))
        {
            reason = "prose word";
            return false;
        }

        var first = Bare(words[0]);
        if (SingleLetterCommands.Contains(first) && words.Length == 1)
        {
            reason = "single-letter command";
            return true;
        }

        // The story's own dictionary is the strongest signal there is, and the only one that
        // speaks the story's language rather than this file's (§4.17): "every word in the line
        // is one the parser knows" is a command in a French story exactly as it is in an
        // English one, and the opener list below has never heard of `prends`. It is tried
        // first for that reason. The sentence filters above still come first, so prose is
        // demoted to the model whatever language it is written in.
        if (knowsWord is not null && words.All(word => knowsWord(Bare(word))))
        {
            reason = "every word is in the story's dictionary";
            return true;
        }

        if (!StartsWithCommandWord(first))
        {
            reason = "unrecognised opener";
            return false;
        }

        // A line that opens the way this file expects, holding a word the story has never
        // heard, is somebody talking: a story only understands its own words. The dictionary
        // is optional because the core may not have managed to read one, and an absent
        // dictionary must not turn commands into conversation.
        if (knowsWord is not null && !words.All(word => knowsWord(Bare(word))))
        {
            reason = "a word the story does not know";
            return false;
        }

        reason = "recognised game verb";
        return true;
    }

    /// <summary>
    /// The longest line that may still be a single game command.
    /// <para>
    /// The value in force is <c>max_command_words</c> in <c>agent-config.json</c>; this is the
    /// shipped default and what a caller that does not say gets.
    /// </para>
    /// </summary>
    public const int MaxCommandWords = Config.ConfigDefaults.MaxCommandWords;

    /// <summary>
    /// Words that cannot be part of a command for any story and that people type constantly
    /// when they are talking. Without these, any sentence opening with a verb the medium
    /// uses is a command: measured, <c>wait - the trun is rotten to the core.  whats inside
    /// the trunk</c> was typed into the story because it begins with <c>wait</c> (issue #40).
    /// <para>
    /// Deliberately small and unarguable. Words a parser could legitimately see — <c>the</c>,
    /// <c>in</c>, <c>it</c>, <c>that</c>, <c>there</c> — are not here, because a false
    /// positive silences a real command.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ProseParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "were", "am", "be", "been",
        "isnt", "arent", "wasnt", "werent", "dont", "doesnt", "didnt",
        "cant", "couldnt", "wont", "wouldnt", "shouldnt", "havent", "hasnt",
        "whats", "thats", "theres", "im", "ive", "youre", "theyre", "hes", "shes",
        "hmm", "erm", "eh", "okay", "ok",
    };

    /// <summary>
    /// Punctuation no parser command carries, and every one of them is how a person writes.
    /// </summary>
    private static bool ReadsAsProse(string line)
    {
        if (line.Contains('?'))
        {
            return true;
        }

        if (line.Contains(" -", StringComparison.Ordinal)
            || line.Contains("- ", StringComparison.Ordinal)
            || line.Contains(',', StringComparison.Ordinal)
            || line.Contains(';')
            || line.Contains(':')
            || line.Contains('!'))
        {
            return true;
        }

        // A full stop with words after it means there was a sentence before them.
        return line.Contains(". ", StringComparison.Ordinal);
    }

    /// <summary>Strip punctuation a player may have left attached to a word.</summary>
    private static string Bare(string word) =>
        word.Trim('.', ',', '?', '!', ';', ':', '"', '\'');

    /// <summary>
    /// Is this word one that cannot be part of a command for any story, and that people type
    /// constantly when they are talking?
    /// </summary>
    public static bool IsProseWord(string word) => ProseParticles.Contains(Bare(word));

    /// <summary>
    /// Was this refusal a near miss — a line with the shape of a command that was turned
    /// down — rather than prose that was never one?
    /// <para>
    /// The distinction is only for the log. A command the story cannot understand becomes a
    /// sentence to the companion, and from the outside that is indistinguishable from the
    /// player simply talking: the reason is the one trace that it happened at all, so it is
    /// worth carrying rather than flattening to "conversation" (issue #40).
    /// </para>
    /// </summary>
    public static bool WasCommandShaped(string reason) => reason is
        "too long for a command" or "reads as a sentence" or "prose word"
        or "a word the story does not know";

    /// <summary>
    /// Was this read an accepted command — the reasons <see cref="IsBareCommand"/> gives when it
    /// says yes?
    /// <para>
    /// Those lines no longer go to the parser on their own (the `.`/`/direct` form is the player's
    /// declared route to the game), so the reason is what lets the read-back name the line for
    /// what it is rather than filing it as prose.
    /// </para>
    /// </summary>
    public static bool WasACommand(string reason) => reason is
        "single-letter command" or "recognised game verb"
        or "every word is in the story's dictionary";

    /// <summary>
    /// Is this line a command for the interpreter rather than for the story?
    /// <para>
    /// dfrotz intercepts anything beginning with a backslash before the game sees it, and does
    /// the interface's bidding with it: `\N` restarts the story behind the core's checkpoint,
    /// `\X` quits the interpreter, and `\R` and `\P` record and replay a command file — the
    /// walkthrough in disguise this design already refuses by name, just under a prefix that
    /// turned out not to exist. The core refuses these too, at the one place that writes into
    /// frotz; the rule lives here as well so the companion is *told* rather than left waiting
    /// for a turn that is never coming.
    /// </para>
    /// </summary>
    public static bool IsInterpreterLine(string action) =>
        action.TrimStart().StartsWith('\\');

    /// <summary>
    /// The class of an act: what it costs the player if it is wrong (§4.7).
    /// <para>
    /// Three classes and no fourth. <b>Low</b> is looking and walking. <b>Medium</b> is
    /// handling things — reversible in every story this design cares about. <b>High</b> is
    /// the small set of acts nobody can take back: spending, breaking, burning, giving away.
    /// The player's standing danger level is compared against this, and the proposal they
    /// read names the class of the act it asks about, so "risky" is a measurement rather
    /// than an adjective.
    /// </para>
    /// </summary>
    public static RiskLevel RiskOf(string action)
    {
        var words = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return RiskLevel.Low;
        }

        // The verb may be preceded by a qualifier such as "quietly".
        if (words.Take(2).Any(word => RiskyVerbs.Contains(word.Trim(',', '.'))))
        {
            return RiskLevel.High;
        }

        var first = words[0].Trim(',', '.');
        return Directions.Contains(first) || SafeVerbs.Contains(first)
            ? RiskLevel.Low
            : RiskLevel.Medium;
    }

    /// <summary>Does this action need the player's blessing before it happens? (§10.4)</summary>
    public static bool IsRiskyAction(string action) => RiskOf(action) == RiskLevel.High;
}
