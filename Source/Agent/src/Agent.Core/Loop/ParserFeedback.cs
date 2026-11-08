namespace Agent.Core.Loop;

/// <summary>
/// What this game's parser tends to accept, and what it has already refused — <b>help text for
/// the model, never a gate</b> (§10.4).
/// <para>
/// The companion is a person at the keyboard: it types, and the game is the authority on what
/// it understands. So this is phrased as a primer — what the game calls things, what it has
/// accepted, what it has refused — and nothing here may stop an act being sent. A word missing
/// from a dictionary is not proof that the game cannot be driven with it (no dictionary holds
/// the `1` a chapter menu asks for), and a refusal covers one attempt with one wording, not the
/// intention behind it.
/// </para>
/// <list type="number">
/// <item><b>The game's own dictionary.</b> The core reads it out of the story file and sends it
/// as the <c>vocabulary</c> message: the words that parser recognises. Useful for steering away
/// from invented verbs, and nothing more.</item>
/// <item><b>The parser's refusals.</b> Anything the game refuses at runtime is remembered and
/// shown to the model, because a wording that failed once will usually fail again.</item>
/// </list>
/// <para>
/// The live run that prompted all of this: Adventure's parser is far smaller than a generic verb
/// list. The companion sent <c>follow stream</c> and <c>downstream</c> and was told <i>"That's
/// not a verb I recognise."</i> twice. Neither word is in Adventure's dictionary (786 entries) —
/// while <c>follow</c> <i>is</i> in The Witness's (711). The answer to that was to tell the model
/// what this parser knows; it was wrong to also refuse its act, and it cost a driver the ability
/// to answer a game's own question.
/// </para>
/// </summary>
public sealed class ParserFeedback
{
    /// <summary>
    /// What a refusal looks like when it is a verdict on the <b>word</b>: the parser does not
    /// hold it, so no rewording of the same verb will help.
    /// <para>
    /// Lower-cased for comparison, because the games themselves are inconsistent about case and
    /// none of them are consistent about punctuation.
    /// </para>
    /// </summary>
    private static readonly string[] WordUnknownMarkers =
    [
        "that's not a verb i recognise",
        "that's not a verb i recognize",
        "i don't know the word",
        "there is no verb",
    ];

    /// <summary>
    /// What a refusal looks like when it is about the <b>attempt</b>: the thing named is not here,
    /// the sentence did not parse, that action is not implemented. Every one of these is a fact
    /// about one wording, and not one of them is a reason to take a word off the table.
    /// <para>
    /// This list is where the harm was. Measured 2026-09-21: <c>You can't see any letter here!</c>
    /// — a remark about the letter — was read as a verdict on <c>read</c>, and over one evening
    /// <c>read</c>, <c>examine</c>, <c>take</c>, <c>put</c> and <c>down</c> were all recorded as
    /// refused, in a game whose parser holds every one of them. The words were then handed to the
    /// model as words not to use, and it worked around them for sixteen hours (02_game_boundary.md §4.3 (`G24`)).
    /// </para>
    /// </summary>
    private static readonly string[] AttemptMarkers =
    [
        "i don't know how to",
        "i don't understand that sentence",
        "i beg your pardon",
        "you can't see any",
    ];

    /// <summary>Words this game's parser has no word for, and words it has accepted.</summary>
    private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The attempts themselves, oldest first: what was typed and what came back.</summary>
    private readonly List<ParserAttempt> _attempts = [];

    /// <summary>How many attempts the note shows. Enough for a pattern, few enough to read.</summary>
    private const int AttemptsShown = 6;

    /// <summary>The game's own dictionary, when the core could read it.</summary>
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Z-characters of text a dictionary entry holds. Words are cut to this before
    /// lookup, exactly as the interpreter does, so <c>inventory</c> matches a V3
    /// game's stored <c>invent</c> rather than being wrongly blocked.
    /// </summary>
    private int _resolution;

