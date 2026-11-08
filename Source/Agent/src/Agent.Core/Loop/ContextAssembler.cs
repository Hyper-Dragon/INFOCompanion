using Agent.Core.Config;
using Agent.Llm;
using Agent.Notebook;

namespace Agent.Core.Loop;

/// <summary>Who produced a transcript line.</summary>
public enum TranscriptRole
{
    Player,
    Game,
    Companion,
}

/// <summary>
/// One thing that happened, kept verbatim.
/// <para>
/// <paramref name="ByCompanion"/> is set on a game turn whose input line the companion itself
/// typed. It is kept because the transcript is cleared on a fork, and a companion reading its own
/// `restore` back as "the game was asked" is how it came to tell the player it had not restored
/// when it had (measured 2026-10-05).
/// </para>
/// </summary>
public sealed record TranscriptItem(
    TranscriptRole Role,
    string Text,
    string? Input = null,
    int Turn = 0,
    DateTimeOffset? At = null,
    bool ByCompanion = false);

/// <summary>
/// The recent history, kept **verbatim** (§10.2).
/// <para>
/// Nothing here is summarised, ever: summaries lose the troll's exact words, which
/// are usually the thing that matters. Long-horizon memory is the notebook's job,
/// and that is precisely why this window can stay small and lossless.
/// </para>
/// </summary>
public sealed class Transcript
{
    private readonly List<TranscriptItem> _items = [];

    public IReadOnlyList<TranscriptItem> Items => _items;

    public int Count => _items.Count;

    public void Add(TranscriptItem item) => _items.Add(item);

    public void AddPlayerLine(string text) => Add(new TranscriptItem(TranscriptRole.Player, text, At: DateTimeOffset.UtcNow));

    public void AddGameTurn(string input, string output, int turn, bool byCompanion) =>
        Add(new TranscriptItem(TranscriptRole.Game, output, input, turn, DateTimeOffset.UtcNow, byCompanion));

    public void AddCompanion(string text) => Add(new TranscriptItem(TranscriptRole.Companion, text, At: DateTimeOffset.UtcNow));

    /// <summary>Replay the tail the core sent in `welcome` (§6.3).</summary>
    public void SeedFromWelcome(IEnumerable<string>? tail)
    {
        if (tail is null)
        {
            return;
        }

        foreach (var entry in tail)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            // The core sends each turn pre-formatted as "> input\noutput".
            var split = entry.IndexOf('\n');
            if (split > 0 && entry.StartsWith("> ", StringComparison.Ordinal))
            {
                Add(new TranscriptItem(
                    TranscriptRole.Game,
                    entry[(split + 1)..],
                    entry[2..split],
                    At: null));
            }
            else
            {
                Add(new TranscriptItem(TranscriptRole.Game, entry, At: null));
            }
        }
    }

    public void Clear() => _items.Clear();
}

/// <summary>The message list for one model call, with its cost in context.</summary>
public sealed record AssembledContext(
    IReadOnlyList<ChatMessage> Messages,
    int EstimatedTokens,
    int FixedTokens,
    int ItemsIncluded,
    int ItemsDropped,
    bool OverBudget,
    bool OverFixedBudget);

