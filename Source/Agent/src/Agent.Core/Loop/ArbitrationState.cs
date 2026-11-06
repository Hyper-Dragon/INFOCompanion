namespace Agent.Core.Loop;

/// <summary>The states of the voice-and-vote machine (§10.4).</summary>
public enum CompanionState
{
    /// <summary>The game is waiting; nothing of the companion's is pending.</summary>
    Idle,

    /// <summary>The companion is deciding or acting: the default.</summary>
    AgentTurn,

    /// <summary>The player spoke mid-turn; the companion stops acting and responds.</summary>
    PlayerInterrupt,

    /// <summary>A risky move was proposed; waiting for a grant or a veto.</summary>
    AwaitingGrant,

    /// <summary>The companion asked the player a question and is waiting for the answer.</summary>
    WaitingForAnswer,

    /// <summary>The player took the keyboard by force for one command.</summary>
    PlayerDirect,
}

/// <summary>An intended move awaiting the player's blessing (§6.4).</summary>
public sealed record PendingProposal(
    string Id,
    string Action,
    string Reasoning,
    string Risk,
    DateTimeOffset CreatedAt);

/// <summary>
/// The arbitration state machine — the heart of the project (§10.4).
/// <para>
/// The rules that matter are enforced here in code, not merely asked for in the
/// prompt: announce-then-act, preemption on any player line, risky actions needing a
/// grant, a post-veto cooldown, and a hard cap on how many steps the companion may
/// chain before handing the wheel back. "The prompt is not a guarantee; the code is
/// the guarantee" (§2.7).
/// </para>
/// </summary>
public sealed class ArbitrationState
{
    /// <summary>
    /// How many of the companion's recent actions the history keeps, for the going-in-circles rule
    /// (§10.6). The value in force is <c>arbitration_history_length</c> in <c>agent-config.json</c>.
    /// </summary>
    public int HistoryLength { get; init; } = Config.ConfigDefaults.ArbitrationHistoryLength;