    public IReadOnlyCollection<string> Rejected => _rejected;

    public IReadOnlyCollection<string> Accepted => _accepted;

    /// <summary>How many words the game's dictionary held, for logs and diagnostics.</summary>
    public int KnownWords => _known.Count;

    /// <summary>
    /// Take the game's dictionary as read by the core. Replaces anything learned
    /// about a previous game.
    /// </summary>
    public void SetKnownVocabulary(IEnumerable<string> words, int resolution)
    {
        _known.Clear();
        _rejected.Clear();
        _accepted.Clear();
        _resolution = resolution > 0 ? resolution : 9;

        foreach (var word in words)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                _known.Add(word.Trim().ToLowerInvariant());
            }
        }
    }

    /// <summary>Is there a dictionary to check against?</summary>
    public bool HasKnownVocabulary => _known.Count > 0;

    /// <summary>
    /// Does this game's dictionary hold <paramref name="word"/>?
    /// <para>
    /// A query for building the model's notes — <b>not</b> a licence to refuse anything. With no
    /// dictionary the answer is "yes", so a game whose words could not be read is never narrowed
    /// at all.
    /// </para>
    /// </summary>
    public bool Knows(string word)
    {
        if (_known.Count == 0)
        {
            return true;
        }

        return _known.Contains(Cut(word));
    }

    /// <summary>
    /// Cut a word the way the dictionary does: lowercase letters cost one z-character
    /// each, anything else costs two because it needs a shift. Stories are
    /// overwhelmingly lowercase, so this is exact in practice.
    /// </summary>
    private string Cut(string word)
    {
        var budget = _resolution;
        var builder = new System.Text.StringBuilder();
        foreach (var character in word.Trim().ToLowerInvariant())
        {
            var cost = character is >= 'a' and <= 'z' ? 1 : 2;
            if (cost > budget)
            {
                break;
            }

            budget -= cost;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Every attempt this session has watched, oldest first — the model's history.</summary>
    public IReadOnlyCollection<ParserAttempt> Attempts => _attempts;

    /// <summary>
    /// Does this turn's output look like the parser refusing what we sent, in either sense?
    /// </summary>
    public static bool LooksLikeRejection(string output) =>
        Matches(WordUnknownMarkers, output) || Matches(AttemptMarkers, output);

    /// <summary>
    /// Is this refusal a verdict on the word rather than on the attempt? Only these are worth
    /// remembering as "this parser has no word for that"; everything else is one attempt at one
    /// wording, kept as history for the model to reason from (02_game_boundary.md §4.3 (`G24`)).
    /// </summary>
    public static bool LooksLikeWordUnknown(string output) => Matches(WordUnknownMarkers, output);

    private static bool Matches(string[] markers, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var lower = output.ToLowerInvariant();
        return markers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The attempt this output represents: the line the companion typed, and the line the game
    /// answered it with. The answer is the <em>sentence</em> rather than the whole screen — a
    /// refusal is one line, and a transcript in the prompt is noise.
    /// </summary>
    public static ParserAttempt TheAttempt(string typed, string? output, bool wordUnknown)
    {
        var markers = wordUnknown ? WordUnknownMarkers : AttemptMarkers;
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var lower = trimmed.ToLowerInvariant();
            if (markers.Any(marker => lower.Contains(marker, StringComparison.Ordinal)))
            {
                return new ParserAttempt(
                    (typed ?? string.Empty).Trim(),
                    trimmed.Length > 200 ? trimmed[..200] : trimmed,
                    wordUnknown);
            }
        }

        return new ParserAttempt((typed ?? string.Empty).Trim(), string.Empty, wordUnknown);
    }

    /// <summary>
    /// Record one attempt: what was typed, whether the game had a word for it, and the sentence it
    /// answered with. True when the attempt is news, which is the signal the vocabulary store
    /// writes on (03_session.md §9–§10 (`S58`–`S76`)).
    /// <para>
    /// The attempt is kept either way. A refusal is a fact about one wording, and one that is not
    /// written down is one the model will re-derive wrongly — or, worse, be told to avoid a word
    /// the game accepts.
    /// </para>
    /// </summary>
    public bool NoteAttempt(ParserAttempt attempt)
    {
        var isNews = true;
        if (attempt.Typed.Length > 0)
        {
            isNews = !_attempts.Any(seen =>
                string.Equals(seen.Typed, attempt.Typed, StringComparison.OrdinalIgnoreCase));
            _attempts.RemoveAll(seen =>
                string.Equals(seen.Typed, attempt.Typed, StringComparison.OrdinalIgnoreCase));
            _attempts.Add(attempt);
            while (_attempts.Count > AttemptsShown)
            {
                _attempts.RemoveAt(0);
            }
        }

        if (attempt.WordUnknown && (NamedWord(attempt.GameSaid) ?? Verb(attempt.Typed)) is { } verb)
        {
            _accepted.Remove(verb);
            _rejected.Add(verb);
        }

        return isNews;
    }

    /// <summary>
    /// The word a word-unknown verdict is <em>about</em>.
    /// <para>
    /// The game names it: <c>[I don't know the word "doors."]</c>. Reading the verdict against the
    /// first word of the line instead blames the verb for the noun, and the cost is not
    /// theoretical — measured 2026-09-25, one evening of Moonmist put five good verbs on the list of
    /// words this parser has no word for:
    /// </para>
    /// <list type="bullet">
    /// <item><c>examine sliding doors</c> → <c>examine</c></item>
    /// <item><c>take reading lamp</c> → <c>take</c></item>
    /// <item><c>open boot</c> → <c>open</c></item>
    /// <item><c>ask vivien about wrendish</c> → <c>ask</c></item>
    /// <item><c>say Just visiting, Doctor.</c> → <c>say</c></item>
    /// </list>
    /// <para>
    /// The list is rebuilt from the store at every load, so the damage persists and grows: 32 of
    /// that evening's 295 thinking blocks were spent arguing with it. A verdict that names nothing
    /// (<c>That's not a verb I recognise.</c>) is still a verdict on something, and the caller falls
    /// back to the line's own verb for it.
    /// </para>
    /// </summary>
    public static string? NamedWord(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        // Both quote styles: the games are not consistent, and a story that says
        // `I don't know the word 'xyz'` is naming the same word as one that uses double quotes.
        foreach (var quote in new[] { '"', '\'' })
        {
            var open = output.IndexOf(quote);
            if (open < 0)
            {
                continue;
            }

            var close = output.IndexOf(quote, open + 1);
            if (close < 0)
            {
                continue;
            }

            // Deliberately narrow: the game quotes one word, and a quoted *phrase* is not a
            // dictionary entry — `Verb` takes its first word and trims the punctuation off it,
            // which is exactly right for `"just."` and wrong for a whole sentence in quotes.
            // A pair that holds a phrase is the only pair worth reading: anything after it is
            // prose, and an apostrophe in `don't` must never be mistaken for a quoted word.
            var quoted = output[(open + 1)..close];
            if (quoted.Any(char.IsWhiteSpace) || quoted.Length > 24)
            {
                return null;
            }

            return Verb(quoted);
        }

        return null;
    }

    /// <summary>
    /// The verb of a command line: its first word, stripped of punctuation, lower-cased.
    /// <para>
    /// Only the verb is tracked. A parser refusing <c>take lamp</c> is refusing
    /// <c>take</c>, and guessing which of the nouns it disliked would be inventing
    /// information the game did not give us.
    /// </para>
    /// </summary>
    public static string? Verb(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var first = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var trimmed = first.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')');
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Record that this parser has no word for <paramref name="verb"/>. True when the verdict is
    /// news — which is the signal the vocabulary store uses to write it down once (03_session.md §9–§10 (`S58`–`S76`)).
    /// <para>
    /// Only a word-unknown verdict reaches this. Everything else is an attempt, and goes to
    /// <see cref="NoteAttempt"/>.
    /// </para>
    /// </summary>
    public bool NoteRejected(string verb)
    {
        _accepted.Remove(verb);
        return _rejected.Add(verb);
    }

    /// <summary>Record an acceptance. True when the word was not already known to be accepted.</summary>
    public bool NoteAccepted(string verb)
    {
        return !_rejected.Contains(verb) && _accepted.Add(verb);
    }

    /// <summary>Forget everything: used when the game changes, since parsers differ.</summary>
    public void Clear()
    {
        _known.Clear();
        _rejected.Clear();
        _accepted.Clear();
    }

    /// <summary>
    /// The line handed to the model with every call, or <c>null</c> when there is
    /// nothing to say yet.
    /// <para>
    /// It is deliberately phrased as the game's own verdict rather than as an order,
    /// and it names the verbs that have worked, so the model is not merely forbidden
    /// but redirected.
    /// </para>
    /// </summary>
    public string? ContextNote()
    {
        if (_known.Count == 0 && _rejected.Count == 0 && _accepted.Count == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("## This game's parser\n\n");

        if (_known.Count > 0)
        {
            // The game's dictionary is mostly nouns, and a V3 game stores them
            // truncated ("invent" for "inventory"), so listing it raw would be a
            // puzzle. Instead the companion offers the action words it would
            // plausibly reach for, filtered by what this game actually accepts —
            // in the spelling the player would type, not the stored fragment.
            //
            // And filtered again by what this parser has already refused. The two lists are built
            // from different things — the dictionary, and the verdicts met in play — so they can
            // disagree, and help text that says "it accepts open … it has no word for open" in one
            // breath is worth less than no help at all (measured 2026-09-25: four verbs, and the
            // companion stopped to reason about the contradiction thirty-two times in one evening).
            var usable = Vocabulary.ActionWords
                .Where(Knows)
                .Where(word => !_rejected.Contains(word))
                .OrderBy(word => word, StringComparer.Ordinal)
                .Take(80)
                .ToList();

            builder
                .Append($"The game's parser knows {_known.Count} words. Of the actions you might take, it accepts: ")
                .AppendJoin(", ", usable)
                .Append(".\n");
        }

        if (_rejected.Count > 0)
        {
            // A word this parser has no word for is worth steering away from, and this is all it
            // says. It is deliberately not a list of forbidden words: nothing here may remove a
            // word from what the companion can type (02_game_boundary.md §4.3 (`G24`)).
            builder
                .Append("This parser has no word for: ")
                .AppendJoin(", ", _rejected.OrderBy(word => word, StringComparer.Ordinal))
                .Append(".\n");
        }

        if (_attempts.Count > 0)
        {
            builder.Append(
                "What you have already typed, and what came back. This is history, not a rule: a "
                + "refusal is one attempt at one wording, and the game will take the same verb "
                + "again whenever the thing you name is there.\n");
            foreach (var attempt in _attempts)
            {
                builder.Append("- `").Append(attempt.Typed).Append("` → ");
                builder
                    .Append(attempt.GameSaid.Length == 0 ? "(nothing came back)" : attempt.GameSaid)
                    .Append('\n');
            }
        }

        if (_accepted.Count > 0)
        {
            builder
                .Append("It has accepted: ")
                .AppendJoin(", ", _accepted.OrderBy(word => word, StringComparer.Ordinal))
                .Append(".\n");
        }

        builder.Append(
            "That is help, not a limit: you may type anything, and the game will say if it does "
            + "not understand you. Never invent a verb to express an intention — if nothing above "
            + "fits, prefer a plain compass direction, or answer the game if it is asking you "
            + "something, or say so and ask.\n");

        return builder.ToString();
    }
}