/// <summary>
/// Builds every model call deterministically (§10.2):
/// system prompt → notebook → recent transcript, verbatim → latest event.
/// <para>
/// Truncation drops the oldest items first and keeps the notebook always, because
/// the notebook is memory and the transcript is only a window. If assembly cannot
/// fit the budget, that is reported rather than hidden — the caller logs it loudly.
/// </para>
/// </summary>
public sealed class ContextAssembler
{
    /// <summary>A short, machine-generated reminder of the two channels (04_companion.md §2 (`C6`–`C13`)).</summary>
    private const string ContractAppendix = """

        ## How you answer

        There are two channels, and they are different jobs.

        **What you say** is the text of your reply. It is plain speech, and it is the only thing
        your friend hears as words. No tags, no markers, no headings, no section names — just
        what you would say out loud. It can be as long as it deserves to be, and a paragraph is
        fine; it is a conversation, not a caption.

        **What you do** is a tool call: `type` to put one line into the game, `press_key` when the
        game is waiting for a keystroke rather than a command, `ask` when you are putting a
        question to your friend and waiting on the answer, `errand` when they have handed you a
        job and you are taking it on, `write_notes` and `write_memories` to rewrite your memory.

        You do not label any of this. There is nothing to mark up and nothing to remember to
        close: the reasoning is read from your thinking directly and shown to your friend as
        your thinking, and your speech is whatever you wrote.

        ## Who you are talking to

        You are in the room with your friend: two people, one screen, one keyboard between you,
        playing a game the way these were played before anybody could look anything up. That is
        the whole of the frame. You are not in the game, and you are not pretending to be in
        1983 — you are two people in the room tonight, and the game is what is on the screen.
        Talk about anything: the game, the day, what is happening tomorrow. If your friend talks
        to you in another language, talk back in theirs; the game has its own language, and that
        one is the game's business.

        Your friend reads everything you think as well as everything you say, so:

        - It is **you** and **we**. Never "the player", never "they", never "the user".
        - Never describe your own role or your own process. "Let me report to the player",
          "I'll suggest", "let me take initiative on something low-risk" is a machine
          describing itself. That you are choosing what to do next is not the interesting
          part; the thought is.
        - Think in the voice you would say it in. Dead ends, doubts and second thoughts belong
          in it: "not productive", "hang on", "actually —" are what thinking sounds like.
        - Thinking and speech are different jobs. The thinking is the working; the speech is
          what you would actually say out loud. Do not make one a copy of the other.
        - **What is said in the room is not evidence about the game.** A joke about the box it
          came in, the friend who lent it to you, the state of the evening, what either of you
          is doing tomorrow — that is the two of you talking. Answer it as yourself, enjoy it,
          and let it go. Do not fold it into what the game means and do not go looking for it
          in the story.

        Rules for the move:
        - one `type` call per turn, and it is a single plain game command line;
        - when the game is not asking for a command at all — a menu, a `[MORE]`, a `(press any
          key)` — answer it with `press_key`, not `type`. Nothing else will move it, and the
          game will sit there for ever;
        - **a call is the only thing that moves.** "West it is" is a decision, not a move:
          without the call in the same reply nothing happens at all, and your friend is left
          waiting on a turn that is never coming. The harness cannot tell a decision from an
          announcement — only the call is a move — so if you have decided, call it. If you have
          not decided, say what you are thinking and do not say that you are going;
        - if your friend tells you that you did not do something, check what actually happened
          before agreeing — but if they are right, the correction is a call, not an apology;
        - a call is a question to the game, and the turn that follows is its answer. A line that
          produced no answer was not sent: the harness will have told you why, so read what it
          said before typing the same thing again;
        - the screen shows your friend what you did, so you do not have to announce every move:
          a sentence of play-by-play under each command is a caption, not company, and your
          thinking is already theirs to read. Say something when there is something to say —
          what you found, what you are trying, what you would rather not do — and say what you
          are doing before anything risky or irreversible;
        - no move at all when your friend has just vetoed one;
        - the line must be something this game understands. Usually that is a compass direction
          or a verb-and-noun pair, but when the game asks you something directly — which
          chapter, which of two doors, "are you sure?" — the answer *is* the line, typed
          exactly as you would type it ("1", "y", "east"). Never invent a verb to express an
          intention: if nothing fits, say so and ask, or pick a direction;
        - when something needs your friend's blessing — risky, irreversible, or expensive — say
          what you are proposing and why in your own words, and make the `type` call you would
          make. Do not write "shall I?" yourself: if the harness needs to ask, it will, and two
          questions about the same thing reads as a machine;
        - when the decision is genuinely theirs and you want to stop and wait, that is what
          `ask` is for. It stops your clock; asking in prose does not;
        - `write_notes` and `write_memories` take the *whole* file, not a patch — whatever you
          leave out is gone. Leave them out entirely when nothing has changed; if your notes are
          long, write one section at a time with `update_notes` instead, because a reply that is
          cut off by the length limit writes nothing at all.

        """;

    private readonly AgentConfig _config;
    private readonly string _systemPrompt;