    private readonly List<string> _recentActions = [];
    private readonly Dictionary<string, DateTimeOffset> _vetoed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a vetoed action stays vetoed.</summary>
    public TimeSpan VetoCooldown { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How many repeats of one action count as going in circles.</summary>
    public int CirclesThreshold { get; init; } = 2;

    public CompanionState State { get; private set; } = CompanionState.Idle;

    /// <summary>The move waiting on the player, if any.</summary>
    public PendingProposal? Pending { get; private set; }

    /// <summary>Did the companion author the input of the turn now in flight?</summary>
    public bool AgentAuthoredFlight { get; private set; }

    /// <summary>Has the player taken the keyboard by force for the turn in flight?</summary>
    public bool PlayerDirectedFlight { get; private set; }

    /// <summary>
    /// Has the game stopped?
    /// <para>
    /// Nothing of ours is in a world that is no longer running (A9), and that is two facts at once: a
    /// pass may talk and may not act, because there is nothing to move in; and an errand in hand ends
    /// with the game it was for. It is a fact about the *world* rather than about the turn, which is
    /// why it does not go away with <see cref="NoteTurnCompleted"/>: it is cleared by
    /// <see cref="ResetForNewGame"/>, which is what every path that brings a game back calls — a
    /// restart, a reset, a restore, or a different story entirely.
    /// </para>
    /// </summary>
    public bool TheWorldIsGone { get; private set; }

    /// <summary>The game stopped: nothing of ours is in a world that is no longer running.</summary>
    public void NoteTheWorldIsGone() => TheWorldIsGone = true;

    /// <summary>Steps taken since the player last spoke, for logs and diagnostics.</summary>
    public int ChainDepth { get; private set; }

    public bool IsAwaitingGrant => Pending is not null;

    /// <summary>
    /// The question the companion asked and has not had answered (issue #23).
    /// <para>
    /// Nothing used to record that an <c>[ask]</c> had gone out. The companion asked a
    /// question, the idle clock carried on counting from the *player's* last line, and about
    /// fifty seconds later it spoke over the question it had just asked — twice in one
    /// measured session, and the player's complaint was precisely that it will not wait.
    /// </para>
    /// <para>
    /// A question puts the ball in the player's court, and the machine now knows it: while
    /// this is set, nothing self-initiated happens at all.
    /// </para>
    /// </summary>
    public string? AwaitingAnswer { get; private set; }

    /// <summary>
    /// When the outstanding question was asked, so the wait can be bounded.
    /// <para>
    /// Found by leaving the container unattended: the companion asked "Follow the gully down,
    /// or try the road instead?", nobody was connected to answer, and it held everything for
    /// ever. The game stopped advancing after six turns and stayed stopped. "It asked, so it
    /// waits" is right; "it waits until the end of time" is not.
    /// </para>
    /// </summary>
    public DateTimeOffset? AwaitingAnswerSince { get; private set; }

    /// <summary>
    /// When the outstanding proposal went out, so that wait can be bounded too.
    /// <para>
    /// A field of its own, not a shared "the companion is waiting for the player" clock: the
    /// first version shared one, and the two gates clear each other. Making a proposal sets
    /// <c>acted</c>, and the loop clears the answer state when it has acted — which wiped the
    /// proposal's own start time, so the grant wait read "elapsed: zero" on every tick and the
    /// give-up could never fire. The run stopped at turn eighteen and stayed there, holding a
    /// proposal nobody could answer, and the log said patiently that it would give up in 90
    /// seconds for ever. Two waits, two clocks.
    /// </para>
    /// </summary>
    public DateTimeOffset? ProposalSince { get; private set; }

    /// <summary>Is the companion waiting on an answer to a question it asked?</summary>
    public bool IsAwaitingAnswer => AwaitingAnswer is not null;

    /// <summary>
    /// Is the player holding the companion? (03_session.md §6 (`S41`–`S43`))
    /// <para>
    /// A line whose first character is a space is the player saying *"wait while I write this"* — a
    /// key that already means "I am composing" is a better switch than a new command, because it
    /// needs no explanation and interrupts nobody. Take the space off — backspace, or delete — and
    /// it carries on; press Return, which starts a fresh line with no space on it, and it carries
    /// on. There is no timer and no release gesture: the hold is a property of the line in the box,
    /// not a state the player enters and leaves.
    /// </para>
    /// <para>
    /// It is deliberately *not* a cancellation. Nothing is killed, no call is called off, and the
    /// game is not touched: what waits is the next turn the companion would have taken, which is
    /// the difference between a pause and a stop.
    /// </para>
    /// </summary>
    public bool IsHeld { get; private set; }

    /// <summary>The line in the box began or stopped beginning with a space (03_session.md §6 (`S41`–`S43`)).</summary>
    public void NoteHeld(bool held)
    {
        IsHeld = held;
        if (held)
        {
            // Holding is the player doing something, so the room is not quiet — and without this
            // the clock would carry on through the hold and fire the idle trigger the instant the
            // space was taken off again.
            NoteTheExchangeMoved("the player holding the companion");
        }
    }

    /// <summary>
    /// When anything last happened in the room — either of them (04_companion.md §5 (`C29`–`C41`)).
    /// <para>
    /// This used to be a single timestamp for the player's last line, and nothing else moved it:
    /// not the companion's own words, not its own question, not a turn the game had just handed
    /// over, not a keystroke still being typed. Measured on 2026-09-17, that clock fired the idle
    /// trigger **103 times and produced one call** (04_companion.md §5 (`C29`–`C41`)), and the symptom the player
    /// reported — a companion talking over the question it had just asked — is the same mechanism.
    /// </para>
    /// <para>
    /// Quiet is therefore a property of the *exchange*, not of the player: it is how long it has
    /// been since either of them did anything at all. Everything that means "something happened"
    /// moves it — a player line, a keystroke, a framed turn, a command dispatched into the game,
    /// the companion's own words and its own question. Wall-clock time survives for exactly one
    /// job, noticing that a player who is still connected has gone, and even that is now measured
    /// from the last thing anybody did rather than from the player's last line.
    /// </para>
    /// <para>
    /// The pace does not need its own term here: a turn takes at most the ceiling to arrive (spec
    /// 02 §8.2, twenty seconds by default), which is well inside the shortest window anything
    /// asks about, so "its own words have gone out" is never mistakenly read as silence.
    /// </para>
    /// </summary>
    public DateTimeOffset LastEvent { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>What that last thing was, in words, for the log and for the nudge's own text.</summary>
    public string LastEventWas { get; private set; } = "the session opening";

    /// <summary>How long the room has been quiet: nothing either of them has done.</summary>
    public TimeSpan QuietFor => DateTimeOffset.UtcNow - LastEvent;

    /// <summary>
    /// Something happened in the room (04_companion.md §5 (`C29`–`C41`)). Called by every event below, and by the loop for the
    /// two things the state cannot see for itself: the companion's speech, and a session opening.
    /// </summary>
    public void NoteTheExchangeMoved(string what)
    {
        LastEvent = DateTimeOffset.UtcNow;
        LastEventWas = what;
        ExchangeMoved?.Invoke();
    }

    /// <summary>
    /// Told whenever the exchange moves, so everything that keeps a clock of its own can start it
    /// again from the same event (04_companion.md §5 (`C29`–`C41`)).
    /// <para>
    /// The speaking triggers hold a nudge guard measured against the quiet clock, and a guard
    /// counting against a clock that resets is the sort of thing that quietly stops working: wired
    /// once here, no call site can forget it, and `quiet` means the same thing everywhere it is
    /// asked about.
    /// </para>
    /// </summary>
    public Action? ExchangeMoved { get; set; }

    /// <summary>
    /// When the player last pressed a key in a pane they can type in (issue #42).
    /// </summary>
    public DateTimeOffset? LastPlayerTyping { get; private set; }

    /// <summary>
    /// Is the player mid-sentence? The window is the caller's: a keystroke every few seconds is
    /// somebody composing a line, and one keystroke on its own is somebody leaning on the keyboard.
    /// </summary>
    public bool PlayerIsTyping(TimeSpan window) =>
        LastPlayerTyping is { } at && DateTimeOffset.UtcNow - at < window;

    /// <summary>
    /// The player pressed a key (issue #42).
    /// <para>
    /// Not an answer — but both waits restart, because somebody who is *typing* is not somebody who
    /// has left. Measured in the UAT session: of ten questions the companion asked, four were
    /// settled by the companion acting first, because ninety seconds of quiet is indistinguishable
    /// from a person taking their time over a careful answer, and the harness read the quiet as an
    /// answer.
    /// </para>
    /// <para>
    /// Restarting the clocks is the whole mechanism. A player who takes two minutes to write
    /// something is doing the right thing slowly, and the patience must not punish them for it;
    /// a player who stops typing gets the ordinary patience again, so a genuinely absent one is
    /// not waited on for ever.
    /// </para>
    /// </summary>
    public void NotePlayerTyping()
    {
        LastPlayerTyping = DateTimeOffset.UtcNow;
        NoteTheExchangeMoved("the player typing");
        if (IsAwaitingAnswer)
        {
            AwaitingAnswerSince = DateTimeOffset.UtcNow;
        }

        if (IsAwaitingGrant)
        {
            ProposalSince = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>The companion asked the player something and is now waiting.</summary>
    public void NoteAsked(string question)
    {
        AwaitingAnswer = question;
        AwaitingAnswerSince = DateTimeOffset.UtcNow;
        State = CompanionState.WaitingForAnswer;
        // Its own question is an event like any other: the room has not gone quiet just because
        // the companion chose to stop talking (C5, issue #23).
        NoteTheExchangeMoved("the companion's own question");
    }

    /// <summary>
    /// The question is no longer live: the player answered it, preempted it, or the run it
    /// belonged to has ended.
    /// </summary>
    /// <remarks>
    /// Deliberately does not touch <see cref="ProposalSince"/>. The loop calls this whenever the
    /// companion acted, and proposing is acting — so sharing one clock here is exactly how a
    /// fresh proposal lost its start time.
    /// </remarks>
    public void ClearAnswer()
    {
        AwaitingAnswer = null;
        AwaitingAnswerSince = null;
        if (State == CompanionState.WaitingForAnswer)
        {
            State = CompanionState.AgentTurn;
        }
    }

    /// <summary>Any player line preempts the companion (§10.4 rule 2 and rule 5).</summary>
    public void NotePlayerLine(string text, bool grantedOrVetoed)
    {
        State = CompanionState.PlayerInterrupt;
        ChainDepth = 0;

        // Whatever the player said, it answers the question — or makes it moot — and it settles
        // any proposal as well, so both wait clocks go. Either way the companion is no longer
        // waiting on them (issue #23). A stale clock would make the *next* question look like it
        // had already expired the moment it was asked.
        AwaitingAnswer = null;
        AwaitingAnswerSince = null;
        ProposalSince = null;

        if (!grantedOrVetoed)
        {
            // Talking about something else drops the plan; the player always wins.
            Pending = null;
        }

        NoteTheExchangeMoved("the player's line");
        _ = text;
    }

    /// <summary>The companion is deciding what to say or do.</summary>
    public void BeginDecision() => State = CompanionState.AgentTurn;

    /// <summary>Nothing pending: back to idle.</summary>
    public void Idle() => State = Pending is null ? CompanionState.Idle : State;

    /// <summary>A proposal went out; the companion waits for a grant or a veto.</summary>
    public void NoteProposal(PendingProposal proposal)
    {
        Pending = proposal;
        ProposalSince = DateTimeOffset.UtcNow;
        State = CompanionState.AwaitingGrant;
        NoteTheExchangeMoved("the companion's own proposal");
    }

    /// <summary>Approve the pending proposal, returning the action for the caller to run.</summary>
    public string? Approve()
    {
        var action = Pending?.Action;
        Pending = null;
        ProposalSince = null;
        State = CompanionState.AgentTurn;
        return action;
    }

    /// <summary>Cancel the pending proposal and freeze that action for a while.</summary>
    public string? Veto(DateTimeOffset now)
    {
        var action = Pending?.Action;
        if (!string.IsNullOrWhiteSpace(action))
        {
            _vetoed[Normalise(action)] = now + VetoCooldown;
        }

        Pending = null;
        ProposalSince = null;
        State = CompanionState.AgentTurn;
        return action;
    }

    /// <summary>Has this action been vetoed recently? (§10.4 rule 4)</summary>
    public bool IsVetoed(string action, DateTimeOffset now)
    {
        var key = Normalise(action);
        if (!_vetoed.TryGetValue(key, out var until))
        {
            return false;
        }

        if (until <= now)
        {
            _vetoed.Remove(key);
            return false;
        }

        return true;
    }

    /// <summary>Record that frotz was asked to do something, and by whom.</summary>
    public void NoteInputSent(bool byAgent)
    {
        AgentAuthoredFlight = byAgent;
        if (byAgent)
        {
            ChainDepth++;
        }

        // A command into the game is an event whichever of them typed it.
        NoteTheExchangeMoved(byAgent ? "the companion's own command" : "the player's command");
    }

    public void NotePlayerDirect(bool started)
    {
        PlayerDirectedFlight = started;
        State = started ? CompanionState.PlayerDirect : CompanionState.AgentTurn;
        if (started)
        {
            Pending = null;
            AwaitingAnswerSince = null;
            ProposalSince = null;
            NoteTheExchangeMoved("the player taking the keyboard");
        }
    }

    /// <summary>The turn finished: clear the per-turn flags.</summary>
    public void NoteTurnCompleted()
    {
        AgentAuthoredFlight = false;
        PlayerDirectedFlight = false;
        // `PlayerDirect` goes back to `AgentTurn` along with the flag it belongs to. One flag is not
        // the other: leaving the state naming a flight that has just been declared over is how the
        // next reader of it inherits the bug this call exists to end (2026-09-24).
        if (State is CompanionState.PlayerInterrupt or CompanionState.PlayerDirect)
        {
            State = CompanionState.AgentTurn;
        }

        // The game answering is the room moving, whoever asked. This is the one the idle clock
        // most needed: a turn landing a second ago is not fifty seconds of silence, and the old
        // clock could not tell the difference.
        NoteTheExchangeMoved("the game's answer");
    }

    /// <summary>Remember an action, for the going-in-circles check (§10.6).</summary>
    public void NoteActionTaken(string action)
    {
        _recentActions.Add(Normalise(action));
        while (_recentActions.Count > HistoryLength)
        {
            _recentActions.RemoveAt(0);
        }
    }

    public int RepetitionsOf(string action)
    {
        var key = Normalise(action);
        return _recentActions.Count(candidate => candidate == key);
    }

    /// <summary>The action the companion keeps retrying without progress, if any.</summary>
    public (string Action, int Count)? StuckAction()
    {
        var stuck = _recentActions
            .GroupBy(action => action, StringComparer.Ordinal)
            .Select(group => (Action: group.Key, Count: group.Count()))
            .Where(pair => pair.Count > CirclesThreshold)
            .OrderByDescending(pair => pair.Count)
            .FirstOrDefault();

        return stuck.Count > CirclesThreshold ? stuck : null;
    }

    /// <summary>Forget the action history (a game switch makes it meaningless).</summary>
    public void ResetForNewGame()
    {
        _recentActions.Clear();
        _vetoed.Clear();
        Pending = null;
        ChainDepth = 0;
        AwaitingAnswer = null;
        AwaitingAnswerSince = null;
        ProposalSince = null;
        State = CompanionState.Idle;
        IsHeld = false;
        // A game is back: a restart, a reset, a restore, or a different story. Whatever stopped, the
        // world is running again, so a pass may act in it and an errand is worth having.
        TheWorldIsGone = false;
        NoteTheExchangeMoved("a new game");
    }

    private static string Normalise(string action) =>
        string.Join(' ', (action ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