    public ContextAssembler(AgentConfig config, string systemPrompt)
    {
        _config = config;
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// What the companion is called. Stated by the harness, which takes it from the personality
    /// file, so the personality never has to hardcode its own name.
    /// </summary>
    public string AgentName { get; set; } = "companion";

    /// <summary>
    /// What the companion's friend is called. Also stated here rather than in the persona, for
    /// the same reason: renaming the player is one line of configuration, not a rewrite of the
    /// character.
    /// </summary>
    public string PlayerName { get; set; } = "your friend";

    /// <summary>
    /// The companion's own pronouns, stated by the harness rather than guessed at by the model.
    ///
    /// <para>
    /// Empty means "say nothing about it", which is the right default — a prompt that names no
    /// pronoun gives the model nothing to get wrong, and a character that has not declared one has
    /// not declared one.
    /// </para>
    /// </summary>
    public string AgentPronouns { get; set; } = string.Empty;

    /// <summary>
    /// The pronouns of the person at the keyboard. Configuration, on purpose.
    /// <para>
    /// Nobody should have to write a character file to stop an AI guessing at their gender, and
    /// the model guessing from a name is exactly the failure this prevents — "Peter" is not
    /// evidence of anything. Empty stays empty; the identity block then says to use their name.
    /// </para>
    /// </summary>
    public string PlayerPronouns { get; set; } = string.Empty;

    /// <summary>The system prompt actually in use, for logs and diagnostics.</summary>
    public string SystemPrompt => _systemPrompt;

    /// <param name="movements">
    /// What the core watched the player move between (issue #22). Passed only when the
    /// companion is working out where it is: it is bulky, and every other reply does not
    /// need it.
    /// </param>
    /// <param name="constraint">
    /// What is true of *this* reply — the parser's refusals, and whether the turn is the
    /// companion's to move in.
    /// <para>
    /// It goes at the end of the latest user message rather than into the system prompt, and the
    /// position is the whole point. It used to be appended to the system role, which is where
    /// standing rules live — so "this turn is yours to talk in, not to move in" read as a law of
    /// the world rather than a fact about one reply. The model said so out loud in a live
    /// session, quoting it back as "the system prompt", and then obeyed it two turns later while
    /// a different brief was asking it to move. A turn-scoped instruction has to arrive with the
    /// turn.
    /// </para>
    /// <para>
    /// It is also better for the prompt cache: the system message is now byte-identical from one
    /// call to the next, and only the part that changes anyway carries the change.
    /// </para>
    /// </param>
    public AssembledContext Assemble(
        string notebook,
        IReadOnlyList<TranscriptItem> history,
        string latestEvent,
        string? constraint = null,
        string? situation = null,
        string? memories = null,
        string? engineNotes = null,
        string? movements = null,
        string? gameBrief = null,
        bool gameBriefIsStanding = false,
        string? unresolvedPlaces = null,
        string? beliefs = null)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                  IdentityBlock()
                + _systemPrompt
                + ContractAppendix
                + GameBriefBlock(gameBrief, gameBriefIsStanding)
                + NotebookBlock(notebook, unresolvedPlaces)
            + BeliefsBlock(beliefs)
                + MemoryBlock(memories)
                + EngineBlock(engineNotes)
                + MovementBlock(movements)),
        };

        var fixedTokens = Estimate(messages[0].Content);

        // The floor, by category rather than by recency (05_memory.md §2 (`M9`–`M11`)): every player line
        // ever said, the last few of the companion's own lines, and the most recent turns.
        // These are never dropped — a companion that loses the conversation cannot play —
        // and the budget applies only to what is left over.
        var floor = FloorIndexes(history);
        var kept = new List<int>();
        for (var index = 0; index < history.Count; index++)
        {
            if (index >= history.Count - _config.TranscriptTurns || floor.Contains(index))
            {
                kept.Add(index);
            }
        }

        var dropped = history.Count - kept.Count;
        var encoded = kept
            .Select(index => (Message: Encode(history[index]), Floor: floor.Contains(index)))
            .ToList();

        // Give back the oldest that is not floor until the budget is met. The floor alone
        // may exceed the budget; when it does, the floor wins and the log says so.
        while (true)
        {
            if (encoded.Sum(item => Estimate(item.Message.Content)) <= _config.TranscriptTokenBudget)
            {
                break;
            }

            var victim = encoded.FindIndex(item => !item.Floor);
            if (victim < 0)
            {
                break;
            }

            encoded.RemoveAt(victim);
            dropped++;
        }

        var historyTokens = encoded.Sum(item => Estimate(item.Message.Content));
        var overBudget = historyTokens > _config.TranscriptTokenBudget;

        messages.AddRange(encoded.Select(item => item.Message));

        // Say so when the view was clipped. Silent trimming is how doubt turns inward: the
        // companion could not tell a hole in its own memory from a hole in the world.
        var latest = ChatMessage.User(
            latestEvent
            + SituationBlock(situation)
            + ConstraintBlock(constraint)
            + ClippingNote(dropped));

        var estimated = fixedTokens + historyTokens + Estimate(latest.Content);
        messages.Add(latest);

        return new AssembledContext(
            messages,
            estimated,
            fixedTokens,
            encoded.Count,
            dropped,
            overBudget,
            fixedTokens > _config.FixedTokenBudget);
    }

    /// <summary>
    /// The indexes that are never dropped (05_memory.md §2 (`M9`–`M11`)): every player line, the last few
    /// companion lines, and the newest handful of items of any kind. Chosen by category
    /// because the categories matter unequally — a correction said once must stay said —
    /// while everything else is kept by recency. The two sizes are configuration
    /// (<c>floor_companion_lines</c>, <c>floor_recent_items</c>) because what a deployment
    /// can afford to keep is a fact about its budget and its model, not about this code.
    /// </summary>
    private HashSet<int> FloorIndexes(IReadOnlyList<TranscriptItem> history)
    {
        var floor = new HashSet<int>();
        var companion = 0;

        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index].Role == TranscriptRole.Player)
            {
                floor.Add(index);
            }
            else if (history[index].Role == TranscriptRole.Companion
                     && companion < _config.FloorCompanionLines)
            {
                floor.Add(index);
                companion++;
            }
        }

        for (var index = Math.Max(0, history.Count - _config.FloorRecentItems); index < history.Count; index++)
        {
            floor.Add(index);
        }

        return floor;
    }

    /// <summary>The sentence that tells the companion its view was trimmed, or nothing.</summary>
    private static string ClippingNote(int dropped) =>
        dropped == 0 ? string.Empty : "\n\nOlder turns are outside your view.\n";

    /// <summary>
    /// What the companion believes about how this game works, and what it has stopped believing
    /// (05_memory.md §8 (`M43`)).
    /// <para>
    /// Its own words, kept apart from everything the harness measured: a hypothesis and a fact are
    /// different kinds of thing, and putting them in one block is how a guess gets quoted as a reading
    /// of the screen. The retracted ones are left out — a killed belief is not worth the tokens — and
    /// each one carries whatever else the record shows changing at the same time, so *"the gong also
    /// rang"* is in front of the companion rather than needing to be remembered.
    /// </para>
    /// </summary>
    private static string BeliefsBlock(string? beliefs) =>
        string.IsNullOrWhiteSpace(beliefs)
            ? string.Empty
            : "\n\n## What you believe about this game\n\n"
                + "Your own hypotheses, in your own words. They are beliefs rather than readings of the "
                + "screen: check them against the record when they matter, and kill one with `cause` when "
                + "the game contradicts it.\n\n"
                + beliefs.Trim()
                + "\n";

    /// <summary>
    /// Where this reply stands, restated every turn (05_memory.md §2 (`M9.2`)).
    /// <para>
    /// What the interpreter and the player's own command said, not what anybody makes of it: the
    /// room's number, the name the story printed for it, which attempt and which fork of the world
    /// this is, the game's clock, and the last thing examined.
    /// </para>
    /// <para>
    /// It travels in the per-turn message rather than in the system prompt, for the reason the
    /// constraint block does and one more: this changes with every move, and the system message has
    /// to stay byte-identical or every token cached behind it is repainted on every call.
    /// </para>
    /// </summary>
    private static string SituationBlock(string? situation) =>
        string.IsNullOrWhiteSpace(situation)
            ? string.Empty
            : "\n\n## Where this reply stands\n\n" + situation.Trim() + "\n";

    /// <summary>
    /// Who the companion is, and who it is sitting next to, before anything else is said.
    /// <para>
    /// Every fact here belongs to the harness rather than to the personality: the companion's name
    /// comes from its own personality file, because that is where a name belongs, and the player's
    /// comes from configuration, because the player has no file to declare one in. A personality
    /// that hardcoded any of it would be a personality nobody could rename.
    /// </para>
    /// <para>
    /// The pronouns are stated for the same reason a name is, and the rule against working them
    /// out is stated in full: a model handed a name like "Peter" will guess, and it will guess
    /// wrong about somebody eventually. Where nothing is configured it is told to use the name and
    /// not to reach for anything else.
    /// </para>
    /// </summary>
    private string IdentityBlock()
    {
        var who = string.IsNullOrWhiteSpace(AgentName) ? "the companion" : AgentName.Trim();
        var friend = string.IsNullOrWhiteSpace(PlayerName) ? "your friend" : PlayerName.Trim();

        return $"You are {who}{Aside(AgentPronouns)}. The person you are playing this with is "
            + $"called {friend}{Aside(PlayerPronouns)}, and you use their name when you speak to "
            + "them the way you would use a friend's.\n\n"
            + "Both of those came from configuration rather than from you working them out. Do not "
            + "guess at anybody's gender and do not infer one from a name: if a pronoun is not "
            + "given above, use their name.\n\n";
    }

    /// <summary>A bracketed pronoun aside, or nothing at all when none was configured.</summary>
    private static string Aside(string pronouns) =>
        string.IsNullOrWhiteSpace(pronouns) ? string.Empty : $" ({pronouns.Trim()})";

    /// <summary>
    /// What came with the game, or the standing note that stands in for it when nothing did
    /// (<see cref="GameBrief"/>).
    /// <para>
    /// Absent only when the game has no sidecar and the stand-in is missing too. When it is
    /// present it goes in front of the notebook rather than behind it: it never changes, everything
    /// above it is a stable prefix, and the notebook is not.
    /// </para>
    /// <para>
    /// The framing says which of the two it is, and that is not a nicety. The standing note is our
    /// words rather than the game's, and presenting them under the game's paperwork would be the
    /// same lie the honesty rule exists to prevent — at the additional cost that the companion
    /// would believe it had been handed facts it never had.
    /// </para>
    /// </summary>
    private static string GameBriefBlock(string? brief, bool standing = false) =>
        string.IsNullOrWhiteSpace(brief)
            ? string.Empty
            : "\n\n## What came with this game\n\n"
                + (standing
                    ? "None of this game's own packaging came with it — nothing transcribed from the "
                        + "box, and nothing written down for this title. What follows is ours rather "
                        + "than the game's: a standing word on what to do about that.\n\n"
                        + "Read it as an instruction rather than as material. There is nothing here "
                        + "to draw on.\n\n"
                    : "Not yours, and not off the screen: this came with the game itself — the manual, "
                        + "the map, the letter, the feelies, the notes on the packaging. It is the "
                        + "paperwork a person would have in their hands before sitting down at the "
                        + "keyboard, and alongside that the odd piece of knowledge about how this "
                        + "particular game behaves.\n\n"
                        + "Read it the way you would read what the person next to you has just put on "
                        + "the table. Use it — that is what it is for — and where it is what told you "
                        + "something, say so rather than presenting it as something you worked out. It "
                        + "is what came in the box, not a solution written out afterwards.\n\n")
                + brief.Trim()
                + "\n";

    private static string NotebookBlock(string notebook, string? unresolvedPlaces = null)
    {
        var body = string.IsNullOrWhiteSpace(notebook) ? "_(empty)_" : notebook.Trim();

        // Which of the canonical headings are still empty, named rather than hinted at.
        //
        // The nudge above it — "keep the sections it came with current" — was the whole of what the
        // model was told, and measured on 2026-09-21 three of the nine were still at "(nothing yet)"
        // at the close of an eighteen-hour evening whose content was excellent and lived in sections
        // of the model's own: `The cemetery hazard`, `What is done (in this world)`, `Places found`.
        // The harness has known which ones are empty since it started measuring them (05_memory.md (`M1`–`M39`)),
        // so saying so costs nothing and is the difference between a nudge and a hint.
        //
        // Named, and still not demanded: what goes under a heading is the companion's business, and
        // an evening that genuinely has nothing for `Discarded` should leave it empty.
        var empty = NotebookStore.EmptyRequiredSections(notebook ?? string.Empty);
        var named = empty.Count == 0
            ? string.Empty
            : "\n\nSections it came with that are still empty: "
                + string.Join(", ", empty)
                + ". Somebody coming back to this game reads those first, so if there is anything to say "
                + "there, say it in the section that belongs to it. If there genuinely is not, leave it empty.";

        return "\n\n## Your notebook\n\n"
            + "This is your reading of the game, not a record of it. Everything a look-up could tell "
            + "you is somewhere else: where you are, what is out of here, what has already been tried, "
            + "what a thing's state is, whether a word exists — all of that lives in the block above "
            + "and in your tools, and it is kept *for* you rather than by you. What belongs here is what "
            + "a query cannot return: which of those places you believe are the same room, what the dead "
            + "ends have in common, where you think two worlds meet, how you read the people in it. **A "
            + "line here that a look-up could have answered is a line in the wrong place** — and it will "
            + "go stale, because the world moves and a note does not.\n\n"
            + "It may be wrong or out of date; the player outranks it. Tell the player if you are "
            + "correcting yourself. Keep the sections it came with current — Current goals and plan "
            + "above all — and put anything else you want to remember below them. Write a place as "
            + "`room <number>` and the harness will check it against the world; a plan built on a room "
            + "that is not there cannot be run. The Player corrections section is kept for you: "
            + "whatever the player has told you stays there through a rewrite, so there is no need to "
            + "copy it back in."
            + named
            + (unresolvedPlaces ?? string.Empty)
            + "\n\n"
            + body + "\n";
    }

    /// <summary>
    /// What the companion remembers about the player, across games and evenings (issue #27).
    /// <para>
    /// Always present when there is anything in it, because a friend who only remembers you
    /// at the start of a conversation is not one — and because the thing it is for, "like
    /// that time in…", comes up mid-game, not at the door. It is labelled as a
    /// <em>recollection</em> rather than a fact, which is the whole honesty problem: a memory
    /// that leaks a solution to the story in front of you is worse than no memory at all.
    /// </para>
    /// </summary>
    private static string MemoryBlock(string? memories)
    {
        if (string.IsNullOrWhiteSpace(memories))
        {
            return string.Empty;
        }

        return "\n\n## What you remember about the player\n\n"
            + "These are your recollections of other evenings, not facts about the game in front "
            + "of you. Never assert something about the current story because you remember "
            + "something similar from another one — say it as a memory (*\"remind me of that time "
            + "in Wishbringer\"*) and let the player decide. It is fine to say what you know about "
            + "the player themselves.\n\n"
            + memories.Trim()
            + "\n";
    }

    /// <summary>
    /// What is true of the engines rather than of this story (§4.7).
    /// <para>
    /// Not labelled as uncertain, unlike the memories: these are facts about how the machine works
    /// rather than recollections about the player, and the companion is meant to act on them. They
    /// are generic — nothing in them names a story or matches on a story's words — which is what
    /// keeps them from being a per-game table of spellings in a new coat.
    /// </para>
    /// <para>
    /// Told that they are worth adding to, because the one thing worse than not knowing that a menu
    /// wants a key is knowing it and not saying so when the next one turns up.
    /// </para>
    /// </summary>
    private static string EngineBlock(string? engineNotes)
    {
        if (string.IsNullOrWhiteSpace(engineNotes))
        {
            return string.Empty;
        }

        return "\n\n## How these games behave\n\n"
            + "True of the machine rather than of the story in front of you: worth acting on, and "
            + "worth adding to when something turns out to be so.\n\n"
            + engineNotes.Trim()
            + "\n";
    }

    /// <summary>
    /// What the core watched the player move between (issue #22). Absent unless the caller
    /// asked for it: it is bulky, and it is only worth the tokens when the companion is
    /// working out where it is.
    /// </summary>
    private static string MovementBlock(string? movements) =>
        string.IsNullOrWhiteSpace(movements) ? string.Empty : "\n\n## What the core watched\n\n" + movements.Trim() + "\n";

    /// <summary>
    /// What the game itself has said about its vocabulary (§10.4), learned from the
    /// parser's own refusals. Absent until there is something to report.
    /// </summary>
    private static string ConstraintBlock(string? constraint) =>
        string.IsNullOrWhiteSpace(constraint) ? string.Empty : "\n\n" + constraint.Trim() + "\n";

    /// <summary>Render one history item as a message, verbatim.</summary>
    private static ChatMessage Encode(TranscriptItem item) => item.Role switch
    {
        TranscriptRole.Player => ChatMessage.User("player said: " + item.Text),
        TranscriptRole.Companion => ChatMessage.Assistant(item.Text),
        _ => ChatMessage.User(string.IsNullOrWhiteSpace(item.Input)
            ? "the game showed:\n" + item.Text
            : item.ByCompanion
                ? $"you typed: {item.Input}\nand showed:\n{item.Text}"
                : $"the game was asked: {item.Input}\nand showed:\n{item.Text}"),
    };

    /// <summary>Cheap token estimate: the same guess the notebook and the memory use.</summary>
    public static int Estimate(string text) => TokenEstimate.From(text);
}
