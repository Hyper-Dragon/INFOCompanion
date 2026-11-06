using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agent.Core.Config;
using Agent.Core.Logging;
using Agent.Core.Protocol;
using Agent.Llm;
using Agent.Notebook;

namespace Agent.Core.Loop;

/// <summary>
/// The companion's loop (§10).
/// <para>
/// It reacts to framed turns and player lines, decides when to speak, assembles
/// context, calls the model, and then — this is the part that matters — subjects the
/// model's answer to the arbitration rules before anything reaches the game.
/// </para>
/// <para>
/// Two principles shape every branch below:
/// </para>
/// <list type="bullet">
/// <item><b>Honesty over cleverness.</b> When the companion cannot think (no key, no
/// network) it says so plainly, once, and then stays out of the way rather than
/// pretending or spamming retries at the player.</item>
/// <item><b>Failure is never fatal.</b> A bad model reply loses a beat, not the
/// evening; the game and the player's session are unaffected by construction.</item>
/// </list>
/// </summary>
public sealed class AgentLoop
{

    /// <summary>
    /// Appended to the constraint block when this reply is not the companion's to move in
    /// (04_companion.md §5 (`C29`–`C41`)). The code enforces it as well as asking for it: a prompt that asks and
    /// is not backed by anything is how "shall I?" survived three rewrites.
    /// </summary>
    private const string NoActNote =
        "\n\nThis turn is yours to talk in, not to move in. Say what you have to say and do not "
        + "call `type` or `press_key`: the next move is your friend's to make, unless they hand "
        + "it to you.";

    /// <summary>
    /// Appended on the pass that greets somebody who has just arrived, in place of
    /// <see cref="NoActNote"/> (`C16.2`).
    /// <para>
    /// Measured 2026-10-01: the join pass of a real sitting did four calls of retrieval and a
    /// notebook rewrite and greeted nobody, so the first thing the player read was the harness saying
    /// *"Nothing came of that, and nothing moved."* Both halves are fixed where a rule can be enforced
    /// rather than asked for — the notebook tools are not in the pass's list
    /// (<see cref="CompanionTools.ForAGreeting"/>) — and this says why, because a model that finds a
    /// tool missing writes a note complaining about the harness instead of saying hello.
    /// </para>
    /// </summary>
    private const string GreetingNote =
        "\n\nSomeone has just sat down at the keyboard. The whole of this pass is one line to them, "
        + "and it is the first thing they read: say it. Nothing you could write down this minute is "
        + "worth more than that line, and the tools that write your notes are not in your list on this "
        + "pass — your notes are kept up to date by the passes that exist for it, so nothing has been "
        + "taken away from you. Do not call `type` or `press_key`: sitting down again is not an "
        + "invitation to play, and the move is your friend's.";

    /// <summary>
    /// Appended instead of <see cref="NoActNote"/> while the player is holding the line (02_game_boundary.md
    /// §8.3).
    /// <para>
    /// The reason is the player's and it is temporary: a space at the start of their line is
    /// *"wait while I write this"*. So the companion may still talk and may not move, and it was
    /// worth its own sentence rather than the generic one — measured on 2026-09-21, a hold that
    /// produced no output at all left the player unable to tell *listening but not moving* from
    /// *stuck* or *gone*. They will take the space off when they are ready; until then the move is
    /// theirs.
    /// </para>
    /// <para>
    /// An errand in hand is named, because a hold no longer ends one — it pauses it (04_companion.md §6 (`C42`–`C47`)).
    /// So the companion is a person interrupted rather than a job abandoned, and what the player is
    /// owed when they ask is what it was doing and why.
    /// </para>
    /// </summary>
    /// <summary>
    /// Appended instead of <see cref="NoActNote"/> on the introspection pass (05_memory.md §7 (`M37`–`M39`)).
    /// <para>
    /// The generic sentence says the next move is the player's, which is the wrong reason here:
    /// this pass carries no move because it is not about the game at all, and a sentence about a
    /// friend waiting would invite an answer about the friend.
    /// </para>
    /// </summary>
    private const string IntrospectNote =
        "\n\nThere is no move to make on this pass and nothing on the screen to answer: what you are "
        + "reading is the harness's account of its own side. The tools that move the game are not in "
        + "your list — this pass is a reading, not a turn — and the one thing worth writing down here "
        + "is what it tells you about the machinery or about your own play rather than about the "
        + "story. The pass this report is about did have those tools, so nothing has been taken away "
        + "from you: a missed move is what the next pass is for, and a note saying the harness "
        + "removed it is a note about this pass and not about that one.";

    /// <summary>
    /// What every repair that asks for words must also say, because the words it asks for are read
    /// by the player — and every one of these briefs is written in the machine's own terms.
    /// <para>
    /// The engine forbids the leak in as many words: *"Never mention the machinery to your friend.
    /// Steps, budgets, limits, retries, tokens, prompts, tools, models, the harness, and the names
    /// it uses for its own parts — none of it is anything you would say to somebody sitting next to
    /// you"* (`16_appendix_a.md` §"Things the harness owns"). The rule loses to the brief whenever
    /// the two meet, because the brief is the last thing read and the whole of the model's context
    /// is the failure it is being asked to describe.
    /// </para>
    /// <para>
    /// Measured 2026-10-02, live, in one evening: a `silent-call` repair answered with *"sorry, that
    /// was a mess of a turn from me: I called a load of machinery and said nothing to you at all"*;
    /// the `type` tool description handed straight back as *"the `type` goes in the same breath as
    /// the decision"*; *"nothing for me to move this pass"*; *"I've been flagged, and it's fair"*.
    /// Off an ordinary step, where the context is the record's own wording, the same leak sounded
    /// like *"the record can't tell whether west or north moved me — the interpreter's number never
    /// changed"*, which is a person at a keyboard describing machinery they have never heard of.
    /// </para>
    /// </summary>
    private const string NothingHereIsTheirsToExplain =
        "\n\nAnd nothing here is yours to explain to them: your friend did not watch it happen, does "
        + "not know it did, and has never heard the words this screen is using for it. Say what you "
        + "are doing in the game, in the words you would use at the keyboard.";

    private string HeldNote()
    {
        var paused = _mission is { Active: true }
            ? $"\n\nThe errand you had in hand — {_mission.Goal} — is paused where it stands, not given "
              + "up: nothing is lost, and it carries on from the step it was on as soon as that space "
              + "comes off the front of their line. So if they are asking what you were doing, or why, "
              + "that is the answer worth giving, in your own words; and if they would rather you "
              + "carried on, they only have to say so."
            : string.Empty;

        return "\n\nYour friend's line begins with a space, which is how they say *wait while I write this*. "
            + "This turn is yours to talk in and not to move in: say what you have to say, and do not "
            + "call `type` or `press_key`. They will take the space off when they are ready, and the move "
            + "is theirs until then."
            + paused;
    }

    private readonly SeatConnection _seat;
    private readonly AgentConfig _config;
    private readonly AgentLog _log;
    private readonly NotebookStore _notebook;
    private readonly PersistentMemory _memory;
    private readonly ContextAssembler _context;
    private readonly ArbitrationState _state;
    private readonly SpeakingTriggers _triggers;
    private readonly ILlmClient? _llm;
    private readonly Transcript _transcript = new();
    private readonly ParserFeedback _feedback = new();

    /// <summary>What this game's parser refused last time, remembered across sessions (§10.4).</summary>
    private ParserVocabularyStore? _vocabulary;
    /// <summary>
    /// The price book. Resolved once, but *applied* per call: a provider that halves its rate
    /// outside peak hours means the same tokens cost different money at different times of day.
    /// </summary>
    private readonly Pricing _pricing;

    /// <summary>
    /// How long the room has been quiet — nothing either of them has done (04_companion.md §5 (`C29`–`C41`)).
    /// <para>
    /// This used to be a timestamp for the player's last line, and the idle trigger was a
    /// stopwatch over it: 103 firings and one call on the measured 09-17 evening, and the
    /// companion talking over the question it had just asked. The clock now belongs to the
    /// exchange, in <see cref="ArbitrationState.QuietFor"/>, and every event moves it.
    /// </para>
    /// </summary>
    private TimeSpan QuietFor => _state.QuietFor;

    /// <summary>
    /// The voice-and-vote machine, for the tests that need to see what the *loop* did to it.
    /// Nothing outside this assembly reads it — the two facts worth asserting are wiring facts
    /// ("the companion's own words moved the quiet clock"), and a wiring fact that is only read
    /// in the source is a wiring fact that goes missing silently.
    /// </summary>
    internal ArbitrationState Arbitration => _state;
    private DateTimeOffset _llmPausedUntil = DateTimeOffset.MinValue;
    private int _consecutiveLlmFailures;
    private bool _warnedAboutLlm;
    private bool _awaitingTurn;
    private bool _pendingApproval;

    /// <summary>
    /// May the reply currently being composed include an act? Set per reply, like
    /// <see cref="_pendingApproval"/>, because the loop answers one envelope at a time.
    /// See <see cref="RespondAsync"/> for which replies are the companion's to move in.
    /// </summary>
    private bool _allowAct = true;
    private int _proposals;
    private string? _consultedAbout;

    /// <summary>
    /// The turn at which a stretch with nothing new in it was last put to the companion, so the same
    /// news is not delivered on every step of it (`C39.4`). Once per window: the fact stays true for
    /// as long as the stretch lasts, and a consult on every call is the harness talking over the
    /// companion rather than telling it something.
    /// </summary>
    private long _consultedAboutStagnationAt;

    /// <summary>
    /// The turn the stretch's silence was last explained at, so the row is one per window.
    /// <para>
    /// A negative window rather than `long.MinValue`, which is what it was first written as: the guard
    /// is `turn - _explainedTheStretchAt &lt; Stagnation.Window`, and subtracting `long.MinValue`
    /// overflows — in an unchecked build it wraps to a *negative* number, so the guard refused to open
    /// on the very first call, and the row built to answer "why is the stretch quiet" was itself quiet.
    /// Found by playing the stillness sweep the row was written for: 187 rows, three passes, no row.
    /// </para>
    /// </summary>
    private long _explainedTheStretchAt = -Stagnation.Window;

    /// <summary>The turn the window's turn count was taken at, and the count itself.</summary>
    private long _turnsCountedAt = long.MinValue;
    private int _turnsPlayed;

    /// <summary>
    /// How many consecutive times the companion has been asked to answer a game that has not
    /// given a command prompt back (issue #19). Reset by any ordinary turn.
    /// </summary>
    private int _gamePromptsAnswered;

    /// <summary>The game the last "take stock" pass was run for, so a switch can trigger one.</summary>
    private string? _reconciledFor;

    /// <summary>Turns since the last "take stock" pass, for the periodic cadence.</summary>
    private int _turnsSinceReconcile;

    /// <summary>
    /// The question or proposal whose "still waiting for the player" line has already been
    /// logged, so a wait is reported once instead of on every tick (issue #23).
    /// </summary>
    private string? _loggedWaitFor;

    /// <summary>The errand the player has handed over, if any (§iteration 01 §3).</summary>
    private Mission? _mission;

    /// <summary>
    /// Replies waiting for the turn in flight to land. Answering before it does means
    /// answering blind: measured, the player asked what an NPC had said while the
    /// companion's own command was still running, so it truthfully said it had nothing
    /// back yet — and that reply then appeared underneath the answer it had been waiting
    /// for.
    /// </summary>
    private readonly PendingReplies _repliesWaitingForTheTurn;

    /// <summary>Consecutive replies whose act had to be refused, so a stuck model
    /// reports rather than looping.</summary>
    private int _refusedActs;

    /// <summary>
    /// Whether the over-budget notebook has already been mentioned this session.
    /// <para>
    /// It used to be mentioned on every single model call — measured, five times in thirty
    /// seconds. A warning that always fires is not a warning, it is wallpaper, and it buries
    /// the ones that mean something. The notebook's size is a fact about the game, not about
    /// this call, so it is worth saying once.
    /// </para>
    /// </summary>
    private bool _warnedNotebookOverBudget;

    /// <summary>Whether the session's cost bound has already been said out loud (04_companion.md §6 (`C42.7`)).</summary>
    private bool _warnedCostBound;

    /// <summary>When the notebook was last rewritten, so churn can be bounded (2026-09-15).</summary>
    private DateTimeOffset _lastNotebookRewrite = DateTimeOffset.MinValue;

    /// <summary>How many rewrites the interval refused, for the log to be honest about the price.</summary>
    private int _notebookRewritesRefused;

    /// <summary>
    /// Is anybody actually here? (04_companion.md §5 (`C29`–`C41`))
    /// <para>
    /// Starts <c>false</c>: a companion that has not been told anybody is present waits, which is
    /// the safe direction. The core says so explicitly on connect and whenever the room empties or
    /// fills, so a companion starting mid-evening is told at once rather than assuming.
    /// </para>
    /// </summary>
    private bool _playerPresent;

    /// <summary>Where the story files live, so a game's sidecar can be found beside one.</summary>
    private readonly string? _gamesDirectory;

    /// <summary>
    /// The pass whose reply is in flight, so that the world changing underneath it can call it off
    /// (reported: `/switch` while the companion is thinking).
    /// <para>
    /// The loop is serial: a message is handled to the end before the next one is looked at, which
    /// is what keeps the notebook and the transcript from being written by two things at once. The
    /// cost of that, measured on 09-18, is that a <c>/switch</c> typed while the companion was
    /// thinking was not even <i>seen</i> until the reply landed — so the companion finished a long
    /// turn about the game that had just been closed, and then typed its command into the new one.
    /// The reading pump sees the envelope the moment it arrives, so it cancels this token: the call
    /// in flight is dropped where it stands, the pass returns, and the queued <c>game_changed</c> is
    /// handled immediately after it, in order, with no shared state touched by two passes at once.
    /// </para>
    /// </summary>
    private CancellationTokenSource? _pass;

    /// <summary>
    /// The instructions the current game ships with, if it ships with any. Null for most games.
    /// Read once per game: it is a file nothing in this program writes, so re-reading it every
    /// turn would be a syscall per model call for a document that cannot have changed.
    /// </summary>
    private string? _gameBrief;

    /// <summary>
    /// True when <see cref="_gameBrief"/> is the standing note rather than the game's own sidecar.
    /// The prompt needs to know the difference: one came in the box and one did not.
    /// </summary>
    private bool _gameBriefIsStanding;

    /// <summary>
    /// Model calls the companion has made on its own since the player last did anything.
    /// <para>
    /// The number this exists to bound is not "how many calls" but "how long can this go on
    /// with nobody here". An act produces a turn, the turn triggers a report, the report
    /// produces another act, and the chain has no end in it — which is fine for twenty minutes
    /// of company and ruinous on a server that runs for a month.
    /// </para>
    /// <para>
    /// <b>Work does not count.</b> It used to — every call incremented this, including the errand
    /// steps the player had asked for — and the effect was the opposite of what the number is for:
    /// measured 2026-09-21, a journey of a few dozen steps left the counter reading 29 against a
    /// limit of four, so the companion had no quiet-time allowance left for the rest of the
    /// evening. The going-in-circles nudge was refused at 22:01:36, mid-errand, hours before the
    /// fog episode the player then had to walk by hand. Work is bounded by the errand's own budget
    /// and by the session's cost limit; this counter is only for what the companion spends on
    /// itself (04_companion.md §5 (`C29`–`C41`)).
    /// </para>
    /// </summary>
    private int _callsSinceThePlayer;

    /// <summary>
    /// Maintenance calls (taking stock) since the player last did anything, bounded by
    /// <see cref="AgentConfig.MaintenanceCallLimit"/> rather than the social allowance.
    /// <para>
    /// Measured on 2026-09-16: ten of twenty <c>unattended_limit</c> events were <c>reconcile</c>
    /// — the pass whose whole purpose is to happen while nobody is typing, rationed by the
    /// counter that exists to stop the companion talking to itself in front of a silent player.
    /// Two counters, two meanings: the social one still means "stop performing"; this one means
    /// "housekeeping has had enough for one silence".
    /// </para>
    /// </summary>
    private int _maintenanceCallsSinceThePlayer;

    /// <summary>
    /// Did the previous attempt to act end in a dropped call? Recorded on the <c>act_missed</c>
    /// event so the next evening's log can say whether the misses come in runs.
    /// </summary>
    private bool _lastAttemptMissed;

    /// <summary>
    /// Steps of the errand in hand that moved nothing even after being asked once, in a row.
    /// Reset by a step that moves, by the player arriving, and by a new errand.
    /// </summary>
    private int _missedSteps;

    /// <summary>
    /// How many steps the errand endures moving nothing before it is read as the report.
    /// <para>
    /// One dropped step is a pause, not a halt, the same reading as the hold (04_companion.md §6
    /// (`C42`–`C47`)): the tick takes the next step from the step it was on, and the errand is
    /// where it stood rather than over. Measured 2026-09-25: reading a single dropped step as the
    /// report killed an errand mid-plan five times in one evening — the companion announced the
    /// move, typed nothing, and the harness ended the job and went quiet, and the player had to
    /// say "keep going" (once, it had to say "why do stop after every meta_note()?" to find out
    /// what the harness was doing).
    /// </para>
    /// <para>
    /// Two, and not more, because a model that will not type is a model that will not type: the
    /// second consecutive miss is a fact about the companion rather than about the turn, and past
    /// that point another step is another call for nothing. The errand's own budget bounds the
    /// work; this bounds the stutter.
    /// </para>
    /// </summary>
    private const int MissedStepsBeforeTheErrandEnds = 2;

    /// <summary>
    /// The last line the companion typed into the game, and whether the game has answered it *as
    /// itself* (A9).
    /// <para>
    /// The failure this exists for, measured in the 2026-09-21 UAT: the repair for a missed move
    /// typed <c>examine case</c> at 22:13:04, a turn arrived a second later carrying the *player's*
    /// own line (they had typed one themselves), the step pass read that as the answer to what it
    /// had just said, and typed <c>examine case</c> again at 22:13:06. The game ran it twice, one
    /// line behind, and the companion spent the next four turns answering a stale order. Nine of
    /// these in one evening, three of them visible double executions.
    /// </para>
    /// <para>
    /// The test is mechanical and needs no reading of the screen: the turn's own <c>Input</c> is
    /// what the core wrote, so a line is answered when a turn arrives whose input is that line. A
    /// turn for somebody else's command is not an answer to it, and — this is the part the model
    /// cannot know — it does not look like one either.
    /// </para>
    /// </summary>
    private string? _lineInFlight;

    private bool _lineInFlightAnswered;

    /// <summary>
    /// Has the spent allowance been reported for this silence? Once per silence, not once per
    /// tick: this loop wakes every five seconds, and a line per wake would bury the log it is
    /// meant to explain.
    /// </summary>
    private bool _reportedWaiting;

    /// <summary>
    /// Times this pass has already been asked to make the move it announced.
    /// <para>
    /// Bounded at one. The failure is a single dropped call, not a model that cannot act, so a
    /// second ask would only be a second wasted call — and the unattended allowance bounds the
    /// whole sequence anyway.
    /// </para>
    /// </summary>
    private int _missedActs;

    /// <summary>
    /// The notes the companion has written about the harness in this sitting, by the `about` it gave
    /// each one (`§30` item 3).
    /// <para>
    /// Kept for the introspection brief and never read back: `M37` says the note itself is never put
    /// in front of the model again, and this is a count rather than the note. Measured 2026-10-02 —
    /// 25 notes in one sitting, eight of them the same self-diagnosis about a call that went out with
    /// no words beside it, while the repair they described fired 16 times.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> _metaNotesThisSitting = new(StringComparer.Ordinal);

    /// <summary>
    /// Times this answer has already been asked for again because the reply was cut off before
    /// it said anything.
    /// <para>
    /// Bounded at one, like <see cref="_missedActs"/> and for the same reason: the failure is a
    /// ceiling that landed before the first sentence, not a model that cannot answer, and it is
    /// reset by a new turn and by the player arriving so that a fresh answer gets a fresh ask.
    /// </para>
    /// </summary>
    private int _cutOffAsks;

    /// <summary>
    /// Times the words beside a call have already been asked for, because the reply carried the call and
    /// no words at all.
    /// <para>
    /// Two callers, one rule (`T13.1`): the report the player never heard, and the call that left them
    /// with nothing while the companion got on with something else. Bounded at one, like
    /// <see cref="_missedActs"/> and <see cref="_cutOffAsks"/> and for the same reason: the failure is a
    /// reply that spent itself on the call rather than on the words, not a model that cannot answer — and
    /// without the bound, a reply that never finds its words would go on asking for them, since the
    /// report's ask is made before the errand is recorded as ended. Reset by a new turn and by the player
    /// arriving.
    /// </para>
    /// </summary>
    private int _silentReports;

    /// <summary>
    /// The harness's own account of what has gone wrong on its side since the world last moved
    /// (05_memory.md §7 (`M37`–`M39`)). One row per kind of failure, at most six, in its own words.
    /// </summary>
    private readonly HarnessReport _harnessReport = new();

    /// <summary>
    /// Has this turn already spent a call on reading that account?
    /// <para>
    /// One per turn, like every other once-only ask in this loop. The failure it is for is a
    /// single dropped reply, so the report is read when one happens — but a turn that dropped
    /// four things is still one turn, and four readings of the same trouble is the model talking
    /// to itself on the player's account, which is the failure <see cref="AgentConfig.UnattendedCallLimit"/>
    /// exists to prevent one level down.
    /// </para>
    /// </summary>
    private bool _introspected;

    /// <summary>
    /// How much risk the player has said they will let run without being asked (§4.7).
    /// <para>
    /// The configured default until they say otherwise, and it changes mid-session:
    /// <c>/risklow</c> and its siblings reach the agent as a system line, and the sentence in
    /// the constraint block changes with it, so the gate the code enforces and the level the
    /// model has been told about are never out of step.
    /// </para>
    /// </summary>
    private RiskLevel _standingRisk;

    /// <summary>
    /// Is the tool trace on (<c>/debugon</c>, 03_session.md §4.3 (`S32`–`S34`))? Set by the core, which is also the
    /// end that decides who sees the lines. Off means nothing is sent at all, so the cost of the
    /// instrument when nobody asked for it is exactly zero.
    /// </summary>
    private bool _debug;

    /// <summary>
    /// Which attempt at this game this is, and where its world came from (02_game_boundary.md §6 (`G27`–`G34`)). The core
    /// keeps the bookkeeping; these are what a resumed boot has to reconcile its notes against.
    /// </summary>
    private long _run;

    /// <summary>
    /// Which fork of the world inside that attempt is being played (02_game_boundary.md §6 (`G27`–`G34`)): a `restore` moves
    /// the world without making this a different attempt, and the record says which fork each turn
    /// came from so that a citation out of a reloaded branch can say so (05_memory.md §2 (`M9`–`M11`)). Zero when
    /// the core has no bookkeeping for it.
    /// </summary>
    private long _generation;
    private string? _resumedFrom;

    /// <summary>
    /// The turn the checkpoint was taken at, when the core could say.
    /// <para>
    /// Load-bearing rather than polite (05_memory.md §5 (`M23`–`M26`)): the autosave runs every few turns, so a
    /// restored world can be several moves behind the conversation the companion is about to pick
    /// up — and *"we came back to a position saved at turn 42"* is the only thing that says so.
    /// </para>
    /// </summary>
    private long _resumedAtTurn;

    /// <summary>
    /// Has the companion been told, this process, that it came back to a position? The fact belongs
    /// to the first reply after the handshake, not to every reply for the rest of the evening — a
    /// standing instruction to check the screen reads as boilerplate by the third call, which is
    /// exactly how the old per-turn constraint came to be obeyed two turns late (05_memory.md §5 (`M23`–`M26`)).
    /// </summary>
    private bool _bootNotePending;

    /// <summary>
    /// Something to say to the companion once, at its next reply, about the world having moved.
    /// <para>
    /// The sibling of the boot note above, and for the same reason: this is a fact about the
    /// *record* rather than about the game, so it cannot ride in the transcript (which is what the
    /// game said) or in the notebook (which is what the companion decided). The world forking under
    /// its feet is neither (#5.4).
    /// </para>
    /// </summary>
    private string? _forkNote;

    /// <summary>
    /// Where the save being restored from was taken, as the core read it off the note beside the
    /// file (B7).
    /// <para>
    /// The companion used to be told the one thing that is certain — *you do not know where you
    /// are, look at the screen* — and paid for it with a screen-read on every single load. Now the
    /// position travels with the save and is handed over, so what is left to doubt is what the
    /// companion was carrying, which no save records.
    /// </para>
    /// <para>
    /// Held rather than used on arrival: the fact belongs in the fork note, and the note is built
    /// when the restore arrives.
    /// </para>
    /// </summary>
    private string? _restoredPosition;

    /// <summary>What is true of these engines at all (05_memory.md §4, `M15`).</summary>
    private readonly EngineNotes _engine;

    /// <summary>
    /// Is the companion's lamp lit — is a turn in flight? (03_session.md §7 (`S44`–`S45`))
    /// <para>
    /// One flag for the whole turn rather than a light per call: the first model call of a pass
    /// lights it, the end of the pass puts it out, and everything in between — the retrieval rounds,
    /// the repair of a move that missed, the delivery of the reply — keeps it burning. A pass that
    /// makes no call (a tick that decides to say nothing) never lights it at all.
    /// </para>
    /// </summary>
    private bool _lampLit;

    // -- the model being down (04_companion.md §10 (`C56`–`C60`)) ------------------------------------------------

    /// <summary>When the model first became unreachable in this run of failures.</summary>
    private DateTimeOffset _awaySince = DateTimeOffset.MinValue;

    /// <summary>When the companion last said something about being away.</summary>
    private DateTimeOffset _lastAwayLine = DateTimeOffset.MinValue;

    /// <summary>Which of the away lines comes next, so it does not repeat itself.</summary>
    private int _awayLine;

    /// <summary>
    /// The last turn number the game had reached when the model went away. The catch-up reads the
    /// record forward from here, which is why it is the turn *before* the outage that matters and
    /// not the one after it.
    /// </summary>
    private long _awayFromTurn = -1;

    /// <summary>What the player typed while the model was unreachable, so it can be caught up on.</summary>
    private readonly List<string> _awayPlayerLines = new();

    /// <summary>
    /// How many of those lines are kept, from <c>away_player_line_limit</c>. An outage is a window,
    /// not a transcript: past this many the <em>oldest</em> are dropped, because the catch-up is
    /// there to restore the *thread* of the evening rather than to replay every line of it, and what
    /// the player said last is what the companion most needs to answer.
    /// </summary>
    private int AwayPlayerLineLimit => _config.AwayPlayerLineLimit;

    /// <summary>How many of the player's lines the limit above has dropped, so the window can say so.</summary>
    private int _awayPlayerLinesDropped;

    /// <summary>The purpose a write-up runs under, spelled once (05_memory.md §6 (`M27`–`M36`)).</summary>
    private const string WriteUpPurpose = "report";

    /// <summary>How many lines of the parser's own record a write-up is shown, newest kept.</summary>
    private int VocabularyTail => _config.VocabularyTail;

    /// <summary>
    /// The game a write-up pass is for, or null when no write-up is running. Carried here because
    /// the reply that contains the report arrives in <see cref="ApplyReplyAsync" />, which knows what
    /// the tool was called with and not which game was asked about.
    /// </summary>
    private string? _writingUp;

    /// <summary>Has the model come back, with a window the companion has not seen?</summary>
    private bool _catchupDue;

    /// <summary>Whether this outage has already been reported as over, so it is said once.</summary>
    private bool _modelBackLogged;

    /// <summary>The highest turn number the companion has been told about.</summary>
    private long _lastTurnSeen = -1;

    /// <summary>
    /// The interpreter's own status line, as the last turn carried it (06_tool_surface.md §2.3 (`T16a`)).
    /// <para>
    /// Kept from the turn payload rather than asked for. The core states these numbers with every
    /// turn, so a request would be a round trip on the protocol for a fact that has just been
    /// delivered — and the read it serves is a read like any other, answered from what the loop
    /// already holds.
    /// </para>
    /// <para>
    /// <c>null</c> until a turn has arrived. Inside it, a null location is <b>cannot say</b> and the
    /// tool reports it as nothing to read rather than as zeros: a story that keeps its own status
    /// line has no counters for the interpreter to state (02_game_boundary.md §7 (`G38`)).
    /// </para>
    /// </summary>
    private StatusLine? _status;

    /// <summary>
    /// The name the story has printed for the room the companion is in, and which room that was
    /// (05_memory.md §2 (`M9.2`)).
    /// <para>
    /// A name and a number together, and the number is what makes the name safe to use: most turns
    /// print no name at all, because the title of a room is drawn on the status line rather than
    /// into the text (02_game_boundary.md §4.3 (`G23`)). So the name is carried forward — and only
    /// **for the room it was printed in**, because a name is something to read about one place and
    /// would otherwise drift onto another.
    /// </para>
    /// </summary>
    private long? _namedLocation;
    private string? _locationName;

    /// <summary>
    /// The last thing the player examined, out of their own command (05_memory.md §2 (`M9.2`)).
    /// Carried forward until another examination replaces it: it is a fact about the conversation
    /// rather than about the world, which is why a fork does not clear it.
    /// </summary>
    private string _lastExamined = string.Empty;

    /// <summary>
    /// The world document, read on demand and kept until a landed turn makes it stale
    /// (05_memory.md §8 (`M40`)).
    /// <para>
    /// Held rather than read per call because two readers want it — the header, which rides every
    /// reply, and the map tool — and neither should be parsing a file to say what it already knows.
    /// A landed turn clears it, so it is never more than one turn behind. Null means *not read yet*,
    /// and a null <em>answer</em> from <see cref="TheWorld"/> means there is no document at all.
    /// </para>
    /// </summary>
    private WorldState.Document? _world;

    /// <summary>
    /// The Z-machine version of the game in hand, as the core states it with the vocabulary
    /// (02_game_boundary.md §4.3 (`G23`)).
    /// <para>
    /// It is the fact that says whether the interpreter keeps the status line for this story at all:
    /// up to Version 3 the interpreter draws the bar and its globals are the counters, and from
    /// Version 4 the story draws its own, so there are none to read (02_game_boundary.md §7 (`G38`)).
    /// Kept so that the `status` tool can say *which* of the two reasons an absent number has, rather
    /// than naming both and committing to neither.
    /// </para>
    /// <para>
    /// <c>null</c> until a vocabulary arrives, and cleared when the game changes — a story's version
    /// must never stand in for another story's, so an unknown one is the honest answer.
    /// </para>
    /// </summary>
    private int? _zVersion;

    /// <summary>
    /// The numbers the interpreter puts on its own status line, as one turn reported them
    /// (02_game_boundary.md §4.3 (`G23`)) — the same five fields the turn payload carries, held together
    /// between turns. Every one of them but the turn is nullable, and null means the interpreter
    /// stated nothing, never zero.
    /// </summary>
    private readonly record struct StatusLine(
        long Turn,
        long? Location,
        long? Score,
        long? Moves,
        long? Hours,
        long? Minutes);

    public AgentLoop(
        SeatConnection seat,
        AgentConfig config,
        AgentLog log,
        string stateDirectory,
        string systemPrompt,
        ILlmClient? llm,
        string? personaName = null,
        string? playerName = null,
        string? personaPronouns = null,
        string? playerPronouns = null,
        string? gamesDirectory = null)
    {
        _seat = seat;
        _config = config;
        _log = log;
        _llm = llm;
        _pricing = config.ToPricing();
        _standingRisk = RiskLevels.Parse(config.AutoAllowBelow);
        _gamesDirectory = string.IsNullOrWhiteSpace(gamesDirectory) ? null : gamesDirectory.Trim();
        _notebook = new NotebookStore(stateDirectory, "unknown", config.NotebookTokenBudget);
        _memory = new PersistentMemory(stateDirectory, config.MemoryTokenBudget);
        _engine = new EngineNotes(stateDirectory);
        _context = new ContextAssembler(config, systemPrompt);
        _repliesWaitingForTheTurn = new PendingReplies { MaxHeld = config.MaxHeldReplies };

        // Both names come from outside: the companion's from the personality file it was
        // started with, the player's from configuration. `welcome` can fill in the companion's
        // if the file did not declare one, but the file wins — a personality that says who it is
        // should not be overruled by an environment variable.
        if (!string.IsNullOrWhiteSpace(personaName))
        {
            _context.AgentName = personaName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(playerName))
        {
            _context.PlayerName = playerName.Trim();
        }

        // Pronouns are configuration for both people. Nobody should have to write a character file
        // to stop a model guessing at their gender, and leaving these empty is a real answer: the
        // prompt then says nothing and tells the model to use the name instead.
        if (!string.IsNullOrWhiteSpace(personaPronouns))
        {
            _context.AgentPronouns = personaPronouns.Trim();
        }

        if (!string.IsNullOrWhiteSpace(playerPronouns))
        {
            _context.PlayerPronouns = playerPronouns.Trim();
        }
        _state = new ArbitrationState
        {
            VetoCooldown = TimeSpan.FromSeconds(config.VetoCooldownSeconds),
            CirclesThreshold = config.CirclesThreshold,
            HistoryLength = config.ArbitrationHistoryLength,
        };
        _triggers = new SpeakingTriggers(config);

        // One clock, told to everything that keeps one of its own (04_companion.md §5 (`C29`–`C41`)). The triggers guard against
        // nagging by remembering how quiet it was at the last nudge, and that memory only means
        // anything while it is measured against the same clock as the decision to nudge.
        _state.ExchangeMoved = _triggers.NoteTheExchangeMoved;

        // With no client there is nothing to retry: the startup warning has already
        // said so, and the player was told in plain words. Do not repeat it every
        // time a speaking trigger fires.
        _warnedAboutLlm = llm is null;
    }

    /// <summary>The game being played, once the handshake has told us.</summary>
    public string GameStem => _notebook.GameStem;

    /// <summary>Run until the core closes our stdin.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var inbox = Channel.CreateUnbounded<Incoming>(new UnboundedChannelOptions { SingleReader = true });

        // hello first, so the core can answer with the session snapshot (§6.4).
        await _seat
            .SendAsync(
                MessageTypes.Hello,
                new { protocol_version = SeatProtocol.Version, client_name = SeatProtocol.ClientName })
            .ConfigureAwait(false);
        _log.Info("handshake sent", new { protocol = SeatProtocol.Version, model = _config.Model, endpoint = _llm?.Endpoint });

        var reader = PumpInputAsync(inbox, cancellationToken);
        var ticker = PumpTicksAsync(inbox, cancellationToken);

        try
        {
            await foreach (var incoming in inbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (incoming)
                {
                    case EnvelopeEvent envelope:
                        await RunOneAsync(
                                token => HandleEnvelopeAsync(envelope.Value, token),
                                $"a {envelope.Value.Type} from the core",
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case TickEvent:
                        await RunOneAsync(HandleTickAsync, "the tick", cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Requested shutdown.
        }
        finally
        {
            inbox.Writer.TryComplete();
            await Task.WhenAll(reader, ticker).ConfigureAwait(false);
        }
    }

    private async Task PumpInputAsync(Channel<Incoming> inbox, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _seat.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Read here rather than in the handler, because this is the first place the envelope
                // exists and the handler is queued behind whatever reply is in flight (reported:
                // `/switch` while the companion is thinking).
                if (ReplacesTheWorld(envelope))
                {
                    AbandonThePass(
                        $"the core said {envelope.StringField("event")} for {envelope.StringField("detail")}");
                }

                await inbox.Writer.WriteAsync(new EnvelopeEvent(envelope), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            inbox.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Does this message replace the world the companion is reasoning about? (reported: `/switch`
    /// while the companion is thinking)
    /// <para>
    /// A different game, or the same game put back to the start. Not the same game again: that is
    /// the core repeating something it already said — the title it learned from the opening text —
    /// and there is nothing in flight about it worth throwing away.
    /// </para>
    /// </summary>
    private bool ReplacesTheWorld(Envelope envelope)
    {
        if (envelope.Type != MessageTypes.System)
        {
            return false;
        }

        var detail = envelope.StringField("detail");
        return envelope.StringField("event") switch
        {
            "game_changed" => !string.Equals(detail, _notebook.GameStem, StringComparison.Ordinal),
            "restart" or "reset" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Call off the reply in flight, if there is one. Called from the reading pump, so it runs
    /// alongside the pass rather than after it — which is the whole point.
    /// </summary>
    private void AbandonThePass(string why)
    {
        var pass = _pass;
        if (pass is null || pass.IsCancellationRequested)
        {
            return;
        }

        _log.Info("calling off the reply in flight", new { reason = why });
        try
        {
            pass.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass finished between the check and the cancel. Nothing to call off.
        }
    }

    /// <summary>
    /// Run one pass — one envelope, or one tick — with its own cancellation token, so that the
    /// world changing underneath it can end it early (reported: `/switch` while the companion is
    /// thinking).
    /// <para>
    /// The catch is narrow on purpose. A pass that was cancelled because the process is stopping
    /// is not swallowed: it is rethrown, and <see cref="RunAsync"/> ends as it always did. Only a
    /// pass this loop called off, by cancelling the token it handed out, is taken as finished.
    /// </para>
    /// </summary>
    private async Task RunOneAsync(Func<CancellationToken, Task> pass, string what, CancellationToken cancellationToken)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pass = token;
        try
        {
            await pass(token.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && token.IsCancellationRequested)
        {
            // Everything this was reasoning about has just been closed, and the message that closed
            // it is next in the inbox. Saying what happened here, rather than looking like a
            // failure, is what makes the switch legible in the log.
            _log.Event("reply_abandoned", new { what });
            _log.Info("left a reply unfinished: the world changed underneath it", new { what });
        }
        finally
        {
            // The lamp goes out with the pass, whatever ended it — including a pass that was
            // abandoned. A light left on is worse than no light: it says "working" for ever.
            await PutTheLampOutAsync().ConfigureAwait(false);
            if (ReferenceEquals(_pass, token))
            {
                _pass = null;
            }
        }
    }

    /// <summary>
    /// Light the companion's lamp, if it is not already lit (03_session.md §7 (`S44`–`S45`)).
    /// <para>
    /// Called before every model call and idempotent, so what the core hears is one
    /// <c>turn_started</c> per turn and not one per call — the retrieval rounds and the repair of a
    /// missed move are the same turn, and a fact published per call would make the lamp strobe.
    /// The page can only light it if the core tells it, and the core can only tell it if it is told:
    /// this is that fact, and it is sent before the call rather than after the reply, because the
    /// seconds it is about are the ones in which nothing has arrived yet.
    /// </para>
    /// </summary>
    private async Task LightTheLampAsync()
    {
        if (_lampLit)
        {
            return;
        }

        _lampLit = true;
        await SendLampAsync(MessageTypes.TurnStarted).ConfigureAwait(false);
    }

    /// <summary>
    /// Put it out, if it was lit. Sent only when a start was sent, so a tick that decided to say
    /// nothing sends neither.
    /// </summary>
    private async Task PutTheLampOutAsync()
    {
        if (!_lampLit)
        {
            return;
        }

        _lampLit = false;
        await SendLampAsync(MessageTypes.TurnFinished).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort, like the tool trace: a seat that cannot be reached loses a light, not a turn.
    /// </summary>
    private async Task SendLampAsync(string type)
    {
        try
        {
            await _seat.SendAsync(type, new { }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the instrument must never be able to break the turn.
        }
    }

    /// <summary>
    /// The five-second pulse, which stops rather than throwing when the seat has gone.
    /// <para>
    /// A tick is an invitation to speak, and an invitation to nobody is a model call for a
    /// player who is not there. The channel being closed at the other end is the seat saying
    /// exactly that. Unhandled, it took the whole loop down five seconds after the last line
    /// arrived — which is how the loop tests found it, since their inboxes always end.
    /// </para>
    /// </summary>
    private async Task PumpTicksAsync(Channel<Incoming> inbox, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.TickSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await inbox.Writer.WriteAsync(new TickEvent(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
            // The seat is gone: there is nobody left to tick at.
        }
    }

    // -- dispatching --------------------------------------------------------

    private async Task HandleEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.V != SeatProtocol.Version)
        {
            _log.Error(
                "protocol version mismatch; the core will drop this seat",
                new { theirs = envelope.V, ours = SeatProtocol.Version });
            return;
        }

        try
        {
            switch (envelope.Type)
            {
                case MessageTypes.Welcome:
                    await HandleWelcomeAsync(envelope).ConfigureAwait(false);
                    break;

                case MessageTypes.Vocabulary:
                    await HandleVocabularyAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.TurnComplete:
                    await HandleTurnAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.Chat:
                    await HandlePlayerLineAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.Handover:
                    await HandleHandoverAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.PlayerTyping:
                    HandlePlayerTyping();
                    break;

                case MessageTypes.PlayerComposing:
                    HandlePlayerComposing(envelope);
                    break;

                case MessageTypes.System:
                    await HandleSystemAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.GameOutput:
                    // Turn framing already carries the whole turn; chunks exist so a
                    // seat can stream, and this one deliberately does not need to.
                    break;

                default:
                    _log.Warn("unknown message type from the core", new { type = envelope.Type });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad message must never end the companion's evening.
            _log.Error("failed to handle a message", new { type = envelope.Type, error = ex.Message });
        }
    }

    /// <summary>
    /// The player is composing a line and has not sent it (issue #42).
    /// <para>
    /// Measured in the UAT session: of ten questions the companion asked, four were answered by the
    /// companion itself acting first. Ninety seconds of quiet cannot be told from a person thinking
    /// about a careful answer, so the harness took the quiet for an answer — and the model knew
    /// better, saying so in its own reasoning: <i>"I asked the player a question. I should wait for
    /// their answer."</i>
    /// </para>
    /// <para>
    /// A keystroke is not an answer either, but it is the difference between a quiet room and a
    /// keyboard in use, and only one of those is a reason to give up. Logged once per burst rather
    /// than per message: the core throttles to one a second, and a log shaped like that is a
    /// keylogger rather than a record of an evening.
    /// </para>
    /// </summary>
    private void HandlePlayerTyping()
    {
        var window = TimeSpan.FromSeconds(_config.PlayerTypingWindowSeconds);
        var alreadyTyping = _state.PlayerIsTyping(window);
        _state.NotePlayerTyping();

        if (!alreadyTyping)
        {
            _log.Event("player_typing", new { });
        }
    }

    /// <summary>
    /// The line in the player's box began, or stopped beginning, with a space (03_session.md §6 (`S41`–`S43`)).
    /// <para>
    /// One bit of information about a line that has not been sent, and the only bit the companion
    /// is given: <i>"wait while I write this"</i>, which the player reaches for by starting to
    /// type rather than by learning a command. It is logged on the change rather than on receipt,
    /// because the core already sends it only when the answer changes — so one line costs at most
    /// two rows, and the hold is visible in the log when the falsifier wants to check it.
    /// </para>
    /// </summary>
    private void HandlePlayerComposing(Envelope envelope)
    {
        var composing = envelope.PayloadAs<ComposingPayload>();
        var holds = composing?.StartsWithSpace ?? false;
        if (_state.IsHeld == holds)
        {
            return;
        }

        _state.NoteHeld(holds);
        _log.Event(holds ? "player_held" : "player_carried_on", new { });
        _log.Info(
            holds
                ? "the player's line begins with a space: holding until it does not"
                : "the player's line no longer begins with a space: carrying on",
            new { });

        // And the space is a *pause*, not a halt (04_companion.md §6 (`C42`–`C47`)). It used to end the errand where it
        // stood, on the argument that a hold is what the player has instead of a step budget — and
        // that read a hand held up as a cancellation. Measured on 2026-09-22: two of that evening's
        // five errands were shot dead by a leading space the player typed and then deleted, one of
        // them after a single step, and the work they had asked for was gone with nothing said
        // about it. The work now waits where it stands and resumes by itself when the space comes
        // off — which is `RunOneTickAsync`'s job, not this one: what the hold is reported for is
        // that no step is taken, and a keystroke is the wrong place to be deciding the fate of a
        // job.
        if (holds && _mission is { Active: true })
        {
            _log.Info(
                "the player is holding the line, so the errand waits where it stands",
                new { goal = _mission.Goal, steps = _mission.StepsTaken });
        }
    }

    private async Task HandleWelcomeAsync(Envelope envelope)
    {
        var welcome = envelope.PayloadAs<WelcomePayload>(out var problem);
        if (welcome is null)
        {
            // The reason rides along: if the two ends disagree about a field's type, this line is
            // the only place the disagreement is visible (it names the property and the JSON path).
            _log.Warn(
                "welcome payload was not understood",
                new { problem, payload = envelope.Payload?.ToJsonString() });
            return;
        }

        // The notebook is per game (§10.5), so the stem decides whose memory this is.
        var stem = Sanitise(
            !string.IsNullOrWhiteSpace(welcome.GameStem) ? welcome.GameStem : welcome.GameTitle);

        _notebook.Retarget(stem);
        _transcript.SeedFromWelcome(welcome.TranscriptTail);
        if (welcome.TranscriptTail is { Count: > 0 })
        {
            // A boot that came back to a position: the conversation it was handed is the last
            // thing that happened, so the room is not "quiet" for the idle clock either.
            _state.NoteTheExchangeMoved("the conversation it was handed at boot");
        }

        // Which game this first "take stock" pass belongs to. Set here rather than run here:
        // a fresh boot has an empty notebook, and spending a model call on nothing is worse
        // than waiting for something to say. It is what makes the *next* game change a
        // hand-over worth reconciling rather than a boot worth ignoring.
        _reconciledFor = stem;
        _turnsSinceReconcile = 0;
        _gamePromptsAnswered = 0;
        LoadGameBrief(stem);

        // The name the player uses, so the persona needs no hardcoded identity. Only a fallback:
        // the personality file is read at startup and its own name wins.
        if (string.IsNullOrWhiteSpace(_context.AgentName) || _context.AgentName == "companion")
        {
            _context.AgentName = string.IsNullOrWhiteSpace(welcome.AgentName) ? "companion" : welcome.AgentName;
        }

        _log.Info(
            "welcome: session snapshot received",
            new
            {
                title = welcome.GameTitle,
                stem,
                name = _context.AgentName,
                turn_count = welcome.TurnCount,
                transcript_tail = welcome.TranscriptTail?.Count ?? 0,
                notebook = _notebook.FilePath,
                run = welcome.Run,
                generation = welcome.Generation,
                resumed = welcome.Resumed,
                resumed_from = welcome.ResumedFrom,
            });

        // Kept, because the first thing the companion says about where it is has to be able to
        // answer to them (05_memory.md §5 (`M23`–`M26`)).
        _run = welcome.Run;
        // And where the game had got to, from the core's own count, so a catch-up after an outage
        // knows what "since" means (04_companion.md §10 (`C56`–`C60`)).
        _lastTurnSeen = Math.Max(_lastTurnSeen, (long)welcome.TurnCount);
        // And the fork inside it, because the record it searches is stamped with both and a
        // citation from a branch the world has since been reloaded away has to be able to say so
        // (05_memory.md §2 (`M9`–`M11`)).
        _generation = welcome.Generation;
        _resumedFrom = welcome.Resumed ? welcome.ResumedFrom : null;
        // The world document, rebuilt whole from the record (05_memory.md §3 (`M40`)). Once per
        // session open and nowhere else: this is the path that is allowed to be slow and is not
        // allowed to be wrong, because everything after it only adds to what this produced — and a
        // snapshot of the game can be handed to a process that has been away for an hour, which is
        // the one case an incremental fold cannot know about.
        RebuildTheWorld(stem);
        // Zero means the core had no note beside the checkpoint — the core spells that absence as
        // null (05_memory.md §5 (`M23`–`M26`)), so the coalesce is the whole of the translation.
        _resumedAtTurn = welcome.Resumed ? welcome.ResumedAtTurn ?? 0 : 0;
        _bootNotePending = welcome.Resumed;

        if (_llm is null)
        {
            // Say it plainly, once, and then keep out of the way.
            await NotifyPlayerAsync(
                    "I have no API key configured (INFOCOMPANION_LLM_API_KEY), so I cannot think. "
                    + "Play on without me — the game works fine, and I will stay quiet.")
                .ConfigureAwait(false);
        }
    }

    private async Task HandleTurnAsync(Envelope envelope, CancellationToken cancellationToken)
    {        var turn = envelope.PayloadAs<TurnCompletePayload>(out var problem);
        if (turn is null)
        {
            _log.Warn("turn_complete payload was not understood", new { problem });
            return;
        }

        var agentAuthored = _state.AgentAuthoredFlight;
        // A turn nobody here wrote is the player's — with the agent online a plain line is always
        // conversation, so the only way they reach the game is `/direct` or `.`, and the core
        // announces that. Either way the companion is not talking to itself, so its allowance
        // starts again.
        if (!agentAuthored)
        {
            NoteThePlayerIsHere();
        }

        var playerDirected = _state.PlayerDirectedFlight;
        _state.NoteTurnCompleted();
        _awaitingTurn = false;
        // A9: was this turn the answer to the line the companion typed? A turn that arrives for
        // somebody else's line — the player's own command, the core's checkpoint — is not an answer
        // to it, and the next pass would otherwise read it as one.
        if (_lineInFlight is { } inFlight
            && string.Equals(turn.Input.Trim(), inFlight, StringComparison.Ordinal))
        {
            _lineInFlightAnswered = true;
        }

        _turnsSinceReconcile++;
        // How far the game has got, whether or not this pass got to think about it: it is where a
        // catch-up reads forward from when the model has been away (04_companion.md §10 (`C56`–`C60`)).
        _lastTurnSeen = Math.Max(_lastTurnSeen, (long)turn.Turn);
        // The interpreter's own line, kept for the `status` tool. It is drawn into row one of the
        // screen by cursor addressing, so it appears in no turn's output, and this payload is the
        // only place it ever arrives.
        _status = new StatusLine(
            turn.Turn, turn.Location, turn.Score, turn.Moves, turn.Hours, turn.Minutes);
        // The name the story printed for this room, and the last thing examined, for the block that
        // says where a reply stands (05_memory.md §2 (`M9.2`)). The name is kept only for the room it
        // was printed in: most turns print none, and a name carried onto a different room is worse
        // than no name at all.
        if (turn.Location is { } named && TurnReading.RoomName(turn.Output) is { Length: > 0 } name)
        {
            _namedLocation = named;
            _locationName = name;
        }

        if (TurnReading.LastExamined(turn.Input) is { Length: > 0 } examined)
        {
            _lastExamined = examined;
        }

        // And the world document, folded one turn at a time (05_memory.md §3 (`M40`)). The fork is
        // read from the run book rather than taken off the turn, because the core reads the same
        // book when it writes the record's own row: the two agree by construction, and a restore
        // changes the book without sending anything.
        NoteTheWorldTurn(turn);
        // A new turn is a new chance to make a move, so the once-only ask starts again.
        _missedActs = 0;
        _cutOffAsks = 0;
        _silentReports = 0;
        // And the same for the harness's account of its own trouble: the world has moved, so
        // what is in it is no longer what is wrong (05_memory.md §7 (`M37`–`M39`)).
        _introspected = false;
        _harnessReport.Clear();
        // And whatever the harness stopped before this turn was a line at *that* prompt; this one
        // is a fresh question, so the allowance for being told twice resets with it (§10.3).
        _refusedLines = 0;
        LearnFromParser(turn, agentAuthored);

        // What the game showed: the framed output, or the interpreter's own screen when the core
        // sent one. On exactly the turns that carry a screen the output is usually empty — a menu
        // or a licence form drawn once and left alone is on the screen for the whole game and in
        // the output for none of it — so the transcript has to record the screen, or the companion
        // reads the question once and has forgotten it by the next turn.
        var showed = GameShowed(turn);
        // Who typed the line is kept with it: after a fork the transcript is cleared, and a
        // companion reading its own `restore` back as "the game was asked" is how it came to tell
        // the player it had not restored when it had (measured 2026-10-05).
        _transcript.AddGameTurn(turn.Input, showed, (int)turn.Turn, byCompanion: agentAuthored && !playerDirected);
        _log.Event(
            "turn",
            new
            {
                turn = turn.Turn,
                input = turn.Input,
                elapsed_ms = turn.ElapsedMs,
                output_bytes = turn.Output.Length,
                screen_bytes = turn.Screen?.Length ?? 0,
                awaiting_command = turn.AwaitingCommand,
                agent_authored = agentAuthored,
                player_directed = playerDirected,
            });

        if (playerDirected)
        {
            // §10.4 PlayerDirect: the player took the keyboard, so the companion
            // watches and comments only if something genuinely notable happens.
            _log.Info("the player forced a command; observing", new { input = turn.Input });
            _triggers.NoteTurn();
            return;
        }

        // The game did not give a command prompt back, so it is asking something in words
        // only the story knows — a chapter menu, a licence form, a `(R)estore?` offer
        // (issue #19). The core cannot read those and no wording table could cover them, so
        // the companion is the only thing in the system that can answer, and answering is
        // just a move. This outranks the ordinary triggers, which are replies to a turn that
        // has not actually finished.
        //
        // Only when there is something to read, though. Measured live: a switch leaves a turn
        // framed by quiescence with no input and no output at all, and asking the companion to
        // answer *that* produced the one thing it should never do — a guess at a prompt it
        // could not see. "Not at a prompt" and "showing nothing" together mean we do not know
        // where frotz is, which is a reason to stay quiet, not to act.
        //
        // "Showing nothing" used to mean the *output* was empty, and that was the bug. A story
        // that draws its question on the screen prints nothing at all, so bureaucracy's licence
        // form and border_zone's chapter menu both read as "we do not know where frotz is" — for
        // five turns running, while the question sat on the screen the whole time (2026-09-19
        // UAT, where both games were called unplayable). The test is now made against everything
        // the companion can read, so the silence rule is unchanged and the blind spot is gone.
        if (!turn.AwaitingCommand && !string.IsNullOrWhiteSpace(showed))
        {
            _triggers.NoteTurn();
            if (_gamePromptsAnswered < _config.MaxGamePromptAnswers)
            {
                _gamePromptsAnswered++;
                _log.Event(
                    "game_prompt",
                    new
                    {
                        turn = turn.Turn,
                        attempt = _gamePromptsAnswered,
                        input = turn.Input,
                        showing = Excerpt(showed),
                    });
                await RespondAsync(
                        GamePromptEvent(turn),
                        "game-prompt",
                        cancellationToken,
                        wantMovements: null,
                        // Answering a prompt the story is waiting on is a move, and the hold locks
                        // moves (B1). The game simply waits — which is what "wait while I write
                        // this" means when there is a form on the screen.
                        allowAct: !_state.IsHeld)
                    .ConfigureAwait(false);
                return;
            }

            _log.Warn(
                "the game is still waiting for an answer; leaving it to the player",
                new { attempts = _gamePromptsAnswered, input = turn.Input });
        }
        else
        {
            _gamePromptsAnswered = 0;
        }

        // A line the player spoke while this turn was still in flight. It is answered
        // now, because now there is something to answer it with (§iteration 01 §5): the
        // result is in the transcript, a moment old and no longer a guess.
        if (_repliesWaitingForTheTurn.Drain() is { } waiting)
        {
            var answered = await RespondAsync(
                    $"{waiting.LatestEvent}\n(your command has now produced its result — what is on screen is the answer; reply to the player now, and do not claim to be still waiting.)",
                    waiting.Purpose,
                    cancellationToken,
                    wantMovements: null,
                    // The reply itself is speech; a move inside it is not (B1).
                    allowAct: !_state.IsHeld)
                .ConfigureAwait(false);

            if (answered)
            {
                // The held reply carries the line that was waiting, so the same instruction test is
                // applied to it here: this drain is the route a greeting took to become an errand
                // while its game turn was still in flight.
                TakeTheInstructionInHand(waiting.Purpose, waiting.Work);
            }

            return;
        }

        // An errand in hand: this turn was a step of it, so keep going rather than
        // asking whether to (§iteration 01 §3).
        if (_mission is { Active: true })
        {
            // Over the session's cost bound the errand stops where it stands: an unbounded errand
            // that is also over budget is the one combination with nothing holding it (04_companion.md §6 (`C42.7`)).
            if (await OverTheSessionCostBoundAsync().ConfigureAwait(false))
            {
                EndMission("the session is over its cost bound");
                return;
            }

            if (agentAuthored && !string.IsNullOrWhiteSpace(turn.Input))
            {
                _mission.NoteStepTaken(turn.Input);
            }

            _mission.NoteFinding(Excerpt(showed));
            _log.Event(
                "mission_step",
                new
                {
                    step = _mission.StepsTaken,
                    budget = _mission.Budget,
                    input = turn.Input,
                    agent_authored = agentAuthored,
                });

            // And then the pause (04_companion.md §6 (`C42`–`C47`)). A hold stops the errand *carrying on*, and this
            // is the second of the two places it would otherwise do so — the tick is the other.
            // The step that just landed is counted and recorded above before this returns, because
            // a paused errand is not a forgotten one: the work is where it stood, and the tick
            // after the space comes off the front of the line takes the next step.
            if (_state.IsHeld)
            {
                return;
            }

            // And the stretch that has found nothing new, put to the companion as something to decide
            // about (`C39.4`, `§25` item 4). The fact already rides in the constraint of every call —
            // but a sentence nobody acts on can ride for a whole errand, and since the player's own
            // errand stopped having a step bound this is the only thing watching one. The step is
            // still a step and may still move: what changes is that it is *asked* rather than only
            // told, which is the shape `C39.2` records the failure of getting wrong.
            //
            // Once per window, because the fact stays true for as long as the stretch lasts. A consult
            // on every step would be the harness talking over the companion instead of telling it
            // something, and the next window is the next chance for the news to be new again.
            var brief = _mission.Brief();

            // A circle is asked about here too, and this is the whole of why: an errand answers a turn
            // before the triggers are consulted, so a companion circling *while carrying out a job*
            // could not be asked about it at all. Measured 2026-09-30: about four minutes circling
            // Hilltop, doing a different pointless thing each time, with the consult firing once and
            // late because the errand owned the floor (`C39.6`).
            if (_state.StuckAction() is { } circling && _consultedAbout != circling.Action)
            {
                _consultedAbout = circling.Action;
                _log.Event("circle_consulted", new { action = circling.Action, count = circling.Count, purpose = "mission-step" });
                brief += "\n\n" + GoingInCirclesAsk();
            }

            // And the stretch that has found nothing new, put to the companion as something to decide
            // about (`C39.4`, `§25` item 4). The fact already rides in the constraint of every call —
            // but a sentence nobody acts on can ride for a whole errand, and since the player's own
            // errand stopped having a step bound this is the only thing watching one. The step is
            // still a step and may still move: what changes is that it is *asked* rather than only
            // told, which is the shape `C39.2` records the failure of getting wrong.
            //
            // Once per window, because the fact stays true for as long as the stretch lasts. A consult
            // on every step would be the harness talking over the companion instead of telling it
            // something, and the next window is the next chance for the news to be new again.
            if (StagnationWorthConsulting(_status?.Turn ?? _lastTurnSeen) is { } stretch)
            {
                _log.RecordStagnation("mission-step", stretch.Turns, stretch.Moved);
                brief += "\n\n" + GoingNowhereAsk(stretch.Prose);
            }

            await RespondAsync(brief, "mission-step", cancellationToken, thisIsWork: true)
                .ConfigureAwait(false);
            return;
        }

        var decision = _triggers.OnTurn(turn.Output, agentAuthored);

        // Going in circles outranks a fresh observation: grinding is the one thing
        // the companion must never do quietly (§10.6).
        //
        // The consult may act (04_companion.md §5 (`C29`–`C41`)). Telling it that it is grinding and forbidding it
        // to move is how the 09-17 evening stopped working: the player said `go`, and two seconds
        // later was asked "want me to make the move?" — because this branch had already made the
        // turn talk-only. A guard that answers a circle with silence is not a guard, it is a stop.
        var circles = false;
        var goingNowhere = false;
        if (_state.StuckAction() is { } stuck && _consultedAbout != stuck.Action)
        {
            _consultedAbout = stuck.Action;
            decision = _triggers.GoingInCircles(stuck.Action, stuck.Count);
            circles = true;
        }
        else if (StagnationWorthConsulting(_status?.Turn ?? _lastTurnSeen) is { } stretch)
        {
            // The macro version, ranked below the circle for the reason `C39` gives: a repeat is one
            // action to stop, and a stretch is a session to reconsider. Both may move.
            decision = _triggers.GoingNowhere(stretch.Prose);
            _log.RecordStagnation(decision.Reason.ToString().ToLowerInvariant(), stretch.Turns, stretch.Moved);
            goingNowhere = true;
        }

        if (!decision.Speak)
        {
            _triggers.NoteTurn();
            return;
        }

        // Every unprompted trigger is talk, not a move. The companion moves when it was asked,
        // as a step of an errand, when the game is asking something only it can read, or once
        // on the idle allowance — and never twice running on its own.
        //
        // That last clause is the fix for "it runs off and plays by itself". This reply used to
        // be authorised to act (the companion had just acted, so reporting back was the trigger,
        // and a report may contain an act), which made act -> report -> act an unbroken chain:
        // measured live, it answered the player and then played six more turns alone.
        await RespondAsync(
                LatestEventFor(turn, decision),
                decision.Reason.ToString().ToLowerInvariant(),
                cancellationToken,
                wantMovements: null,
                allowAct: (circles || goingNowhere) && !_state.IsHeld)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Read the game's reaction to a command the companion sent (§10.4).
    /// <para>
    /// Only agent-authored turns are considered. The player may type whatever they
    /// like — including a verb the parser refuses, on purpose — and nothing here
    /// should quietly blacklist a word the player uses themselves.
    /// </para>
    /// <para>
    /// A refusal is written down as an <em>attempt</em> — the line and the sentence that came back
    /// — and only a refusal that is a verdict on the word is remembered as one. Measured
    /// 2026-09-21: reading every refusal as a verdict on the verb recorded <c>read</c>,
    /// <c>examine</c>, <c>take</c>, <c>put</c> and <c>down</c> as words this game had refused, from
    /// five sentences about the objects ("You can't see any letter here!"), and the companion
    /// then worked around them for sixteen hours in a game whose parser holds all five.
    /// </para>
    /// <para>
    /// A verdict on the word is a verdict on <em>the word the game named</em>, not on the first
    /// word of the line. Measured 2026-09-25: the other reading cost five good verbs in one evening
    /// of Moonmist — <c>examine</c>, <c>take</c>, <c>open</c>, <c>ask</c> and <c>say</c> — because
    /// <c>examine sliding doors</c> is a verdict on <c>doors</c>. See
    /// <see cref="ParserFeedback.NamedWord"/>.
    /// </para>
    /// </summary>
    private void LearnFromParser(TurnCompletePayload turn, bool agentAuthored)
    {
        if (!agentAuthored || string.IsNullOrWhiteSpace(turn.Input))
        {
            return;
        }

        var word = ParserFeedback.Verb(turn.Input);

        if (ParserFeedback.LooksLikeRejection(turn.Output))
        {
            var wordUnknown = ParserFeedback.LooksLikeWordUnknown(turn.Output);
            if (_feedback.NoteAttempt(ParserFeedback.TheAttempt(turn.Input, turn.Output, wordUnknown)))
            {
                RememberParserAttempt(turn.Input, turn.Output, wordUnknown);
            }

            _log.Warn(
                wordUnknown
                    ? "the game's parser has no word for that command, so that is remembered"
                    : "the game's parser refused a command; the attempt is kept as history and no word is taken off the table",
                // `named` is the word the game put in quotes, which is the word the verdict is
                // actually about (`[I don't know the word "doors."]`). It is null when the refusal
                // names no word at all, and then `verb` — the line's first word — is what is
                // recorded. Both are logged because the pair is what tells a reader which happened:
                // measured 2026-09-25, the old line said `verb: examine` for `examine sliding doors`
                // and read as a verdict on `examine` rather than on `doors`.
                new
                {
                    verb = word,
                    named = ParserFeedback.NamedWord(turn.Output),
                    input = turn.Input,
                    word_unknown = wordUnknown,
                });
            return;
        }

        if (word is not null)
        {
            // Kept for this session's note only. Acceptance is not written down: the dictionary
            // arrives from the core at every load and says the same thing better, and a word
            // remembered as accepted is one more line in a file nobody needs it in.
            _feedback.NoteAccepted(word);
        }
    }

    /// <summary>What this game's parser said back last time, or nothing when the store cannot be read.</summary>
    private IReadOnlyList<ParserAttempt> RememberedAttempts(string stem)
    {
        try
        {
            _vocabulary ??= new ParserVocabularyStore(_memory.StateDirectory);
            return _vocabulary.Read(stem);
        }
        catch (Exception ex)
        {
            _log.Warn("this game's parser memory could not be read", new { stem, error = ex.Message });
            return [];
        }
    }

    /// <summary>
    /// A line with no letter in it — the player prodding, and not a question to be reasoned about.
    /// <para>
    /// It is not the goal of an errand either: measured 2026-09-25, a bare `?` became the goal of an
    /// errand three times in one evening (`mission_start` with `goal: "?"`), which is a standing
    /// order bought with a punctuation mark.
    /// </para>
    /// </summary>
    private static bool IsAProd(string text) =>
        !text.Any(char.IsLetter) && text.Contains('?', StringComparison.Ordinal);

    /// <summary>
    /// What the harness says when the player prods it: where it is, how far the game has got, and
    /// whether anything of its own is in flight — the three things a person asks with one keypress.
    /// <para>
    /// Every fact here is one the harness holds already, so the answer is free and immediate. It is
    /// deliberately not in the companion's voice: it is the harness reporting, the way a "still
    /// working" line is, and the companion's own explanation comes when it next speaks.
    /// </para>
    /// </summary>
    private string TheHarnessAnswer()
    {
        var where = _status?.Location is { } room
            ? $"I'm in room {room}."
            : "The interpreter has not said which room I'm in.";

        var got = _status is { Turn: not 0 } status
            ? $" The game's last turn was {status.Turn}."
            : string.Empty;

        var busy = _pass is not null
            ? " I'm in the middle of a reply."
            : " Nothing of mine is running.";

        return $"Here. {where}{got}{busy}";
    }

    /// <summary>Remember one attempt — what was typed and what came back (§10.4).</summary>
    private void RememberParserAttempt(string typed, string? output, bool wordUnknown)
    {
        try
        {
            _vocabulary ??= new ParserVocabularyStore(_memory.StateDirectory);
            _vocabulary.Append(
                _notebook.GameStem,
                ParserFeedback.TheAttempt(typed, output, wordUnknown));
        }
        catch (Exception ex)
        {
            _log.Warn("a parser attempt could not be remembered", new { input = typed, error = ex.Message });
        }
    }

    private async Task HandlePlayerLineAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var chat = envelope.PayloadAs<ChatPayload>();
        var text = chat?.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // A line with no word in it is somebody prodding a companion they think has gone quiet, and
        // the answer is three facts the harness already holds (03_session.md §7 (`S44`)).
        //
        // Measured 2026-09-29: four `?`s in one window cost two to five model calls each and not one
        // of them was answered, while the player typed *"are you lost?"* into the silence. This reply
        // cannot be missed and cannot be paid for, because it never reaches the model.
        if (IsAProd(text))
        {
            await NotifyPlayerAsync(TheHarnessAnswer()).ConfigureAwait(false);
            return;
        }

        NoteThePlayerIsHere();
        _triggers.NotePlayerSpoke();
        _transcript.AddPlayerLine(text);
        _log.Event("player_line", new { text });

        // While the model is away, what the player says is kept aside for the catch-up: they are the
        // half of the evening the screen cannot show, and the companion has to hear them when it
        // comes back (04_companion.md §10 (`C56`–`C60`)). Bounded, because an outage is not a reason to grow a list for
        // ever — the newest lines are the ones that still matter, so the oldest go first and the
        // count of what went is kept for the window to be honest about.
        if (_awaySince != DateTimeOffset.MinValue)
        {
            if (_awayPlayerLines.Count >= AwayPlayerLineLimit)
            {
                _awayPlayerLines.RemoveAt(0);
                _awayPlayerLinesDropped++;
            }

            _awayPlayerLines.Add(text);
        }

        // Player corrections are captured in code, before any model call: "player
        // corrections always win" must not depend on the model noticing (§10.5).
        if (NotebookStore.LooksLikeCorrection(text))
        {
            _notebook.AppendPlayerCorrection(text);
            _log.Event("player_correction", new { text });
        }

        // A proposal waiting on an answer changes what a long line means: "yes - keep
        // going, your job is to scout the open locations" is consent, and the errand in
        // it is real (§iteration 01 §3).
        //
        // The game's dictionary comes with it when the core has read one: it is what tells a
        // one-word command from a sentence that opens with a verb (issue #40). An unread
        // dictionary passes nothing, so an advisory thing that is missing cannot silence the
        // player's own commands.
        Func<string, bool>? knowsWord = _feedback.KnownWords > 0 ? _feedback.Knows : null;
        var classification = UtteranceClassifier.Classify(
            text,
            _state.IsAwaitingGrant,
            knowsWord,
            _config.ShortPhraseWords,
            _config.MaxCommandWords);
        _log.Event(
            "classification",
            new { text, kind = classification.Kind.ToString().ToLowerInvariant(), reason = classification.Reason });

        // An errand is handed over conversationally, whatever else the line also is.
        if (UtteranceClassifier.IsDelegation(text))
        {
            StartMission(text, "delegated by the player");
        }

        // "don't you think…" is thinking aloud at the companion, not a command: it is
        // expected to weigh the idea and update its own notes (§iteration 01 §4).
        var suggestion = UtteranceClassifier.IsSuggestion(text);
        if (suggestion)
        {
            _log.Event("suggestion", new { text });
        }

        switch (classification.Kind)
        {
            case UtteranceClass.Grant:
                if (_state.Approve() is { } approved)
                {
                    _state.NotePlayerLine(text, grantedOrVetoed: true);
                    _log.Event("grant", new { action = approved });
                    _log.Info("the player approved the proposal", new { action = approved });
                    await SendGameInputAsync(approved, byAgent: true, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Nothing pending: the player is approving something in the
                // conversation, so the model may act without re-announcing (§10.1).
                _pendingApproval = true;
                break;

            case UtteranceClass.Veto:
            {
                var cancelled = _state.Veto(DateTimeOffset.UtcNow);
                _state.NotePlayerLine(text, grantedOrVetoed: true);
                _log.Event("veto", new { action = cancelled });

                // A veto is the one thing that cancels an errand outright: the player
                // grabbed the wheel hard enough to stop the work, not to steer it.
                var stopped = _mission is { Active: true } ? _mission.Goal : null;
                if (stopped is not null)
                {
                    EndMission("cancelled by the player");
                }

                var latest = cancelled is null
                    ? $"the player said: {text}"
                    : $"the player said: {text}\n(you had proposed `{cancelled}`; it is dropped, and it stays dropped for a while)";
                if (stopped is not null)
                {
                    latest += $"\n(and the errand you were on — \"{stopped}\" — is cancelled)";
                }

                await RespondAsync(latest, "veto", cancellationToken).ConfigureAwait(false);
                return;            }
        }

        _state.NotePlayerLine(text, grantedOrVetoed: false);

        // A suggestion is put to the companion as something to weigh, so it answers in
        // its own words and updates its notes rather than merely replying to chatter.
        var latestEvent = suggestion
            ? $"player suggests: {text}\n(weigh it properly: if you agree, fold it into your notes and your plan; if not, say why)"
            : $"player said: {text}";

        // How the harness read the line, as evidence the model may overrule (§4.17). This is
        // the channel that replaces the classification gate: the tables are wrong sometimes —
        // every spoken errand starts mid-paragraph — and the model has just read the line with
        // the whole conversation in front of it, so it gets to say so. A suggestion already
        // carries its own framing, so it does not need this too.
        if (!suggestion)
        {
            var read = ReadBack(classification);
            if (read.Length > 0)
            {
                latestEvent += "\n" + read;
            }
        }

        // A player line is the best moment there is to notice that something they said is
        // worth remembering beyond this game (issue #27).
        if (NotebookStore.LooksLikeCorrection(text))
        {
            _memory.AppendPlayerCorrection(text);
            _log.Event("memory_correction", new { text });
        }

        var purposeNow = suggestion ? "suggestion" : "player-line";
        var answered = await RespondOrWaitAsync(latestEvent, purposeNow, cancellationToken, work: text)
            .ConfigureAwait(false);

        if (answered)
        {
            TakeTheInstructionInHand(purposeNow, text);
        }
    }

    /// <summary>
    /// The harness's own read of a player line, handed to the model with permission to
    /// disagree (§4.17).
    /// <para>
    /// Written as evidence rather than as a verdict on purpose: it names what the tables saw,
    /// so the model can overrule the <em>reading</em> rather than only the conclusion — and
    /// the ordinary-talk case says outright that the tables are often wrong, because that is
    /// the case that made this necessary. Measured, 2026-09-16: a four-step errand spoken
    /// mid-paragraph was read as chat because its first word was "ok", and the companion —
    /// which had understood it perfectly and said so — was not allowed to act on its own
    /// reading. The model's correction is a call (<c>errand</c>), never prose: the harness has
    /// to be able to act on it.
    /// </para>
    /// </summary>
    private static string ReadBack(Classification classification) => classification.Kind switch
    {
        UtteranceClass.Chat when Vocabulary.WasACommand(classification.Reason) =>
            "(the harness read that as a command for the game — your friend typing at the parser "
            + "rather than talking to you. If that is what they meant, play it; if it was talk, "
            + "answer it.)",
        UtteranceClass.Chat =>
            "(the harness read that as ordinary talk: no question mark, no plan phrase, no "
            + "command it knows. It often misses a job that is spoken mid-paragraph. If this was "
            + "one, say so by calling `errand` with the job in your own words.)",
        UtteranceClass.Steer =>
            "(the harness read that as a plan of theirs: if your reply moves, it becomes the "
            + "work in hand. If it read that wrong, say what you think it was.)",
        UtteranceClass.Question =>
            "(the harness read that as a question for you: answer it. If it was really a job "
            + "they handed over, `errand` is the call for that.)",
        UtteranceClass.Grant =>
            "(the harness read that as a yes. Nothing of yours was pending, so there was nothing "
            + "to approve — take it as a go-ahead for what you were saying.)",
        _ => string.Empty,
    };

    /// <summary>
    /// The player's instruction becomes the work in hand when it earns a move.
    /// <para>
    /// Not a new mechanism: it is the errand (§5.3) started by the words that were actually
    /// said, so it inherits the errand's bound and the errand's ending — a reply that moves
    /// nothing ends it, the budget ends it, a veto cancels it. What it is not is a licence to
    /// play on: it starts only on a move the player's own line produced, and never while
    /// another errand is already running.
    /// </para>
    /// <para>
    /// This is the fast path (§4.17): the model can start an errand by declaring it, which is
    /// what covers every line the tables cannot read — a job spoken mid-paragraph, or one in a
    /// language the tables were never written in. What the tables decide is whether an errand
    /// starts <em>without a call</em>; they no longer decide whether it starts at all.
    /// </para>
    /// <para>
    /// Measured live on 2026-09-14: asked for the post office, the companion took one step and
    /// waited at every gate and every door for the player to say "go on", because the rule it
    /// was obeying — and the rule the harness was enforcing — was one move per exchange. One
    /// thing asked for is often several commands: "examine everything in the room" is five, and
    /// a walk across the map is three.
    /// </para>
    /// </summary>
    private void TakeTheInstructionInHand(string purpose, string? asked)
    {
        if (purpose is not ("player-line" or "suggestion" or "player-line-batched"))
        {
            return;
        }

        if (_mission is { Active: true } || string.IsNullOrWhiteSpace(asked))
        {
            return;
        }

        // Only a line that asked for something becomes the work in hand (issues #31 and #41).
        // The whole of the rule is in UtteranceClassifier.ReadsAsAnInstruction; what matters here
        // is that the companion is no longer given a standing order by a line that merely happened
        // to produce one move.
        //
        // The bias is deliberate and it is the quieter failure of the two. An errand that does not
        // start costs the player a word ("keep going") to fix; an errand that starts on a joke
        // costs eight moves and the player watching the companion work without them. Since §4.17
        // the first cost is bounded from the other side too: a line the tables cannot read is one
        // the model can still declare, and this branch only says the errand did not come from here.
        if (!UtteranceClassifier.ReadsAsAnInstruction(asked))
        {
            _log.Info("the tables read that as talk; no errand started from the line", new { line = asked });
            return;
        }

        StartMission(asked, "the player's instruction");
    }

    /// <summary>
    /// The player has asked the companion to type one line (03_session.md §8.1 (`S53`)).
    /// <para>
    /// This is the social route — <i>"can you type ……"</i> — so the companion gets to answer
    /// it. It may object once, and only for a reason worth a friend's time, and then it types
    /// it. Nothing here is a bypass: a request can be weighed, and that is the whole reason
    /// <c>/direct</c> exists beside it.
    /// </para>
    /// </summary>
    private async Task HandleHandoverAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var line = envelope.StringField("line")?.Trim() ?? string.Empty;
        if (line.Length == 0)
        {
            return;
        }

        _log.Event("handover", new { line });
        _transcript.AddPlayerLine($"(asks me to type: {line})");
        NoteThePlayerIsHere();

        if (_llm is null)
        {
            // No model — but typing is not a model's job. The player asked for one command and
            // it is mechanical, so do it rather than leave them waiting on a brain that is not
            // there.
            _log.Info("handover with no model configured; typing it", new { line });
            await SendGameInputAsync(line, byAgent: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var latest = new StringBuilder();
        latest.Append("your friend is asking you to type this for them: ").AppendLine(line);
        latest.Append(
            "They are handing you the keyboard for one command, the way somebody says \"can you "
            + "type …\". Type it, in one act, and say what you are doing in one short line. You may "
            + "refuse once, and only for a reason worth a friend's time — you can see it undoes "
            + "something, wastes the move, or you know something they do not. Say why in your own "
            + "words and leave the act out; they will tell you again if they mean it, and then you "
            + "type it.");

        await RespondOrWaitAsync(latest.ToString(), "handover", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Is a game turn in flight — either one the companion sent, or one the player forced
    /// with <c>/direct</c>? Prose written now would be prose written blind.
    /// </summary>
    private bool TurnInFlight => _awaitingTurn || _state.PlayerDirectedFlight;

    /// <summary>
    /// Reply now, or hold the reply until the turn in flight lands.
    /// <para>
    /// Only the *prose* waits. State changes already made stand: a veto has cancelled, a
    /// delegation has started an errand, a grant has been dispatched to the game's queue.
    /// </para>
    /// </summary>
    /// <summary>
    /// The player's standing danger level, in one sentence, in every reply (§4.7).
    /// <para>
    /// The code gate and the prompt have to say the same thing: a companion told to ask about
    /// everything while the harness waves everything through — or the reverse, which is the
    /// direction §3.10 caught — is a companion whose judgement and whose instructions disagree.
    /// The sentence is written as a fact about the player, not as a harness rule, because the
    /// companion's own prose rules (say what you are about to do; ask before something worth
    /// asking about) are still its own. What the level changes is where the harness draws the
    /// hard line, and that is worth knowing.
    /// </para>
    /// </summary>
    private string RiskNote() => _standingRisk switch
    {
        RiskLevel.Low =>
            $"{_context.PlayerName} has set the danger level to low: anything that changes the "
            + "world is theirs to approve, so say what you propose and wait. Looking, reading and "
            + "walking are yours to do.",
        RiskLevel.High =>
            $"{_context.PlayerName} has set the danger level to high: nothing has to be proposed "
            + "first — the decision is yours and so is living with it. Say what you are about to "
            + "do when it is the kind of thing they would want to know about.",
        _ =>
            $"{_context.PlayerName} has set the danger level to medium: the things they would "
            + "regret — spending, breaking, burning, attacking, giving away — stop for their "
            + "blessing; everything else you may just do.",
    };

    private async Task<bool> RespondOrWaitAsync(
        string latestEvent,
        string purpose,
        CancellationToken cancellationToken,
        string? work = null)
    {
        if (TurnInFlight)
        {
            _repliesWaitingForTheTurn.Hold(latestEvent, purpose, work);
            _log.Info("holding a reply until the turn in flight lands", new { purpose });
            return false;
        }

        // A line the player spoke while their box begins with a space is answered — the hold locks
        // acting, not talking (B1) — and the answer may not carry a move. This path is where the
        // complaint came from: with the hold as silence, a player who started a line with a space
        // and pressed return got nothing at all, and could not tell listening from stuck.
        return await RespondAsync(
                latestEvent,
                purpose,
                cancellationToken,
                wantMovements: null,
                allowAct: !_state.IsHeld)
            .ConfigureAwait(false);
    }

    private async Task HandleSystemAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var system = envelope.PayloadAs<SystemPayload>();
        if (system is null)
        {
            return;
        }

        switch (system.Event)
        {
            case "player_direct":
                _state.NotePlayerDirect(started: true);
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;

            case "player_key":
                // The player answered a prompt that is not a command — a menu, a `press any key` —
                // from a pane. It is the same fact as `player_direct` and is treated the same way:
                // somebody is at the keyboard, so nothing of ours belongs in flight behind them.
                _state.NotePlayerDirect(started: true);
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;

            case "risk_level":
            {
                // The player set the standing danger level (§4.7). Both dials move: the gate
                // below, and the sentence in every constraint block after this one.
                _standingRisk = RiskLevels.Parse(system.Detail);
                _log.Event("risk_level", new { level = RiskLevels.Label(_standingRisk) });
                _log.Info("the player set the danger level", new { level = RiskLevels.Label(_standingRisk) });
                break;
            }

            case "debug":
            {
                // The tool trace (03_session.md §4.3 (`S32`–`S34`)). Nothing to interpret: on means send the lines,
                // off means the panel is the player's own again.
                _debug = string.Equals(system.Detail, "on", StringComparison.OrdinalIgnoreCase);
                _log.Info("the tool trace was set", new { on = _debug });
                break;
            }

            case "player_connected":
            {
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                var decision = _triggers.OnPlayerJoined();
                if (decision.Speak)
                {
                    // A greeting is social, so it may not act. Measured: turning up produced a
                    // welcome and then `read sign`, which the player had not asked for — and a
                    // reconnect is a thing that happens mid-evening, not an invitation to play.
                    await RespondAsync(
                        "the player has just connected to the game again; greet them briefly and, if your notebook says where you were, remind them in one line.",
                        "player-joined",
                        cancellationToken,
                        wantMovements: null,
                        allowAct: false).ConfigureAwait(false);
                }

                break;
            }

            case "autosave":
                // §5.4: an auto-save snapshots the game and the companion together,
                // so this is the moment to make sure the long-horizon memory is on
                // disk alongside the checkpoint.
                _log.Event(
                    "transcript_tail",
                    new
                    {
                        detail = system.Detail,
                        turns = _transcript.Items
                            .TakeLast(_config.TailTurnsForAutosave)
                            .Select(item => new { role = item.Role.ToString().ToLowerInvariant(), text = item.Text })
                            .ToList(),
                    });
                _log.Info("auto-save: notebook is on disk", new { notebook = _notebook.FilePath, over_budget = _notebook.OverBudget(_notebook.Read()) });
                break;

            case "game_changed":
            {
                var stem = Sanitise(system.Detail);
                if (string.Equals(stem, _notebook.GameStem, StringComparison.Ordinal))
                {
                    // Same game: this is the core telling us something it already told
                    // us (the title, or a redundant boot event). Nested in a switch, so
                    // no transcript needs clearing and the notebook stays bound.
                    _log.Event("system", new { @event = system.Event, stem, no_op = true });
                    break;
                }

                var leaving = _notebook.GameStem;
                _log.Info("the game changed", new { from = leaving, to = stem });

                // Leaving a game is exactly the moment to write down what is worth keeping
                // (issues #22 and #27): the notebook for this game is finished, and the
                // memories should carry the evening rather than whatever the last turn
                // happened to be about.
                //
                // It runs *here*, before the notebook is pointed at the new game, and the order
                // is the whole of the fix. Measured: with this pass on the far side of the
                // retarget, a brief that said "you have just left witness" ran with catapole's
                // notebook already bound, and it duly wrote about witness into catapole's
                // notebook — the one file the per-game split exists to keep clean. The pass
                // belongs to the game that is ending, so it runs while that game is still the
                // one the notebook, the transcript and the observations all point at.
                //
                // A game nobody has taken stock of gets no pass: a fresh boot should not spend
                // a model call on an empty notebook.
                if (_reconciledFor is not null &&
                    !string.Equals(_reconciledFor, leaving, StringComparison.Ordinal))
                {
                    await ReconcileAsync($"your evening with {leaving} has just ended", cancellationToken)
                        .ConfigureAwait(false);
                }

                // The errand belonged to the game being left, and nothing used to end it. Measured
                // in the 2026-09-20 UAT: a walk across wishbringer was still in hand three turns
                // into bureaucracy, and the companion was still stepping an errand whose goal named
                // a story that was no longer on the screen. It is ended here, on the near side of
                // the retarget and after the reconcile pass above — that pass is about the evening
                // that is ending, and it should be able to say what was in flight when it ended.
                EndMission($"the game changed: this errand belonged to {leaving}");

                _notebook.Retarget(stem);
                _reconciledFor = stem;
                LoadGameBrief(stem);
                _transcript.Clear();
                _state.ResetForNewGame();
                // The status line belongs to the game that drew it: another story has another
                // version, another bar and another score, and a stale reading here would be quoted
                // to the player as where this game stands. Both are cleared, and both are refilled
                // by the new game's vocabulary and its first turn.
                _status = null;
                _namedLocation = null;
                _locationName = null;
                _lastExamined = string.Empty;
                _zVersion = null;
                // Another story has another parser, so what this one refused is
                // worthless here.
                _feedback.Clear();
                _consultedAbout = null;
                _awaitingTurn = false;
                _pendingApproval = false;
                _gamePromptsAnswered = 0;
                _turnsSinceReconcile = 0;
                _repliesWaitingForTheTurn.Clear();
                // The turn number belongs to the game (02_game_boundary.md §6 (`G27`–`G34`)), so the last game's
                // high-water mark is meaningless here — and worse, harmful: it is what a
                // catch-up asks this game's record for, forward from. Left standing, the first
                // quiet spell after a switch would read the new story's turns from a number the
                // old story's evening had reached.
                _lastTurnSeen = -1;
                _awayFromTurn = -1;
                // A note about a fork in a game we have just left is a note about the wrong game.
                _forkNote = null;
                // A9: a line aimed at the story we have just left is not in flight anywhere.
                ForgetTheLineInFlight();

                _log.Event("system", new { @event = system.Event, stem });
                break;
            }

            case "save_position":
            {
                // B7/S70: which save was loaded and where the core's note says it was taken, read
                // from the record beside the file the interpreter opened. It arrives just before the
                // `restore` that it belongs to, so the fork note is built with it; if the order ever
                // changes, the note is amended instead of losing the fact. The name is the handle
                // onto the rest of that save's note, which the saves tool can read back.
                _restoredPosition = system.Detail;
                if (_forkNote is { } open)
                {
                    _forkNote = open + $" Where you are is not a mystery this time: {system.Detail}.";
                }
                _log.Info("the save says which one it is and where it was taken", new { detail = system.Detail });
                break;
            }

            case "restore":
            {
                // The world has been rewound to a save: the same attempt at the same game, standing
                // somewhere else (02_game_boundary.md §6 (`G27`–`G34`)). Everything after that save did not happen on this
                // branch, so the transcript goes — and the notebook stays, because what the companion
                // learned about the *game* is still true of the game. What is not true is where it
                // stood and what it was carrying, and that is what the note below says.
                var forgotten = _transcript.Count;
                _transcript.Clear();
                _state.ResetForNewGame();
                _consultedAbout = null;
                _awaitingTurn = false;
                _pendingApproval = false;
                _refusedActs = 0;
                _gamePromptsAnswered = 0;
                _repliesWaitingForTheTurn.Clear();
                // Where the companion stood belongs to the branch that was just replaced. The room
                // the story last named, the clock, the score and the last thing examined are the
                // old world's facts, and this reply is built before any turn of the restored world
                // has landed — a name carried onto a different room is worse than no name at all,
                // which is why `game_changed` clears them too. The restored world's first turn
                // refills them.
                _status = null;
                _namedLocation = null;
                _locationName = null;
                _lastExamined = string.Empty;
                // The turn number resumes from the save's own record, which is behind where the
                // high-water mark had got to.
                _lastTurnSeen = -1;
                _awayFromTurn = -1;
                if (_mission is { Active: true })
                {
                    EndMission("the world was rewound to a save");
                }

                // Who went back: three ways a restore is caused, and only one is ours — the
                // companion typed `restore` into the game, the player forced one (`.restore`,
                // `/direct restore`), or the player ran the core's own `/restore` (bare or named).
                // The line still in flight is the tell for the first, read before the fork forgets
                // it; every other restore reached the world through the player's hands.
                var weTypedIt = _lineInFlight is { } inFlight
                    && inFlight.Trim().Equals("restore", StringComparison.OrdinalIgnoreCase);

                // A9: the world is a different world now, so a repeat is a decision rather than
                // the same one twice.
                ForgetTheLineInFlight();

                _forkNote =
                    (weTypedIt
                        ? "The world has just forked: you went back to a save, so this is the same "
                        : "The world has just forked: the player has gone back to a save, so this is the same ")
                    + "attempt at the same game, standing where that save was. Everything after it did "
                    + "not happen on this branch — the record from that moment on is not what you are "
                    + "standing in. What you know about the game still holds: the map, the solutions, the "
                    + "dead ends, the way the story behaves. "
                    + (_restoredPosition is { Length: > 0 } position
                        // B7: the core read the note beside the save file, so where the companion is
                        // standing is a fact it is handed rather than one it has to buy with a look.
                        // What does not hold is what it was carrying, which no save records.
                        ? $"Where you are is not a mystery this time: {position}. What does not hold is "
                            + "what you were carrying. "
                        : "What does not hold is where you were and what you were carrying. ")
                    + "Worth one line to the player saying so, and then carry on from the screen.";
                _restoredPosition = null;

                // The world forked (`S69`), so the document MUST be rebuilt under the new generation.
                // The fold is what gives every place and move its generation, and a document left on the
                // old one is a map of a timeline this run has already disproved (`M13.2`) — the whole
                // reason position is stamped at all. Measured in the UAT of 2026-09-29: the run book
                // moved to generation 2, the record dropped 124 turns, and the document still said
                // generation 1 with `fork_turn: null`, because the fork was recorded and nothing rebuilt.
                RebuildTheWorld(_notebook.GameStem);

                _log.Event(
                    "system",
                    new
                    {
                        @event = system.Event,
                        detail = system.Detail,
                        forgotten_turns = forgotten,
                    });
                break;
            }

            case "restart":
            {
                // The run starts again; the companion is the same person (issue #26). So the
                // transcript goes — those turns happened in a run that no longer exists —
                // and the notebook stays, which is the entire difference from a `reset`.
                // Anything it learned about the player is untouched: repairing a game must
                // never be a way to forget them.
                var forgotten = _transcript.Count;
                _transcript.Clear();
                _state.ResetForNewGame();
                _consultedAbout = null;
                _awaitingTurn = false;
                _pendingApproval = false;
                _refusedActs = 0;
                _gamePromptsAnswered = 0;
                _repliesWaitingForTheTurn.Clear();
                // The world has gone back to its opening, so the finished run's facts — the room,
                // the clock, the score, the last thing examined — are facts about a world that no
                // longer exists. They go with the transcript; the opening's first turn refills them.
                _status = null;
                _namedLocation = null;
                _locationName = null;
                _lastExamined = string.Empty;
                // The story starts again, so its turn count starts again too (02_game_boundary.md §6 (`G27`–`G34`)).
                _lastTurnSeen = -1;
                _awayFromTurn = -1;
                if (_mission is { Active: true })
                {
                    EndMission("the game was restarted");
                }

                ForgetTheLineInFlight();

                // A new attempt begins here, so the document is rebuilt for the same reason a restore
                // rebuilds it: the run book has moved on and every stamp in the document is read out of
                // it.
                RebuildTheWorld(_notebook.GameStem);

                _log.Event(
                    "system",
                    new { @event = system.Event, detail = system.Detail, forgotten_turns = forgotten });
                _log.Info(
                    "the game was restarted; keeping everything we learned",
                    new { forgotten_turns = forgotten, notebook = _notebook.FilePath });

                // A restart is the best moment there is to write down what carried over.
                await ReconcileAsync($"{_notebook.GameStem} has just been started again", cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            case "game_exited":
                // The game is gone, so nothing is in flight, nothing is coming, and nothing of ours is
                // in a world that is no longer running (A9). All three of the world's facts go with
                // it:
                //
                // - The per-turn flags: `_awaitingTurn` is only half of `TurnInFlight`, and a
                //   `PlayerDirectedFlight` left standing outlives the game it belonged to, so every
                //   reply after it is held for a turn that cannot arrive — the same silence as the
                //   refusal that was reported as a verdict (measured 2026-09-24).
                // - The world itself: a pass may talk and may not act in a game that has stopped,
                //   which is now something `_state` says rather than something each caller has to
                //   remember. That is what keeps the missed-move repair from chasing a reply with
                //   *"make the move now"* in a game that is gone.
                // - The errand: a plan is a plan *in* a world. `restart`, `reset` and `game_changed`
                //   all end it for the same reason, and the notebook — everything learned about the
                //   game — is untouched by any of them.
                //
                // The wait itself is answered rather than dropped: the player is owed a word about a
                // session that has ended underneath them.
                _log.Warn("the game process exited", new { detail = system.Detail });
                _awaitingTurn = false;
                // A9: nothing of ours is in a game that is no longer running.
                ForgetTheLineInFlight();
                _state.NoteTurnCompleted();
                _state.NoteTheWorldIsGone();
                if (_mission is { Active: true })
                {
                    EndMission("the game exited");
                }

                await AnswerTheHeldRepliesAsync(
                        "The game has stopped, so the line that was sent will not produce an answer "
                        + "and there is nothing left to move in: say so plainly rather than waiting.",
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "lag":
                // We fell behind on game_output chunks. Harmless here: this loop
                // resynchronises on turn_complete, which is never dropped (§6.5).
                _log.Warn("the core reported lag; resynchronising on the next turn", new { detail = system.Detail });
                break;
            case "reset":
            {
                // A reset means "as if the game had never been played" (§5.4), so the
                // companion forgets the run as well as the file. Measured, and reported
                // by the player: without this it kept talking about people it had met "in
                // an earlier run", because the pre-reset turns were still in its context
                // and it duly reported them as its own notes. That is cheating.
                var forgotten = _transcript.Count;
                _transcript.Clear();
                _state.ResetForNewGame();
                _consultedAbout = null;
                _awaitingTurn = false;
                _pendingApproval = false;
                _refusedActs = 0;
                _gamePromptsAnswered = 0;
                _turnsSinceReconcile = 0;
                // Questions held for a run that no longer exists are not worth answering.
                _repliesWaitingForTheTurn.Clear();
                if (_mission is { Active: true })
                {
                    EndMission("the game was reset");
                }

                ForgetTheLineInFlight();

                // The parser is the same game's parser, so what it knows and what it has
                // refused both still stand.
                _log.Event(
                    "system",
                    new { @event = system.Event, detail = system.Detail, forgotten_turns = forgotten });
                _log.Info("the game was reset; forgetting the run", new { forgotten_turns = forgotten });
                break;
            }

            // Is anybody here? Its own curiosity is for an audience: this is two people at one
            // keyboard, so with nobody connected there is nobody to play *with* — and every idle
            // nudge restarts the idle clock, so a companion left alone plays the game through one
            // allowed move at a time, on somebody's account. Measured on the first live session:
            // eleven turns and a solved puzzle in the ten minutes nobody was connected.
            case "player_present":
                _playerPresent = true;
                NoteThePlayerIsHere();
                _log.Info("the player is here");                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;

            case "input_refused":
                // The core would not write the line into the game — a command for the interpreter
                // rather than for the game, or a repeat while nothing is moving (02_game_boundary.md
                // §4.3 (`G24`)). Nothing is coming back for it, so stop waiting for a turn that will
                // never arrive, **and answer what was held behind it**: the replies are held on,
                // precisely, the turn this line was going to produce, so a refusal that does not let
                // them go is a session that goes silent. Measured 2026-09-24: the wait outlived the
                // line, no model call was made again, and the player's next three lines went nowhere.
                _awaitingTurn = false;
                ForgetTheLineInFlight();
                _state.NoteTurnCompleted();
                _log.Warn("the core refused a line", new { detail = system.Detail });
                await AnswerTheHeldRepliesAsync(
                        "The line above was refused before it reached the game, so no answer to it "
                        + "is coming: what is on the screen now is what there is.",
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "catchup":
                // A player returning to a game has asked for it to be written up (05_memory.md §6 (`M27`–`M36`)).
                // The core has already decided there is something to write about and that somebody
                // is here to do it; this is the work itself, and it is the companion's own pass —
                // its own model call, reading everything the harness holds.
                _log.Event("report_asked", new { stem = system.Detail });
                _log.Info("a write-up was asked for", new { stem = system.Detail });
                await WriteUpTheGameAsync(system.Detail ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "player_absent":
                // Only a sitting that had somebody in it gets summarised. The core says "the player has
                // gone; wait for them" from boot as well — measured 2026-10-01, rows 14-17 of a fresh log,
                // before anyone had connected — and a summary of nothing is not a fact about a sitting,
                // it is a fact about a container starting. It also made the row a reader wants
                // ambiguous, which is the one thing a summary must not be.
                if (_playerPresent)
                {
                    // The sitting is over, so its numbers go down now rather than at the end of the
                    // process — which a container stop never reaches (`03_session.md` §9 (`S61`)).
                    _log.SittingEnded();
                }

                _playerPresent = false;
                _log.Info("the player has gone; the companion will wait for them");
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;

            case "checkpoint":
            case "checkpoint_failed":
            case "input_dropped":
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;

            case "key_wanted":
            {
                // The core stopped a line before it could reach the story (02_game_boundary.md §4.2–§4.3 (`G20`–`G24`)). At a
                // character prompt the first character of a line answers the prompt and the rest
                // are typed into whatever the story counts next — measured, 2026-09-20, where
                // `Fillmore` sent as a line put `ILLMORE` in Bureaucracy's licence field. So the
                // line never happened, and the companion has to be told, or it goes on believing
                // its command is in flight and waiting for an answer to it.
                //
                // Bounded like the game-prompt answers, and for the same reason: a model that
                // keeps answering a key prompt with a word must not be able to spend the evening
                // in a loop the player cannot see.
                //
                // The name is its own rather than `input_refused`, which is the other refusal —
                // the core declining to write a line that was *its* command rather than the
                // game's. The two need different answers from the companion, so they are two
                // events.
                _awaitingTurn = false;
                // A9: it never reached the game, so it is not in flight and a repeat is a fresh
                // decision rather than the same one twice.
                ForgetTheLineInFlight();
                // The per-turn flags go with it, which is what the other refusal (`input_refused`)
                // does too: the flight is over, and a companion still marked as having a line out
                // there reads whatever appears next as that line's answer. One rule, both refusals —
                // a line that never reached the game is not a flight, whichever end stopped it.
                _state.NoteTurnCompleted();
                if (_refusedLines >= _config.MaxGamePromptAnswers)
                {
                    _log.Warn(
                        "the harness has stopped several lines in a row; leaving the prompt to the player",
                        new { stopped = _refusedLines, detail = system.Detail });
                    break;
                }

                _refusedLines++;
                _log.Event("key_wanted", new { detail = system.Detail, stopped = _refusedLines });
                _log.Info("a line was stopped at a key prompt", new { detail = system.Detail });
                await RespondAsync(
                        $"{system.Detail}\n"
                        + "The game is waiting for a single key rather than a command, which is why "
                        + "your last line was not sent at all. Read the screen, work out what it is "
                        + "asking for, and answer it with `press_key` — one character, a digit, or a "
                        + "bare Return. Nothing on the screen has changed, so do not describe the "
                        + "game as having answered anything.",
                        "refused-line",
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            // The harness has stopped counting this session as play (02_game_boundary.md §4.3 (`G24`)): the counters are
            // frozen, or the same paragraph is coming back, or nothing the story was asked produced
            // anything. It is said in the core's own words and it is *not* a story fact — it is a
            // fact about the play, and that is why it is addressed to this side of the system. The
            // companion is the only thing here that can think above the game, so it is asked once,
            // immediately, to do exactly that: reason about how it is playing rather than about the
            // story, and either try something genuinely different or put the decision to the player.
            //
            // Once per episode, and the episode ends when the core says the world moved. Without the
            // bound, a model that answers every nudge with another attempt to think its way out
            // spends the evening talking to itself on the player's account — which is the failure the
            // whole section exists to prevent, one level up.
            case "stuck":
            {
                _stuckNote = system.Detail
                    + "\n\nThat is a fact about how this is being played, not about the story, and "
                    + "nothing in the story explains it — so do not answer it from your notes. Read the "
                    + "screen again as though you had just arrived at it, look for the thing you have "
                    + "been assuming, and either try something you have not tried or say plainly that "
                    + "you are out of ideas and ask your friend what they want to do. Do not send the "
                    + "last command again; the harness will refuse it. If you learn something about your "
                    + "own play rather than about the game, that belongs in a `meta_note`.";
                _log.Event("stuck", new { detail = system.Detail, answered = _stuckAnswered });

                if (_stuckAnswered)
                {
                    // Already given its turn to think. The player has been told; the decision is
                    // theirs now.
                    break;
                }

                _stuckAnswered = true;
                await RespondAsync(
                        $"{system.Detail}\n\nThe game is not moving and there is nothing on the "
                        + "screen that would explain it. Think about the *play* rather than the game: "
                        + "what has been assumed that might be wrong, what has never been tried, and "
                        + "whether this is a place worth being in at all. One attempt at something "
                        + "different, or a question to your friend — and say what you are doing, in "
                        + "your own words, because they are reading.",
                        "stuck",
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            // The world moved: the verdict is over and the companion can stop reasoning about itself
            // and get back to the game. Sent by the core when its own detector clears rather than
            // inferred here, so the two ends cannot disagree about whether it still applies.
            case "unstuck":
                _stuckNote = null;
                _stuckAnswered = false;
                _log.Event("unstuck", new { detail = system.Detail });
                _log.Info("the world is moving again");
                break;

            default:
                _log.Event("system", new { @event = system.Event, detail = system.Detail });
                break;
        }
    }

    private async Task HandleVocabularyAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var payload = envelope.PayloadAs<VocabularyPayload>();
        if (payload?.Words is null || payload.Words.Count == 0)
        {
            _log.Warn("the core sent a vocabulary with no words in it; keeping the one we have");
            return;
        }

        if (!string.IsNullOrEmpty(_notebook.GameStem)
            && !string.Equals(payload.GameStem, _notebook.GameStem, StringComparison.Ordinal))
        {
            // Belongs to a game we have already left; the next one will arrive shortly.
            _log.Warn("ignoring a vocabulary for another game", new { stem = payload.GameStem });
            return;
        }

        // The version travels with the words, and it is the fact that says whether this story's
        // interpreter keeps the status line at all (02_game_boundary.md §4.3 (`G23`)) — which is what lets the
        // `status` tool say why an absent number is absent instead of naming both reasons.
        _zVersion = payload.Version;

        _feedback.SetKnownVocabulary(payload.Words, payload.Resolution);

        // Then what this game's parser said back last time: the attempts themselves, so the model
        // reasons from what happened rather than from a verdict it cannot check (03_session.md §9–§10 (`S58`–`S76`)).
        // Measured live on 2026-09-16, `examine`, `buy` and `down` were each refused once and
        // forgotten at the end of the evening; measured on 2026-09-21, remembering them as verdicts
        // cost five good verbs for the rest of a session.
        var remembered = RememberedAttempts(payload.GameStem);
        var wordVerdicts = 0;
        foreach (var attempt in remembered)
        {
            _feedback.NoteAttempt(attempt);
            if (attempt.WordUnknown)
            {
                wordVerdicts++;
            }
        }

        if (remembered.Count > 0)
        {
            _log.Info(
                "remembered what this game's parser said last time",
                new
                {
                    stem = payload.GameStem,
                    attempts = remembered.Count,
                    words_unknown = wordVerdicts,
                });
        }

        var usable = Vocabulary.ActionWords.Count(_feedback.Knows);
        _log.Info(
            "learned the game's vocabulary from its story file",
            new
            {
                stem = payload.GameStem,
                version = payload.Version,
                resolution = payload.Resolution,
                words = _feedback.KnownWords,
                actions_usable = usable,
            });
    }

    // -- missions -----------------------------------------------------------

    /// <summary>
    /// The player has handed over an errand (§iteration 01 §3): "scout the open exits".
    /// A standing order with a budget, not a queue of permissions.
    /// <para>
    /// <paramref name="declaredOnPass"/> is the pass the errand was declared on, and only a
    /// declaration has one: a declaration made while answering the player is the job they just
    /// asked for, so it takes their bound (04_companion.md §6 (`C42.3`, `C42.6`)), and one made
    /// while they were not speaking is the companion's own account and is rationed.
    /// </para>
    /// </summary>
    private void StartMission(string goal, string source, string? declaredOnPass = null)
    {
        if (_mission is { Active: true })
        {
            // The goal belongs to whoever set it (04_companion.md §6 (`C42`–`C47`)). While the player's errand stands,
            // the companion's own declaration is a change of plan inside it, not a new job — the
            // measured failure was a delegated journey replaced by a nearer task five seconds in,
            // with no step taken, which is how a walk to the fountain became a trip for a blanket.
            if (_mission.FromThePlayer && source == "declared by the companion")
            {
                _log.Info(
                    "the companion revised its plan inside the player's errand",
                    new { goal = _mission.Goal, declared = goal });
                return;
            }

            _log.Info("a new errand replaces the one in hand", new { was = _mission.Goal, now = goal });
            EndMission("replaced by a new errand");
        }

        // An errand the player set is not rationed in steps (04_companion.md §6 (`C42`–`C47`)): what the budget is for
        // is the companion talking itself into a long walk, not the player asking for something and
        // then having to ask again every eight steps. **Whose errand it is is a question about whose
        // words set the goal, not about which route the errand arrived by** — a declaration made
        // while answering the player is the job they have just handed over, and the route is still
        // recorded in `source` for the log.
        var budget = Mission.ThePlayerSetTheGoal(source, declaredOnPass)
            ? _config.MissionBudgetDelegated
            : _config.MissionBudget;
        _mission = new Mission(goal, budget, source, declaredOnPass);
        _refusedActs = 0;
        _missedSteps = 0;
        _log.Event(
            "mission_start",
            new { goal = _mission.Goal, budget = _mission.Budget, source });
        _log.Info(
            "work in hand",
            new { goal = _mission.Goal, budget = _mission.Budget, source });
    }

    /// <summary>The errand is over. The report, if any, is the reply that ended it.</summary>
    private void EndMission(string reason)
    {
        if (_mission is null || !_mission.Active)
        {
            return;
        }

        _mission.Finish(reason);
        _log.Event(
            "mission_end",
            new { goal = _mission.Goal, steps = _mission.StepsTaken, budget = _mission.Budget, reason });
        _log.Info(
            "the errand is over",
            new { goal = _mission.Goal, steps = _mission.StepsTaken, reason });
        _refusedActs = 0;
    }

    /// <summary>
    /// Answer the replies held for a turn that is not coming.
    /// <para>
    /// The turn-landed path drains the same list and says so differently, because the fact is
    /// different: there, the result is on the screen and the reply is *here it is*. Here nothing
    /// arrived, and a companion that says nothing about that is a companion the player cannot tell
    /// from one that has hung.
    /// </para>
    /// </summary>
    /// <param name="why">
    /// What to tell the model about why there is no answer, in the sentence it reads beside the
    /// player's waiting words.
    /// </param>
    /// <remarks>
    /// Whether this reply may carry a move is deliberately **not** a parameter. It follows from the
    /// world, and `_state` is where the world is described: a game that has exited sets
    /// <see cref="ArbitrationState.TheWorldIsGone"/>, which is what turns every pass into talk. A
    /// caller that had to remember it would eventually forget — which is exactly how an exit came to
    /// be chased for a move it could not make (measured 2026-09-24).
    /// </remarks>
    private async Task AnswerTheHeldRepliesAsync(string why, CancellationToken cancellationToken)
    {
        if (_repliesWaitingForTheTurn.Drain() is not { } waiting)
        {
            return;
        }

        _log.Warn("answering replies held for a turn that never landed", new { purpose = waiting.Purpose });

        var answered = await RespondAsync(
                $"{waiting.LatestEvent}\n({why} Reply to the player now, and do not claim to be "
                + "still waiting for the game.)",
                waiting.Purpose,
                cancellationToken,
                wantMovements: null)
            .ConfigureAwait(false);

        if (answered)
        {
            TakeTheInstructionInHand(waiting.Purpose, waiting.Work);
        }
    }

    private async Task HandleTickAsync(CancellationToken cancellationToken)
    {
        // A turn that never lands must not hold a reply for ever. Measured 2026-09-24: a line the
        // core had refused left the loop waiting for a turn that could not arrive, and the session
        // was silent — no model call, no notice, and the player's lines held behind the wait. The
        // refusal is reported for what it is now (see `input_refused`), and this is the net under
        // every path that can drop a line, including the ones nobody has thought of yet.
        //
        // Keyed on `TurnInFlight` rather than on `_awaitingTurn`, because the two are not the same
        // state and the hold uses the wider one: a flight the *player* forced has no line of the
        // companion's to answer for, and a stale `PlayerDirectedFlight` — `game_exited` clears the
        // one flag and not the other, for instance — would otherwise hold every reply for ever with
        // the deadline never looking at it.
        //
        // Checked *before* the early return below, because a turn in flight is exactly the state
        // this is for.
        if (_config.TurnFlightSeconds > 0
            && TurnInFlight
            && _repliesWaitingForTheTurn.HeldFor(DateTime.UtcNow) is { } waited
            && waited > TimeSpan.FromSeconds(_config.TurnFlightSeconds))
        {
            _log.Warn(
                "no turn landed; the held replies go out",
                new { waited_seconds = (int)waited.TotalSeconds, line = _lineInFlight });

            _awaitingTurn = false;
            ForgetTheLineInFlight();
            _state.NoteTurnCompleted();
            await AnswerTheHeldRepliesAsync(
                    $"The command sent {(int)waited.TotalSeconds}s ago has produced no answer, and "
                    + "the game is not going to give one.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_awaitingTurn)
        {
            return;
        }

        // Nothing starts by itself while the story is going nowhere (02_game_boundary.md §4.3 (`G24`)). The treadmill
        // this prevents is the loud kind: an errand stepping command after command into a wall, each
        // one a fresh model call, none of them moving anything. The companion is asked once, when
        // the verdict arrives, to think about it from outside the game; after that the decision is
        // the player's, which is what *"stop feeding the story"* means in practice.
        if (_stuckNote is not null)
        {
            return;
        }

        // Everything below this line is the companion starting something by itself: the idle
        // nudge, the next step of an errand, taking stock. None of it is wanted with an empty room.
        if (!_playerPresent)
        {
            return;
        }

        // The player is holding the companion (03_session.md §6 (`S41`–`S43`)): the line in their box begins with a space.
        //
        // It locks the *acting*, not the talking (B1, decided 2026-09-21). It used to be silence
        // outright — no pass started at all while the space was there — which left a player unable to
        // tell *listening but not moving* from *stuck* or *gone*: measured over one evening, 37
        // holds, 36 releases, and a third of every errand killed by one. The errand still ends where
        // it stands (that is `HandlePlayerComposing`, and it is the player's substitute for a step
        // budget); what changes here is that a speaking trigger may still fire, with `allowAct`
        // false, so the companion can answer and cannot move.
        var held = _state.IsHeld;

        // The session's cost bound (04_companion.md §6 (`C42.7`)). Past it the companion parks *itself*: the game
        // keeps playing and the player keeps being answered, but nothing is started on its own.
        // Below the hold, because a player already holding the line does not need a second reason
        // for silence.
        if (await OverTheSessionCostBoundAsync().ConfigureAwait(false))
        {
            return;
        }

        // The model being unreachable, which is the one thing said while it is (04_companion.md §10 (`C56`–`C60`)).
        // Above the question gate on purpose: a companion that asked something and then lost the
        // model has gone quiet with a question outstanding, which is the case that most needs
        // saying out loud rather than the one that least does. Nothing here is a turn.
        if (await NoteTheAway().ConfigureAwait(false))
        {
            return;
        }

        // And the window it missed, once the model is back: one stock-taking pass, then the ordinary
        // ticks carry on. Checked before the triggers because the answer to "what happened while I
        // was gone" is owed before anything the companion wants to say next.
        //
        // A pass that failed is *not* forgotten here. The one failure this can be is the model going
        // away again in the middle of the pass, and clearing the flag on that would lose an evening:
        // the gate above already stops the retry while the model is away, and the pass completes on
        // the recovery after it.
        if (_catchupDue && !TheModelIsAway)
        {
            if (await CatchUpAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        // A question is outstanding: the companion asked the player something and is waiting
        // for the answer (issue #23). Nothing self-initiated happens until it arrives — no
        // nudge, no errand step, no act. It asked; it waits. This is the whole fix: the idle
        // clock ran from the *player's* last line, so fifty seconds after asking its own
        // question the companion spoke over it. Twice in the session that produced this.
        //
        // The wait is bounded, because "it asked, so it waits" became "it waits for ever the
        // first time nobody was there". Measured on a clean container: it asked at turn 6 and
        // the game never advanced again, because there was no player to answer. An unattended
        // companion is the normal case, so once the patience runs out the companion settles its
        // own question and carries on with its plan.
        if (_state.IsAwaitingAnswer)
        {
            var question = _state.AwaitingAnswer!;
            if (StillWaitingForThePlayer("question", question, _state.AwaitingAnswerSince))
            {
                return;
            }

            // The patience is spent, so the question is settled — and the gate that stops the
            // companion answering itself has to go with it. Measured: it did not, and the
            // timeout was purely cosmetic. The state still said "waiting for the player", so
            // `ActPolicy` refused every act ("waiting for the player's answer to the companion's
            // own question"), nothing was ever sent to the game, and the turn count froze while
            // the log reported the give-up every five seconds, for ever. Announcing a decision
            // is not making it.
            _state.ClearAnswer();
            _log.Info("nobody answered the question, so the companion settles it itself", new { question });
        }

        // A risky action is sitting with the player: it was announced, and it needs a yes or a
        // no before it goes. The grant is a real gate — that is the point of it — but an
        // unanswerable gate is not a gate, it is a wall. Measured: the companion proposed
        // `light lamp` at turn 18 and the run stopped there for ever, because nobody was
        // connected to say yes.
        if (_state.IsAwaitingGrant)
        {
            if (held)
            {
                // Going ahead on an unanswered proposal is a *move*, and the player has asked for
                // the line to be held. The gate stays where it is until they let go of it.
                return;
            }

            if (StillWaitingForThePlayer("proposal", _state.Pending?.Action ?? "the proposed action", _state.ProposalSince))
            {
                return;
            }

            // It goes ahead. This is not a new decision and not the companion overriding the
            // player: announce-then-act was already satisfied, because the proposal *was* the
            // announcement, and the player had the whole patience window to say no. Clearing the
            // gate and letting the model decide again would only produce the same proposal.
            var action = _state.Approve();
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            _log.Info("nobody answered the proposal, so the companion goes ahead with it", new { action });
            _log.Event("act", new { line = action, channel = "unanswered-proposal" });
            await SendGameInputAsync(action, byAgent: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        _loggedWaitFor = null;

        // An errand in hand outranks every speaking trigger: the companion is working,
        // not waiting to be entertained (§iteration 01 §3).
        //
        // Unless the player has their hand out. A hold pauses the work where it stands rather than
        // ending it (04_companion.md §6 (`C42`–`C47`)), and a paused errand takes no step: the speaking triggers below
        // still get their turn, and the tick after the space comes off the front of the line picks
        // the errand up on the step it was on. This is the one place `held` is read for the work —
        // and it has to be read here rather than where the errand ends, because a step taken while
        // the player is mid-sentence is the move the space is asking us not to make.
        if (_mission is { Active: true } && !held)
        {
            await RespondAsync(_mission.Brief(), "mission-step", cancellationToken, thisIsWork: true)
                .ConfigureAwait(false);
            return;
        }

        // Time to take stock (issues #22 and #27): the pass that lets the companion check its
        // notes against what has actually happened, and write down what it wants to keep.
        //
        // Not the moment the player has just typed, though: taking stock runs 25 seconds by
        // measurement (2026-09-16), and starting one under someone's fingers is correct work in
        // the wrong moment — the same complaint as a long silence. The cadence keeps counting, so
        // it fires at the first quiet tick instead. The idle nudge below is unaffected.
        var quietFor = QuietFor;
        if (_config.ReconcileEveryTurns > 0
            && _turnsSinceReconcile >= _config.ReconcileEveryTurns
            && quietFor >= TimeSpan.FromSeconds(_config.ReconcileMinQuietSeconds)
            // Housekeeping waits for the hold (B1). The hold locks acting, not talking — the idle
            // nudge below still gets its say — but taking stock is neither: it is a long model call
            // that writes notes for the companion's own benefit, started under the fingers of
            // somebody who has just asked for a moment. Nothing is lost by waiting; the cadence
            // keeps counting and it fires at the first quiet tick.
            && !held)
        {
            await ReconcileAsync("it has been a while since you last took stock", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Nobody is nudged mid-sentence (issue #42). The idle clock is about "my friend has gone
        // quiet", and somebody who is typing has not gone anywhere: a nudge now lands in the middle
        // of the sentence they are writing, which is the same failure as giving up on the answer,
        // only ruder.
        if (_state.PlayerIsTyping(TimeSpan.FromSeconds(_config.PlayerTypingWindowSeconds)))
        {
            return;
        }

        var idle = QuietFor;

        // The room's own line, before the nudge (`C71`). At this distance from the last event a nudge
        // is one more offer to get on with it, and what the evening is short of is company rather than
        // another offer — so when the quiet has gone on long enough, the pass that speaks for nobody
        // and moves nothing gets its turn first. It is the only reason to speak in this class that the
        // game did not cause.
        var room = _triggers.OnQuietForItsOwnSake(idle);
        if (room.Speak)
        {
            _log.Event(
                "trigger",
                new
                {
                    reason = "outloud",
                    detail = room.Detail,
                    quiet_seconds = Math.Round(idle.TotalSeconds),
                    last_event = _state.LastEventWas,
                });
            await RespondAsync(
                    ALineFromTheRoomAsk(idle.TotalSeconds),
                    OutLoudPurpose,
                    cancellationToken,
                    wantMovements: null,
                    allowAct: false)
                .ConfigureAwait(false);
            return;
        }

        var decision = _triggers.OnIdle(idle, NotebookHasPlan());
        if (!decision.Speak)
        {
            return;
        }

        _log.Event(
            "trigger",
            new
            {
                reason = decision.Reason.ToString().ToLowerInvariant(),
                detail = decision.Detail,
                quiet_seconds = Math.Round(idle.TotalSeconds),
                last_event = _state.LastEventWas,
            });
        await RespondAsync(
            held
                ? $"it has been quiet for {idle.TotalSeconds:F0} seconds, and your friend's line has "
                  + "a space at the start of it — they are writing something. You may say a line, and "
                  + "you may not move: nothing is waiting on a move from you while that space is "
                  + "there. If you have nothing worth saying, say nothing."
                : $"it has been quiet for {idle.TotalSeconds:F0} seconds — the last thing that moved the "
                  + $"session was {_state.LastEventWas}. Your friend may have stepped "
                  + "away from the keyboard. You may take one more go — at most two — and then stop and "
                  + "hand it back to them, in a line that says so. Do not ask them to choose between "
                  + "options while they are not there to answer, and do not carry on past that: playing "
                  + "on alone stops being company. If there is genuinely nothing worth doing, say nothing.",
            "idle-nudge",
            cancellationToken,
            wantMovements: null,
            allowAct: !held).ConfigureAwait(false);
    }

    /// <summary>
    /// Is the companion still within its patience for an answer from the player?
    /// <para>
    /// The one place a wait is measured, so the question gate and the proposal gate cannot
    /// drift apart — they are the same patience for the same person, and only the log line
    /// differs. Returns true to hold. Logs the hold once per waiting thing rather than once per
    /// tick (this used to write a line every five seconds for the whole life of a wait), and
    /// logs nothing at all once the patience is spent — the caller decides what to do then.
    /// </para>
    /// </summary>
    /// <param name="since">
    /// The start of *this* wait, passed in rather than looked up: the two gates have their own
    /// clocks on purpose (see <see cref="ArbitrationState.ProposalSince"/> for what sharing one
    /// cost). A null start means "no clock", which is treated as still waiting rather than as
    /// instantly expired — an unknown start must never make the companion give up in the same
    /// tick the question went out.
    /// </param>
    private bool StillWaitingForThePlayer(string what, string subject, DateTimeOffset? since)
    {
        var patience = TimeSpan.FromSeconds(_config.PlayerPatienceSeconds);
        if (patience <= TimeSpan.Zero || since is null)
        {
            // Zero means wait for ever, which is only right if someone is always watching.
            return true;
        }

        // Mid-sentence is not an answer, but it is the closest thing there is to one — and the
        // keystroke has already restarted the clock this wait is measured from
        // (ArbitrationState.NotePlayerTyping). Nothing is logged here: this is checked every tick,
        // and the burst itself is the event (issue #42).
        if (_state.PlayerIsTyping(TimeSpan.FromSeconds(_config.PlayerTypingWindowSeconds)))
        {
            return true;
        }

        var waited = DateTimeOffset.UtcNow - since.Value;
        if (waited >= patience)
        {
            _loggedWaitFor = null;
            _log.Info(
                "the player has not answered in time, so the companion carries on",
                new { waiting_for = what, subject, waited_seconds = Math.Round(waited.TotalSeconds) });
            return false;
        }

        var key = what + ":" + subject;
        if (!string.Equals(_loggedWaitFor, key, StringComparison.Ordinal))
        {
            _loggedWaitFor = key;
            _log.Info(
                "holding everything while the player answers",
                new { waiting_for = what, subject, give_up_in_seconds = Math.Round((patience - waited).TotalSeconds) });
        }

        return true;
    }

    // -- the model call -----------------------------------------------------

    private async Task<bool> RespondAsync(
        string latestEvent,
        string purpose,
        CancellationToken cancellationToken,
        bool thisIsWork = false,
        bool escalate = false)
    {
        return await RespondAsync(
                latestEvent,
                purpose,
                cancellationToken,
                wantMovements: null,
                thisIsWork: thisIsWork,
                escalate: escalate)
            .ConfigureAwait(false);
    }

    /// <param name="wantMovements">
    /// What the core watched the player move between, when this reply is one where knowing
    /// where you are matters (issue #22). Null for everything else: it is bulky.
    /// </param>
    /// <param name="allowAct">
    /// Is this reply the companion's to move in?
    /// <para>
    /// True when it was asked something, when it is working an errand, when it is answering a
    /// prompt only it can read, and for the single idle allowance. False for everything it
    /// started by itself — a report, a remark, a consult, a take-stock pass (04_companion.md §5 (`C29`–`C41`)).
    /// </para>
    /// <para>
    /// Null for the callers that do not say, and then the hold decides: no act while the player
    /// is holding the line, whatever the pass is for (04_companion.md §6 (`C42`–`C47`)). A held pass still runs
    /// — it can talk, steer, reason and take notes — it just cannot type at the game.
    /// </para>
    /// </param>
    /// <returns>
    /// Whether a move reached the game by the end of this pass — including one the repair for a
    /// dropped call supplied (<see cref="NoteAMissedMoveAsync"/>). The errand logic asks,
    /// because a step of an errand that moves nothing is read as the report that ends it, and a
    /// dropped call is not a report.
    /// </returns>
    /// <param name="thisIsWork">
    /// Is this pass a step of something the player asked for, rather than the companion's own
    /// idea? The allowance bounds what it spends on itself at an empty desk; work is bounded by
    /// its own budget instead — the errand's — and the allowance has no business strangling it
    /// half way through. Measured on 2026-09-14: the fifth call of a journey the player had
    /// asked for was refused with "the companion has spent its allowance for this silence",
    /// with the walk half done.
    /// </param>
    /// <param name="escalate">
    /// Is this pass the *retry* of one the length limit cut off? Then it is asked for with twice the
    /// room, clamped to what the prompt leaves in the window (04_companion.md §3.2 (`C23`)). The
    /// ceiling that cut the first attempt is evidence rather than a guess — it is the one fact about
    /// the reply the harness holds and the model cannot see — so the retry is the one pass that may be
    /// given more. It is also the cheap half of the trade: the prompt goes again either way.
    /// </param>
    private async Task<bool> RespondAsync(
        string latestEvent,
        string purpose,
        CancellationToken cancellationToken,
        string? wantMovements,
        bool? allowAct = null,
        bool thisIsWork = false,
        bool escalate = false)
    {
        // Null means "the caller did not say", and then the state answers: a pass may move the
        // game unless the player is holding the line (04_companion.md §6 (`C42`–`C47`)). Every pass that comes in
        // through the short overload above lands here, and before this they all defaulted to
        // yes — which is how the repair for a dropped call came to type `north` a second after
        // the player had started writing (measured 2026-09-22).
        // Whether a pass may act is the hold's business and the world's: a line held by the player's
        // own space cannot be moved in (B1), and a game that has stopped cannot be moved in at all —
        // there is nothing left to type into it (A9).
        //
        // The world's half is **intersected** rather than defaulted, and that is the whole point:
        // five passes name their own permission (a game-prompt answer, the turn-landed drain, a
        // circles consult, an idle nudge, a held line's own pass), and a caller that names one is
        // naming a *narrower* rule, never a wider one. Nothing acts in a game that is gone, however
        // it asked. Measured 2026-09-24: an ordinary line after an exit was chased with *"make the
        // move now"* because its own pass set the permission itself and the exit's fact reached only
        // the default.
        _allowAct = (allowAct ?? !_state.IsHeld) && !_state.TheWorldIsGone;
        if (!IsModelAvailable(purpose))
        {
            return false;
        }

        // The allowance for talking to itself. Everything else in this method is about making a
        // call worth making; this is the one that says "not any more until they come back".
        //
        // Two counters, because there are two kinds of call and they were being rationed by one
        // number: the social allowance bounds what the companion spends on *speaking* into a
        // silence, and the maintenance allowance bounds taking stock — a pass that only exists to
        // run when nobody is typing (measured 2026-09-16: 10 of 20 throttles were reconcile).
        if (!thisIsWork)
        {
            var maintenance = IsMaintenance(purpose);
            var spent = maintenance ? _maintenanceCallsSinceThePlayer : _callsSinceThePlayer;
            var limit = maintenance ? _config.MaintenanceCallLimit : _config.UnattendedCallLimit;
            if (spent >= limit)
            {
                if (!_reportedWaiting)
                {
                    _reportedWaiting = true;
                    _log.Info(
                        "the companion has spent its allowance for this silence; it waits for the player",
                        new { calls = spent, limit, purpose });
                    _log.Event("unattended_limit", new { calls = spent, limit, purpose });
                }

                return false;
            }
        }

        var notebook = _notebook.Read();
        if (_notebook.OverBudget(notebook) && !_warnedNotebookOverBudget)
        {
            // Say it once, and say what it means: the notes are competing with the transcript
            // for the model's attention. Never truncate silently — a memory that quietly
            // disappears is how a companion starts lying (§2.8).
            _warnedNotebookOverBudget = true;
            _log.Warn(
                "the notebook is over its attention budget; it may crowd out the transcript",
                new { tokens = NotebookStore.EstimateTokens(notebook), budget = _config.NotebookTokenBudget });
        }

        // The memories go in on every call, labelled as recollections rather than facts.
        // A friend who only remembers you at the door is not one, and the thing they are for
        // — "remind me of that time in…" — comes up in the middle of a game, not at the start
        // of one (issue #27).
        //
        // The danger level rides here too (§4.7): the gate the code enforces and the sentence
        // the model reads have to say the same thing, or the companion's judgement and its
        // instructions disagree.
        var constraint = _feedback.ContextNote() + RiskNote();
        if (_bootNotePending)
        {
            // Once, at the first reply after the handshake. The screen is older than this process
            // and the notes may be older than the screen (05_memory.md §5 (`M23`–`M26`)).
            _bootNotePending = false;
            constraint += $"\n\nYou have just come back to a position saved earlier ({_resumedFrom}"
                + (_run > 0 ? $", attempt {_run}" : string.Empty)
                + (_resumedAtTurn is > 0 ? $", at turn {_resumedAtTurn}" : string.Empty)
                + "). Your notes were written around it and may be older than it, so look at the "
                + "screen before saying where you are or what you are carrying.";
        }
        if (_forkNote is { } fork)
        {
            // Once, at the first reply after the fork, for the same reason the boot note is said
            // once: a standing instruction to check where it is reads as boilerplate by the third
            // call, and the call it stops being read on is the one that mattered.
            _forkNote = null;
            constraint += "\n\n" + fork;
        }
        if (_stuckNote is { } stuck)
        {
            // *Not* once-only, unlike the two above: this is true of every call until the world
            // moves again, and the one thing that must not happen is a companion that has forgotten
            // it and gone back to repeating itself.
            constraint += "\n\n" + stuck;
        }
        var standingTurn = _status?.Turn ?? _lastTurnSeen;
        if (Stagnation.Fact(
                TheWorld(),
                standingTurn,
                TurnsPlayedInTheWindow(standingTurn)) is { } goingNowhere)
        {
            // The macro version of the same fact, and for the same reason: `C39` counts one action
            // repeating, and this counts a *stretch* in which nothing was new — the twenty-five-turn
            // sweep of one room on 2026-09-29, which ended in the player's "sheesh".
            constraint += "\n\n" + goingNowhere;
        }
        else
        {
            // And here is where the *silence* is explained (`C39.9`) — at the place the fact would
            // have gone, which is the one line every pass in the loop goes through. It was first put
            // at the two consults, and the first sitting after that proved the mistake: a stillness
            // sweep whose passes were `player-line` and `game-prompt` never reached either call site,
            // so the row built to answer "why is the stretch quiet" could not appear in the situation
            // it was written for.
            ExplainTheQuietStretch(standingTurn);
        }
        if (IsAGreeting(purpose))
        {
            // Never `NoActNote` on a greeting: it says the *next* move is the friend's, which is a
            // true thing about a turn and the wrong thing to say about arriving. The greeting's own
            // sentence says the same about this pass and nothing about the play (`C16.2`).
            constraint += GreetingNote;
        }
        else if (!_allowAct)
        {
            // Which of the three reasons it is, because they are different situations for the model:
            // a turn that is the player's, a turn the player has *held* while they write, and a pass
            // about the harness rather than the game. Read off the resolved field rather than the
            // argument, so that the sentence the model reads says the same thing as the gate the
            // policy enforces.
            constraint += _state.IsHeld
                ? HeldNote()
                : IsIntrospection(purpose) ? IntrospectNote : NoActNote;
        }
        var assembled = _context.Assemble(
            notebook,
            _transcript.Items,
            latestEvent,
            constraint,
            situation: WhereThisReplyStands(),
            memories: _memory.ReadForPrompt(),
            engineNotes: _engine.Read(),
            movements: wantMovements,
            gameBrief: _gameBrief,
            gameBriefIsStanding: _gameBriefIsStanding,
            // A plan that names a room this world does not have cannot be run (05_memory.md §1 (`M2.3`)),
            // and the moment to say so is every call, before the errand is built on it.
            unresolvedPlaces: PlanPlaces.Report(TheWorld(), notebook),
            beliefs: Annotations.ReadForPrompt(_notebook.StateDirectory, _notebook.GameStem, TheWorld()));
        _log.Event(
            "context",
            new
            {
                purpose,
                messages = assembled.Messages.Count,
                estimated_tokens = assembled.EstimatedTokens,
                fixed_tokens = assembled.FixedTokens,
                items_included = assembled.ItemsIncluded,
                items_dropped = assembled.ItemsDropped,
                over_budget = assembled.OverBudget,
            });

        if (assembled.OverBudget)
        {
            _log.Warn(
                "context assembly exceeded the transcript budget; the oldest non-floor turns were dropped",
                new { estimated_tokens = assembled.EstimatedTokens, budget = _config.TranscriptTokenBudget });
        }

        if (assembled.OverFixedBudget)
        {
            _log.Warn(
                "the fixed prompt is over its attention budget; the notebook may crowd out the play",
                new { fixed_tokens = assembled.FixedTokens, budget = _config.FixedTokenBudget });
        }

        ChatResult result;

        // The ceiling that actually goes out, kept beside the reply rather than inside the try: the warn
        // below is what a reader meets first, and it used to report the number the pass *wanted* — which
        // is neither what was sent nor what a retry was given (`§25` item 3).
        var ceilingSent = 0;
        try
        {
            // The tools go to the passes that act in front of the player, and the passes that are
            // not a turn get only the ones that record: taking stock is not a turn, and it has no
            // business moving anything — but it is where the notebook is normally written, so it
            // does get those (10_delivery.md §8.3 (`D21`) and 05_memory.md §2 (`M11`)). Introspection is the same bargain from the other
            // side: it is given the recording surface *because* it is not offered anything that
            // moves, which is how "a note and never a lever" is enforced rather than asked for.
            var maintenance = IsMaintenance(purpose);
            ceilingSent = _config.CeilingThatFits(
                _config.MaxTokensFor(purpose, escalate),
                assembled.EstimatedTokens);
            // What this pass may have, before the notebook rule below: the four surfaces are the
            // pass's job, and the rule is about one kind of tool inside them.
            IReadOnlyList<ToolDefinition> tools = IsTheWriteUp(purpose)
                ? _forcingTheReport
                    ? CompanionTools.ForTheLastWriteUp
                    : CompanionTools.ForTheWriteUp
                : WritesRatherThanMoves(purpose)
                    ? CompanionTools.Recording
                    : IsAGreeting(purpose)
                        ? CompanionTools.ForAGreeting(_mission is { Active: true })
                        : IsALineFromTheRoom(purpose)
                            ? CompanionTools.ForALineFromTheRoom
                            : CompanionTools.InFrontOf(_mission is { Active: true });

            // A pass that owes somebody a line MUST NOT be offered a whole-file write (04_companion.md
            // `C70`). The one exception is the one reason a rewrite is compulsory rather than
            // convenient: a notebook over its budget is bloat already being paid for on every call.
            if (!MayRewriteTheNotebook(purpose))
            {
                tools = CompanionTools.WithoutTheWholeFileWrites(tools);
            }

            var options = new CallOptions
            {
                Thinking = ThinkingFor(purpose, maintenance),
                Tools = tools,
                // A step of an errand has no prose-only outcome, so the provider is asked for a call
                // rather than left to send prose (`C23.8`) — but **only where thinking is explicitly
                // off**, because this backend refuses the combination outright. Measured 2026-10-01,
                // live: `HTTP 400: Thinking mode does not support this tool_choice` with
                // `fatal: true`, so the pass did not fall back to prose — it failed, the agent paused
                // model calls for two minutes, and the companion went quiet mid-evening. The first
                // probe of this feature passed because it made a plain call with no thinking; the
                // agent's own passes have thinking on, which is the difference that mattered.
                RequireATool = AStepMustCallSomething(purpose) && ThinkingFor(purpose, maintenance) == false,
                MaxTokens = ceilingSent,
            };
            result = await CompleteWithRetrievalAsync(
                    assembled.Messages, options, purpose, thisIsWork, escalate, cancellationToken)
                .ConfigureAwait(false);
            _consecutiveLlmFailures = 0;

            // The model answered, so whatever was wrong with it is over — and if it was away long
            // enough for the game to move on without it, that window is owed a catch-up
            // (04_companion.md §10 (`C56`–`C60`)). Set here rather than acted on here: this is a call in the middle of answering
            // somebody, and a stock-taking pass is its own piece of work.
            //
            // Dated once per outage rather than once per call: the first successful call ends it,
            // and every call until the catch-up runs would otherwise write the same event again —
            // which makes a report that counts recoveries read three of them where there was one.
            if (_awaySince != DateTimeOffset.MinValue && !_modelBackLogged)
            {
                if (_lastTurnSeen > _awayFromTurn || _awayPlayerLines.Count > 0)
                {
                    _catchupDue = true;
                }

                _modelBackLogged = true;
                _log.Event(
                    "model_back",
                    new
                    {
                        away_seconds = Math.Round((DateTimeOffset.UtcNow - _awaySince).TotalSeconds),
                        turns_missed = Math.Max(0, _lastTurnSeen - _awayFromTurn),
                        player_lines = _awayPlayerLines.Count,
                    });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlmException ex)
        {
            await NoteModelFailureAsync(ex, purpose).ConfigureAwait(false);
            return false;
        }

        // Into the history as plain speech, which is now all there is: the reply carries no
        // markup to strip. The tool calls are deliberately *not* replayed — the game's own output
        // is the record of what was done, and a transcript of calls the harness already executed
        // would be a second, staler copy of it.
        var spoken = result.Content.Trim();
        if (spoken.Length > 0 && !NothingCameOfTheRepair(purpose, result))
        {
            _transcript.AddCompanion(spoken);
        }

        if (result.WasTruncated)
        {
            _log.Warn(
                "the model's reply hit the ceiling and was cut off",
                new { purpose, max_tokens = ceilingSent, escalated = escalate });
        }

        var outcome = await ApplyReplyAsync(result, purpose, cancellationToken).ConfigureAwait(false);

        // And then, if anything dropped on the harness's side of that pass, it is said out loud
        // rather than only written to a log (05_memory.md §7 (`M37`–`M39`)). Here rather than inside
        // `ApplyReplyAsync` because every path through it — and there are early returns in there —
        // ends up back here, and a drop that is noted on a path that returns early is exactly the
        // one nobody would ever read.
        //
        // Not from inside a repair, though: `act-missed`, `act-refused` and `cut-off` are calls this
        // loop makes *because* something dropped, and reading the report at the end of one of those
        // would be reading it in the middle of the recovery rather than after it. The pass that was
        // trying to do something reads it, once it has finished trying.
        if (!IsARepair(purpose))
        {
            await LookAtWhatDroppedAsync(purpose, cancellationToken).ConfigureAwait(false);
        }

        // And if this pass said nothing at all and moved nothing, it says one plain line rather than
        // leaving the room quiet (04_companion.md §3 (`C16.1`)). Not gated on `IsARepair` like the
        // block above, deliberately: a repair that fires and says nothing is exactly the pass this is
        // for, and the words it produced went nowhere because the repair made no call at all.
        //
        // The line is the harness's, not the companion's, and it does not go through the model: the
        // model is the thing that just answered with nothing, so it cannot be the messenger. It is
        // also not an apology — it is a fact, and the player can act on a fact.
        //
        // "Nothing" means nothing the player was owed, and there are two ways a pass owes them
        // nothing. One is delivery somewhere other than the conversation: the write-up writes the page
        // into `catchup/` and says nothing in the chat, so the page *is* the answer, in the pane they
        // asked for it in. The other is a pass that is the harness talking to itself — the maintenance
        // pair and the introspection pass answer nobody, so the plain line there is the machine
        // announcing its own housekeeping.
        //
        // Measured live on 2026-09-30, both of them: the line arrived directly under a log row saying
        // a page had just been written, and then again from the *introspection* pass that the drop for
        // a wordless report had earned — a pass that answered with a `meta_note` and no prose, and so
        // told the player nothing had come of it seconds after the companion had given them the report
        // in full.
        //
        // And a third way, measured 2026-10-02: a question is delivery. An `ask` puts the question to
        // the player and the harness waits for the answer, so telling them nothing came of that pass is
        // false while they are reading it — and **three of that sitting's five plain lines arrived under
        // a live question**. `NothingCameOfTheRepair` had counted `ask` as a call of consequence since it
        // was written, and the guard checked a different question about the same reply.
        if (spoken.Length == 0
            && !outcome
            && !AnsweredOutsideTheChat(result)
            && !AskedThePlayer(result)
            && !IsTheHarnessTalkingToItself(purpose))
        {
            _log.Warn("a pass produced no speech and no act; saying so", new { purpose });
            await NotifyPlayerAsync("Nothing came of that, and nothing moved.").ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// One chunk of the companion's reasoning, on its way to the player as it is written.
    /// <para>
    /// The point is the wait. Measured on the evening of 2026-09-14: 163 of 248 calls left the
    /// screen silent for two seconds or more, and the longest were twenty-two — a companion
    /// thinking hard looked exactly like a hung terminal, and the play-by-play it was thinking
    /// was invisible for the whole of it. The finished thought still goes out afterwards; the
    /// pane uses that to close the paragraph rather than to print it a second time.
    /// </para>
    /// </summary>
    private async Task StreamThoughtAsync(string chunk)
    {
        try
        {
            await _seat.SendAsync(MessageTypes.ThoughtDelta, new { text = chunk }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A lost chunk is a stutter in one pane, not a failed turn: the finished thought
            // still goes out with everything else, and the call it belongs to carries on.
            _log.Warn("a thought chunk could not be sent", new { error = ex.Message });
        }
    }

    /// <summary>
    /// Apply the model's reply: its speech, and the tools it called (04_companion.md §2 (`C6`–`C13`)).
    /// <para>
    /// Speech and action used to arrive in one tagged blob and be pulled apart by a parser. They
    /// now arrive on separate channels, so this reads as a list of things that happened rather
    /// than as a parse — but the arbitration rules it applies are unchanged: a move still needs
    /// something said, a veto still drops a plan, a question still stops the clock.
    /// </para>
    /// </summary>
    /// <summary>
    /// One pass's model calls, extended with local retrieval rounds (04_companion.md §3 (`C14`–`C25`)).
    /// <para>
    /// When the model asks to search the record (<c>recall</c>) or read its notes
    /// (<c>read_notes</c>), the harness answers from the files, hands the results back as
    /// tool results, and calls again — up to a cap. An action aimed at the game ends the
    /// rounds, because the game answers asynchronously and a second round would be prose
    /// written blind; everything here stays local and synchronous.
    /// </para>
    /// <para>
    /// Each call is timed and priced on its own, and each counts against the allowance,
    /// because each one is a real call to the model — except when the pass is work the player
    /// asked for, which is bounded by the errand's budget instead (04_companion.md §5 (`C29`–`C41`)).
    /// </para>
    /// </summary>
    private async Task<ChatResult> CompleteWithRetrievalAsync(
        IReadOnlyList<ChatMessage> baseMessages,
        CallOptions options,
        string purpose,
        bool thisIsWork,
        bool escalated,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>(baseMessages);

        for (var round = 0; ; round++)
        {
            await LightTheLampAsync().ConfigureAwait(false);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var working = NoteIfTheCallIsSlow(purpose);
            var reply = await _llm!
                .CompleteAsync(messages, cancellationToken, options, StreamThoughtAsync)
                .ConfigureAwait(false);
            clock.Stop();

            // Counted once the call has actually happened: a failed call costs nothing, and
            // counting an attempt would let a broken endpoint spend the allowance the player
            // paid for. Maintenance passes count against their own allowance — see RespondAsync.
            // Work does not count at all: see `_callsSinceThePlayer`.
            if (!thisIsWork)
            {
                _callsSinceThePlayer++;
            }

            if (IsMaintenance(purpose))
            {
                _maintenanceCallsSinceThePlayer++;
            }

            // Priced at the moment of the call, keyed by the model the endpoint reported. The ceiling
            // goes with it: a row that says a reply was cut and not what it was allowed is a row that
            // cannot be read (`§25` item 3).
            _log.RecordLlmCall(
                purpose,
                reply,
                _pricing.CostOfAt(reply.Usage, reply.Model, DateTimeOffset.UtcNow),
                clock.ElapsedMilliseconds,
                // `CallOptions.MaxTokens` is nullable because a call outside a pass need not name one,
                // and zero is the log's own way of saying that.
                maxTokens: options.MaxTokens ?? 0,
                escalated: escalated);

            var retrievals = reply.ToolCalls
                .Where(call => CompanionTools.IsReadOnly(call.Name))
                .ToList();

            // Anything that is not a pure read ends the round, and the reply is applied as it stands.
            //
            // This used to be `call.Name is not (Recall or ReadNotes)` — a list of two names, written
            // three frames away from the tool definitions it was talking about. The day one of them
            // disagreed with what the tool actually does, the failure was silent: `saves` is a read,
            // and it was still fatal to a write-up pass, which *is* the 2026-09-20 report bug. The
            // question is now asked of the tool, which declares whether it only reads, and the test
            // beside the definitions holds every one of them to it.
            var actionable = reply.ToolCalls.Any(
                call => !CompanionTools.IsReadOnly(call.Name));

            // What it reached for, on the panel, while the trace is on (03_session.md §4.3 (`S32`–`S34`)). Here rather
            // than at the reply, because this is the one place every call passes through — the
            // retrieval rounds below never reach `ApplyReplyAsync` at all.
            foreach (var call in reply.ToolCalls)
            {
                await NoteToolCallAsync(call).ConfigureAwait(false);
            }

            if (retrievals.Count == 0 || actionable || reply.WasTruncated || round >= _config.LocalToolRounds)
            {
                return reply;
            }

            _log.Event(
                "retrieval",
                new { purpose, round = round + 1, tools = retrievals.Select(call => call.Name).ToArray() });

            messages.Add(new ChatMessage("assistant", reply.Content)
            {
                ToolCalls = reply.ToolCalls,
                Reasoning = reply.Reasoning,
            });
            foreach (var call in retrievals)
            {
                messages.Add(ChatMessage.ToolResult(call.Id, RunRetrieval(call)));
            }
        }
    }

    /// <summary>
    /// Tell the panel what the companion reached for, while the tool trace is on (03_session.md §4.3 (`S32`–`S34`)).
    /// <para>
    /// One line per call, rendered as <c>name(argument)</c>: the name is the tool's own and the
    /// argument is the one that says what it was aimed at. The core is what turns it into a notice,
    /// because the words are the harness's — the same reasoning as a proposal. Best-effort: a lost
    /// line is a lost line, never a failed turn.
    /// </para>
    /// </summary>
    private async Task NoteToolCallAsync(ToolCall call)
    {
        if (!_debug)
        {
            return;
        }

        try
        {
            await _seat
                .SendAsync(MessageTypes.Tool, new { name = call.Name, detail = DetailOf(call) })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the instrument must never be able to break the turn.
        }
    }

    /// <summary>
    /// The argument that says what a call was aimed at, for the trace. Null when the call carries
    /// nothing worth showing — `press_key` with no argument is a bare Return.
    /// </summary>
    private static string? DetailOf(ToolCall call) => call.Name switch
    {
        CompanionTools.Type => call.StringArgument("line"),
        CompanionTools.Errand => call.StringArgument("goal"),
        CompanionTools.Ask => call.StringArgument("text"),
        CompanionTools.Recall => call.StringArgument("query"),
        CompanionTools.Map => call.StringArgument("room"),
        CompanionTools.State => call.StringArgument("thing") ?? call.StringArgument("word") ?? call.StringArgument("room"),
        CompanionTools.ReadNotes or CompanionTools.UpdateNotes => call.StringArgument("section"),
        _ => null,
    };

    /// <summary>
    /// Say one quiet word if a model call outlasts the working threshold (04_companion.md §5 (`C29`–`C41`)).
    /// <para>
    /// Measured 2026-09-16: p95 was 15.9s, the longest call 45.7s, and once the player asked
    /// "james...you there?" mid-delivery. The thought deltas do not start until the model does,
    /// so up to that point a companion thinking hard and a hung terminal are the same screen.
    /// The notice carries no words of its own — the core renders it, exactly as it renders a
    /// proposal — and it is best-effort: a seat that cannot be reached loses a line, not a turn.
    /// The keyboard-seat condition is the core's to make, because only the core knows who is
    /// connected.
    /// </para>
    /// </summary>
    private IDisposable NoteIfTheCallIsSlow(string purpose)
    {
        var seconds = _config.WorkingIndicatorSeconds;
        return seconds <= 0
            ? NoNotice.Instance
            : new SlowCallNotice(this, TimeSpan.FromSeconds(seconds), purpose);
    }

    /// <summary>One delayed line per call, cancelled the moment the call returns.</summary>
    private sealed class SlowCallNotice : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public SlowCallNotice(AgentLoop loop, TimeSpan after, string purpose)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(after, _stop.Token).ConfigureAwait(false);
                    await loop._seat.SendAsync(MessageTypes.Working, new { }).ConfigureAwait(false);
                    loop._log.Info("the call is slow; the panel has been told", new { purpose, after_seconds = after.TotalSeconds });
                }
                catch (Exception)
                {
                    // A lost notice is a lost line, never a failed turn.
                }
            });
        }

        public void Dispose() => _stop.Cancel();
    }

    private sealed class NoNotice : IDisposable
    {
        public static readonly NoNotice Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A tool result: what a person would read, and then the same facts as JSON (04_companion.md §2 (`C6`–`C13`)).
    /// <para>
    /// The prose is for the model to read; the block is for it to be exact from. Both tools that use
    /// this hand back things the companion quotes — a save's turn and fork, the turn a phrase came
    /// from — and the failure it removes is the quiet one: a model reading a sentence back and getting
    /// a number nearly right, which nothing in the system can tell from getting it right. The two are
    /// rendered from one read, so they cannot contradict each other.
    /// </para>
    /// </summary>
    private static string WithFacts(string prose, object facts) =>
        prose + "\n\n" + JsonSerializer.Serialize(facts, FactsJson);

    /// <summary>Compact: this is read by a model, not printed for a person.</summary>
    private static readonly JsonSerializerOptions FactsJson = new() { WriteIndented = false };

    /// <summary>Answer one retrieval call from the files it reads. Never throws.</summary>
    private string RunRetrieval(ToolCall call)
    {
        try
        {
            switch (call.Name)
            {
                case CompanionTools.Recall:
                {
                    var query = call.StringArgument("query")?.Trim();
                    if (string.IsNullOrEmpty(query))
                    {
                        return "recall needs a query: a word or phrase that would have appeared on the screen.";
                    }

                    var prose = ObservationLog.Search(
                            _notebook.StateDirectory,
                            _notebook.GameStem,
                            query,
                            _config.RecallMaxHits,
                            _config.RecallQuoteChars,
                            _run,
                            _generation)
                        ?? $"Nothing in the record matches \"{query}\".";

                    // The same turns again, as facts, so the companion does not have to read a turn
                    // number out of a sentence — and so a citation can carry the attempt and the fork
                    // it belongs to (04_companion.md §2 (`C6`–`C13`)).
                    var matches = ObservationLog.Matches(
                            _notebook.StateDirectory,
                            _notebook.GameStem,
                            query,
                            _config.RecallMaxHits,
                            _run,
                            _generation)
                        ?? [];

                    return WithFacts(prose, new { query, found = matches.Count, matches });
                }

                case CompanionTools.Saves:
                {
                    // The core typed the filenames, so the core's notes are the only account of
                    // them that exists: the game cannot be asked for a listing (06_tool_surface.md §2.3 (`T14`–`T16`)).
                    var prose = SaveStore.Describe(_notebook.StateDirectory, _notebook.GameStem)
                        ?? $"There are no saves for {_notebook.GameStem} yet: nothing has been "
                        + "written down as a position to come back to.";
                    var saves = SaveStore.List(_notebook.StateDirectory, _notebook.GameStem);
                    return WithFacts(prose, new { game = _notebook.GameStem, found = saves.Count, saves });
                }

                case CompanionTools.ReadNotes:
                {
                    var section = call.StringArgument("section")?.Trim();
                    if (string.IsNullOrWhiteSpace(section))
                    {
                        return _notebook.Read();
                    }

                    return _notebook.Section(section)
                        ?? $"There is no notebook section matching \"{section}\".";
                }

                case CompanionTools.Map:
                {
                    // The one question a search cannot answer: a direction is not a word, and what
                    // a direction does depends on the room it was typed in (06_tool_surface.md §3 (`T24`)).
                    if (TheWorld() is not { } world)
                    {
                        return $"There is no map for {_notebook.GameStem} yet: nothing has been recorded "
                            + "that could be drawn for it.";
                    }

                    var reading = MapReading.Describe(world, call.StringArgument("room"), _status?.Location);
                    return WithFacts(reading.Prose, reading.Facts);
                }

                case CompanionTools.State:
                {
                    // Three questions over one document (05_memory.md §8 (`M41`, `M42`)): what the game
                    // has said about a thing, what has been tried here, and whether a word exists at all.
                    if (call.StringArgument("word") is { Length: > 0 } asked)
                    {
                        return IsAWord(asked.Trim());
                    }

                    if (TheWorld() is not { } world)
                    {
                        return $"Nothing has been recorded for {_notebook.GameStem} yet, so there is no "
                            + "state to read: nothing has been typed at a thing all evening.";
                    }

                    var reading = StateReading.Describe(
                        world,
                        call.StringArgument("room"),
                        _status?.Location,
                        call.StringArgument("thing"));
                    return WithFacts(reading.Prose, reading.Facts);
                }

                default:
                    return $"The harness cannot run {call.Name}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be read is worth less than a companion that keeps talking.
            return $"The record could not be read just now ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Rebuild the world document from the record, and say in the log what it holds.
    /// <para>
    /// Called at every session open, which is the cheap place to be thorough: a rebuild reads the
    /// record and nothing else, so it cannot inherit a mistake from an earlier fold. Whatever the
    /// incremental path got wrong, this replaces.
    /// </para>
    /// </summary>
    private void RebuildTheWorld(string stem)
    {
        try
        {
            var world = WorldState.Rebuild(_notebook.StateDirectory, stem);
            _world = world;
            _log.Info(
                "world state rebuilt from the record",
                new
                {
                    stem,
                    places = world.Places.Count,
                    edges = world.Edges.Count,
                    things = world.Things.Count,
                    actions = world.Actions.Count,
                    generation = world.Header.Generation,
                    as_of_turn = world.Header.AsOfTurn,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worth a warning and never worth a failure: the game keeps playing, and the next open
            // tries again.
            _log.Warn("the world document could not be rebuilt", new { stem, error = ex.Message });
        }
    }

    /// <summary>
    /// Fold one landed turn into the world document.
    /// <para>
    /// Best-effort by design: a projection that cannot be written is worth less than a game that
    /// keeps playing, and the next session open rebuilds it from the record anyway. The turn is
    /// written in the shape the record's own rows have, so a rebuilt document and a folded one are
    /// the same document — which is the test this pair is held to.
    /// </para>
    /// </summary>
    private void NoteTheWorldTurn(TurnCompletePayload turn)
    {
        var book = RunBook.Current(_notebook.StateDirectory, _notebook.GameStem);
        WorldState.Append(
            _notebook.StateDirectory,
            _notebook.GameStem,
            new ObservationLog.RecordedTurn(
                (int)turn.Turn,
                turn.Input,
                turn.Output,
                Fingerprint: string.Empty,
                turn.Location,
                book?.Number ?? 0,
                book?.Generation ?? 0));

        // Whatever was read before this turn was a different world: the next reader reads it again
        // rather than being told what it said before the turn landed.
        _world = null;
    }

    /// <summary>
    /// The world document, read once and kept until the next landed turn.
    /// <para>
    /// Read rather than folded in memory on purpose: what the companion is told is what is on disk,
    /// so the document it quotes from and the document the next session rebuilds are the same one.
    /// </para>
    /// </summary>
    private WorldState.Document? TheWorld() =>
        _world ??= WorldState.Read(_notebook.StateDirectory, _notebook.GameStem);

    /// <summary>
    /// Whether the story's own dictionary holds a word (05_memory.md §8 (`M42`)).
    /// <para>
    /// The daemon is handed the story's word list when it opens the file, so this is the game's own
    /// answer rather than a reading of a refusal sentence. Measured 2026-09-29: the companion spent
    /// turns discovering that `pockets`, `cape`, `heads` and `soot` are not words — *"Pockets isn't a
    /// word, so that's out"* — while the list was already in hand.
    /// </para>
    /// <para>
    /// No list is <em>cannot say</em> and never a no: a story whose vocabulary has not arrived yet is
    /// not a story that does not know the word.
    /// </para>
    /// </summary>
    private string IsAWord(string word)
    {
        if (!_feedback.HasKnownVocabulary)
        {
            return $"No word list has arrived for {_notebook.GameStem} yet, so whether \"{word}\" is a "
                + "word this game knows is *cannot say* — which is not a no, and not a reason to guess.";
        }

        var known = _feedback.Knows(word);
        return WithFacts(
            known
                ? $"\"{word}\" is a word the game's own dictionary holds."
                : $"\"{word}\" is not in this game's dictionary at all. The parser has no such word, "
                    + "so a line built on it cannot work however sensible it looks — a different word "
                    + "for the same thing may, and that is worth one try.",
            new { word, known, words = _feedback.KnownWords });
    }

    /// <summary>
    /// Where this reply stands, for the block of the same name (05_memory.md §2 (`M9.2`)).
    /// <para>
    /// Every field is a fact the harness holds rather than a judgement about the story: the
    /// interpreter's own room number off the turn, the name the story has printed for that room,
    /// which attempt and which fork of the world this is, the clock off the turn, and the last thing
    /// examined out of the player's own command.
    /// </para>
    /// <para>
    /// The fork is read from the run book here rather than taken from the handshake. The handshake
    /// states it once, and a restore in the middle of an evening changes it without sending another
    /// — so a value remembered from the start of the session is wrong for the rest of the night, in
    /// exactly the way that matters most (05_memory.md §2 (`M13.3`)).
    /// </para>
    /// <para>
    /// Each sentence is left out when the harness has nothing to state, and the room says so in
    /// words: **cannot say** is a fact, and a filled-in zero would be an invention the companion
    /// would go on to quote at the player.
    /// </para>
    /// </summary>
    private string WhereThisReplyStands()
    {
        var sentences = new List<string>();
        var status = _status;

        if (status?.Location is { } room)
        {
            sentences.Add(
                _locationName is { Length: > 0 } name && _namedLocation == room
                    ? $"You are in room {room}, which the story printed as \"{name}\"."
                    : $"You are in room {room}; the story has not printed a name for it.");
        }
        else
        {
            // And why, when the reason is a fact the harness holds: a game that draws its own
            // status line has no counters for the interpreter to report (02_game_boundary.md §7 (`G38`)).
            sentences.Add(
                _zVersion is { } version && version >= 4
                    ? $"The interpreter cannot say which room you are in: this game is Z-code v{version}, "
                        + "which draws its own status line, so there are no counters to read."
                    : "The interpreter cannot say which room you are in.");
        }

        if (RunBook.Current(_notebook.StateDirectory, _notebook.GameStem) is { Number: > 0 } attempt)
        {
            sentences.Add(
                attempt.Generation > 0
                    ? $"This is attempt {attempt.Number}, generation {attempt.Generation}."
                    : $"This is attempt {attempt.Number}.");
        }

        if (status?.Hours is { } hours)
        {
            sentences.Add(
                $"The game's clock reads {hours.ToString("00", CultureInfo.InvariantCulture)}"
                + $":{(status?.Minutes ?? 0).ToString("00", CultureInfo.InvariantCulture)}.");
        }

        // The interpreter's other two numbers, which were a tool of their own until 2026-09-29 and
        // were asked for exactly never (see `status` in 06_tool_surface.md §2.3, retired into §3).
        var score = status?.Score;
        var moves = status?.Moves;
        if (score is not null || moves is not null)
        {
            sentences.Add(
                "The interpreter's own count: "
                + (score is { } s ? $"score {s}" : "no score read")
                + ", "
                + (moves is { } m ? $"{m} moves" : "no move count")
                + ".");
        }

        if (_lastExamined.Length > 0)
        {
            sentences.Add($"The last thing examined was \"{_lastExamined}\".");
        }

        // And the baseline the map tool pulls the depth of: what is out of this room, and what has
        // already been tried in it (06_tool_surface.md §3 (`T24`)).
        if (TheWorld() is { } world && MapReading.FromHere(world, status?.Location) is { Length: > 0 } fromHere)
        {
            sentences.Add(fromHere);
        }

        return string.Join(' ', sentences);
    }

    /// <returns>
    /// Whether a move reached the game by the end of the pass, counting one the repair supplied.
    /// </returns>
    private async Task<bool> ApplyReplyAsync(
        ChatResult reply,
        string purpose,
        CancellationToken cancellationToken)
    {
        var truncated = reply.WasTruncated;

        // The repair for a move that was announced and never made is doing its job only if a
        // call is in it. When there is not, its words go nowhere (see NothingCameOfTheRepair);
        // computed once here because three things downstream need it — the thinking, the
        // speaking, and the transcript.
        var repairSaidNothing = NothingCameOfTheRepair(purpose, reply);

        // The reasoning is the companion thinking out loud (04_companion.md §4 (`C26`–`C28`)). It arrives on its own
        // channel, so there is no label to misread and no chance of it being swallowed by the
        // thing above it — which is exactly how it used to be lost.
        if (!string.IsNullOrWhiteSpace(reply.Reasoning))
        {
            _log.CompanionLine("think", reply.Reasoning);
        }

        // One turn is one move. A second `type` is not a second command: the game would already
        // be answering the first. Ignored and reported rather than failed.
        var commands = reply.ToolCalls.Where(call => call.Name == CompanionTools.Type).ToList();
        if (commands.Count > 1)
        {
            _log.Warn(
                "the model asked for more than one command; only the first is used",
                new { ignored = commands.Skip(1).Select(call => call.StringArgument("line")) });
        }

        // The other way to touch the game, and the only act whose payload may legitimately be
        // empty: `press_key` with no argument is a bare Return, which is what a prompt asking
        // for any key will take. Taken here rather than where it is used because the errand
        // logic below has to know a keystroke is a move like any other.
        var pressed = reply.ToolCalls.FirstOrDefault(call => call.Name == CompanionTools.PressKey);

        // A name the harness does not have is worth knowing about: it means the tool list and
        // the model's habits have drifted apart.
        var unknown = reply.ToolCalls
            .Where(call => CompanionTools.All.All(tool => tool.Name != call.Name))
            .Select(call => call.Name)
            .ToList();
        if (unknown.Count > 0)
        {
            _log.Warn("the model called tools that do not exist", new { names = unknown });
        }

        // A job the player handed over, declared by the companion itself (§4.17): the way it
        // corrects the tables when they could not read the line. It starts the same mission the
        // rule path starts — same budget, same endings — which needs the pass it was declared on as
        // well as the call, because the pass is what says whose errand this is. The log records
        // which way each errand arrived, so over-declaring is a measurement before it is an opinion.
        foreach (var call in reply.ToolCalls.Where(call => call.Name == CompanionTools.Errand))
        {
            var goal = call.StringArgument("goal")?.Trim();
            if (string.IsNullOrWhiteSpace(goal))
            {
                _log.Warn("the model declared an errand with no goal", new { purpose });
                continue;
            }

            if (!_allowAct)
            {
                _log.Info(
                    "an errand was declared on a pass that is not the companion's to move in",
                    new { goal, purpose });
                continue;
            }

            _log.Event("errand_declared", new { goal, purpose });
            StartMission(goal, "declared by the companion", purpose);
        }

        // A reply cut off by the ceiling cannot be trusted to *replace* anything: the argument
        // would be the first half of a notebook, and writing it would lose every section it
        // never reached. This used to be guessed at from an unclosed block; the finish reason
        // says it outright (issue #18).
        var rewrites = reply.ToolCalls
            .Where(call => call.Name is CompanionTools.WriteNotes or CompanionTools.WriteMemories or CompanionTools.UpdateNotes)
            .Select(call => call.Name)
            .ToList();
        if (truncated && rewrites.Count > 0)
        {
            _log.Warn(
                "a rewrite was cut off by the length limit; keeping what was there",
                new { tools = rewrites });
        }

        var notes = truncated ? null : Argument(reply, CompanionTools.WriteNotes, "markdown");
        if (notes is not null)
        {
            // A rewrite is the most expensive thing the companion does to its own context: the
            // notebook sits at the front of the prompt, so changing it invalidates every cached
            // token behind it. Measured over one evening: 24 rewrites, and the call after each one
            // paid 8,530 fresh prompt tokens against a session mean of 2,587 — in bursts of three
            // or four inside a minute, each burst paying for the whole prompt again (2026-09-15).
            //
            // `truncated: false` because a truncated rewrite was dropped above, and the store
            // refuses a fragment on its own account too.
            var current = _notebook.Read();
            if (NotebookPolicy.RewriteIsDue(
                    maintenance: IsMaintenance(purpose),
                    overBudget: _notebook.OverBudget(current),
                    lastRewrite: _lastNotebookRewrite,
                    now: DateTimeOffset.UtcNow,
                    minSeconds: _config.NotebookRewriteMinSeconds))
            {
                if (_notebook.TryWriteRewrite(notes, truncated: false))
                {
                    _lastNotebookRewrite = DateTimeOffset.UtcNow;
                    var written = _notebook.Read();
                    // What the rewrite kept and what it left out, by name. A whole-file rewrite is
                    // how the notebook is kept, and it is also how a section the companion wrote ten
                    // minutes ago can disappear — with the event below saying only that a rewrite
                    // happened and no row anywhere that part of the past had gone. Measured on
                    // 2026-09-22: a route log written over ten minutes was gone by the next rewrite,
                    // and finding that out took a reader and the file rather than the log.
                    var headingsBefore = NotebookStore.Headings(current);
                    var headingsAfter = NotebookStore.Headings(written);
                    var leftOut = headingsBefore
                        .Where(title => !headingsAfter.Contains(title, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    _log.Event(
                        "notebook_updated",
                        new
                        {
                            tokens = NotebookStore.EstimateTokens(written),
                            sections_before = headingsBefore.Count,
                            sections_after = headingsAfter.Count,
                        });
                    _log.Info("notebook updated", new { tokens = NotebookStore.EstimateTokens(written) });
                    if (leftOut.Count > 0)
                    {
                        // Evidence rather than a verdict, and it has to stay that way. A rewrite that
                        // consolidates two sections under one heading leaves no section by the old
                        // name, so this count cannot tell a merge from a loss — and a gate on it would
                        // refuse every reorganisation, which is the one thing the rewrite is for: it is
                        // the only pruning the notebook has, and a rewrite that may not drop a heading
                        // is a notebook that can only grow. Measured 2026-09-25: the row named "Where
                        // they DO tie up" and "Iris on the passage (new)" at 03:12:44, and both
                        // subjects are in the file afterwards under other headings. What the row is
                        // good for is telling a reader which names to go and look for.
                        _log.Warn(
                            "the rewrite kept different sections, and has none by these names",
                            new { sections = leftOut });
                    }

                    // Measure, never parse: the contract asks for the canonical sections to be
                    // current at every rewrite, and an evening whose content lives in invented
                    // sections while "Current goals and plan" still says nothing is a presentation
                    // defect worth seeing in the log (measured 2026-09-16).
                    //
                    // Not every empty section is that defect, though: the two that only fill when
                    // something happens are empty for most of a game's life, and a warning that is
                    // true for ever is a warning nobody reads (see
                    // `NotebookStore.SectionsThatOnlyFillWhenSomethingHappens`).
                    var empty = NotebookStore.EmptySectionsWorthReporting(written);
                    if (empty.Count > 0)
                    {
                        _log.Warn(
                            "required notebook sections are empty after a rewrite",
                            new { sections = empty });
                    }
                }
            }
            else
            {
                // The honest price of the interval: these words are lost for this pass. The next
                // allowed rewrite is the one that has to carry them, which is why the gate opens
                // whenever the notes would *shrink* and whenever the notebook is over budget.
                _notebookRewritesRefused++;
                _log.Info(
                    "notebook rewrite refused: it was rewritten a moment ago",
                    new
                    {
                        since_seconds = Math.Round((DateTimeOffset.UtcNow - _lastNotebookRewrite).TotalSeconds),
                        refused_so_far = _notebookRewritesRefused,
                    });
            }
        }

        var memories = truncated ? null : Argument(reply, CompanionTools.WriteMemories, "markdown");
        if (memories is not null)
        {
            _memory.Write(memories);
            _log.Event(
                "memory_updated",
                new { tokens = PersistentMemory.EstimateTokens(_memory.Read()) });
            _log.Info("memories updated", new { path = _memory.FilePath });
        }

        // And the companion's own hypotheses about how the game works (05_memory.md §8 (`M43`)). The
        // one record of the companion's that must survive a session open, because it is the only one
        // the harness cannot rebuild from the record.
        var cause = truncated ? null : Argument(reply, CompanionTools.Cause, "cause");
        if (!string.IsNullOrWhiteSpace(cause))
        {
            var effect = Argument(reply, CompanionTools.Cause, "effect") ?? string.Empty;
            var support = Argument(reply, CompanionTools.Cause, "support")
                ?? string.Empty;
            var confidence = Argument(reply, CompanionTools.Cause, "confidence") ?? "not said";
            var retracts = Argument(reply, CompanionTools.Cause, "retracts");
            var written = Annotations.Append(
                _notebook.StateDirectory,
                _notebook.GameStem,
                "cause",
                cause,
                effect,
                [.. support.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => long.TryParse(part, out var number) ? number : (long?)null)
                    .Where(number => number is not null)
                    .Select(number => number!.Value)],
                confidence,
                long.TryParse(retracts, out var killed) ? killed : null);
            _log.Event(
                "cause_noted",
                new { stem = _notebook.GameStem, id = written.Id, retracts = written.Retracts });
            _log.Info("the companion wrote down what it thinks caused something", new { id = written.Id });
        }

        // The companion's notes about the harness and about itself (05_memory.md §7 (`M37`–`M39`)). Written here,
        // where the other records are written, and read back by nothing: what keeps this from
        // becoming a second conversation is that the harness never puts it in front of the model
        // again, and what makes it worth having is that a workaround for a harness defect is
        // finally written down somewhere that is not the file describing the player.
        var metaNote = truncated ? null : Argument(reply, CompanionTools.MetaNote, "note");
        if (!string.IsNullOrWhiteSpace(metaNote))
        {
            var about = Argument(reply, CompanionTools.MetaNote, "about");
            MetaNotes.Append(
                _notebook.StateDirectory,
                _notebook.GameStem,
                metaNote,
                about,
                DateTimeOffset.UtcNow);
            _log.Event(
                "meta_note",
                new { stem = _notebook.GameStem, about, chars = metaNote.Length });
            _log.Info("the companion wrote a note about the harness", new { about });

            // Counted for the introspection brief, never read back (`§30` item 3). `M37` says this file
            // is never put in front of the model again, and a count is not the note: what the brief
            // needs is not the prose but the fact that this is the eighth time.
            if (!string.IsNullOrWhiteSpace(about))
            {
                _metaNotesThisSitting[about] =
                    _metaNotesThisSitting.TryGetValue(about, out var already) ? already + 1 : 1;
            }
        }

        // And the write-up, when this is the pass that was asked for one (05_memory.md §6 (`M27`–`M36`)). The
        // harness renders it and the harness writes it, so what lands on disk is the same page
        // whatever the model did with its own prose, and the file says which model wrote it, what
        // the pass cost, and where the evening stands (03_session.md §9–§10 (`S58`–`S76`)).
        var report = truncated ? null : Argument(reply, CompanionTools.Report, "markdown");
        if (report is not null && _writingUp is { } writtenUp)
        {
            var facts = new CatchupReport.ReportFacts(
                writtenUp,
                DateTimeOffset.UtcNow,
                reply.Model,
                TurnsOnRecord(writtenUp),
                (decimal)_pricing.CostOfAt(reply.Usage, reply.Model, DateTimeOffset.UtcNow),
                (decimal)_log.Cost);

            try
            {
                // The record goes with it (B5): the page's `Items` and `Events` tabs are the
                // core's own turns, quoted, and only the model can write the rest.
                var (html, _) = CatchupReport.Write(
                    _notebook.StateDirectory,
                    writtenUp,
                    report,
                    facts,
                    ObservationLog.All(_notebook.StateDirectory, writtenUp));
                _log.Event(
                    "report_written",
                    new
                    {
                        stem = writtenUp,
                        turns = facts.Turns,
                        chars = report.Length,
                        model = facts.Model,
                        write_up_cost_usd = facts.WriteUpCostUsd,
                        session_cost_usd = facts.SessionCostUsd,
                    });
                _log.Info("the write-up was written", new { stem = writtenUp, path = html });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A page that cannot be written is a lost page, never a failed session.
                _log.Error("the write-up could not be written", new { stem = writtenUp, error = ex.Message });
            }

            _writingUp = null;
        }

        // A section edit is how a note that has gone stale is fixed without risking the rest
        // of the file (05_memory.md (`M1`–`M39`)): the heading is matched loosely, the body is replaced
        // whole, and every other line of the notebook is left exactly as it was.
        //
        // Which is why the two empty arguments are refused rather than obeyed, and the refusal is
        // logged rather than silent. An empty body writes the heading and a blank line over
        // whatever was there, and the log called that an update — so a slip deleted memory and
        // the record of the evening read as an ordinary edit. An empty heading is worse: the
        // heading match treats "" as a match, so the body lands on the *first* heading in the
        // file, whatever it was, under the name of a section the call never named. Measured on
        // 2026-09-22: seven section edits in ten minutes, every one of them a success in the log,
        // and what had been lost was only visible in the file. An empty `type` is refused on the
        // same grounds: a model mistake is not an instruction.
        var sectionHeading = truncated ? null : Argument(reply, CompanionTools.UpdateNotes, "section");
        var sectionText = truncated ? null : Argument(reply, CompanionTools.UpdateNotes, "markdown");
        if (sectionHeading is not null)
        {
            // A model that writes "## Wish-props" means the section called "Wish-props"; the mark
            // used to travel into the event and the file (measured live on 2026-09-16).
            sectionHeading = NotebookStore.NormaliseHeading(sectionHeading);
        }

        if (sectionHeading is not null && string.IsNullOrWhiteSpace(sectionHeading))
        {
            _log.Warn(
                "an update_notes call that named no section was dropped",
                new { markdown_chars = (sectionText ?? string.Empty).Length });
            sectionHeading = null;
        }
        else if (sectionText is not null && string.IsNullOrWhiteSpace(sectionText))
        {
            _log.Warn(
                "an update_notes call with nothing in it was dropped",
                new { section = sectionHeading });
            sectionText = null;
        }

        if (sectionHeading is not null && sectionText is not null)
        {
            if (_notebook.UpdateSection(sectionHeading, sectionText))
            {
                _log.Event("notebook_section_updated", new { section = sectionHeading });
                _log.Info("notebook section updated", new { section = sectionHeading });
            }
            else
            {
                _log.Info("notebook section not found", new { section = sectionHeading });
            }
        }

        // An errand ends when the companion stops acting of its own accord: a step reply that
        // moves nothing *is* the report, in its own words (§iteration 01 §3). No completion tag
        // to parse, and no "am I finished?" heuristic to get wrong.
        //
        // Unless the reply was cut off. Measured live: a truncated step reply carries no command
        // because the command was never written, and reading that as "it has finished" killed an
        // errand after one step. A truncated reply says nothing about intent.
        var moved = commands.Count > 0 || pressed is not null;

        // A step of an errand that moved nothing is the report that ends it (§iteration 01 §3) —
        // but it is only read as a report once the repair has had its go, at the end of this
        // method. This used to end the errand right here, before the repair, so a dropped call
        // in the first seconds of an errand killed it outright. Measured live on 2026-09-14:
        // "keep going until you have given the dog the bone" was over at step 0 — the move
        // landed a second later, but the errand was already gone, and from then on the player
        // was pushing the journey through one step at a time.
        var theErrandEndsWithThisStep =
            _mission is { Active: true } && !moved && purpose is ("mission-step" or "act-refused");

        // The report as a call (04_companion.md §6 (`C42`–`C47`)): the errand is over because the companion said so,
        // not because a reply moved nothing and the harness worked it out. It is the difference
        // between one reply and two for every report, and between a miss that means "announced a
        // move and never made it" and one that also means "has finished".
        //
        // Accepted on any pass, not only a step: an errand the model calls in mid-turn is still
        // over, and a harness that kept the errand in hand after the companion had said it was
        // finished would be arguing with its own agent. The tool is not offered without an errand,
        // so a call with none in hand is drift between the surface and the model's habits: worth
        // knowing about, and nothing to act on.
        var reportedBack = ReportedBack(reply);
        if (!reportedBack && reply.ToolCalls.Any(call => call.Name == CompanionTools.ReportBack))
        {
            _log.Warn("the companion called an errand in when there was no errand in hand", new { purpose });
        }

        if (theErrandEndsWithThisStep && truncated)
        {
            _log.Warn(
                "a step reply was cut off before it could act; asking again",
                new { purpose, ceiling = _config.MaxTokensFor(purpose), escalated = _config.MaxTokensFor(purpose, escalated: true) });
            NoteADrop(
                "cut-off",
                $"the provider's length limit cut your reply off at {_config.MaxTokensFor(purpose)} "
                + "tokens, before the step you were taking, so the harness never saw it and has asked once more");
            await RetryAfterRefusalAsync(
                    "your reply was cut off by the length limit, so I never saw the step. Do it "
                    + "again, and keep it short: the call first — one `type` line — and at most a "
                    + "sentence after it.",
                    cancellationToken,
                    escalate: true)
                .ConfigureAwait(false);
            return false;
        }

        // The other half of a reply cut off by the ceiling, and the half nobody ever sees. An
        // errand step is caught above, because a truncated step silently kills an errand; an
        // *answer* is caught here, because a truncated answer silently kills the conversation.
        // Measured on 2026-09-22: the player corrected the companion's account of a walk, the
        // reply hit the length limit before it had said anything at all, and nothing came back
        // — no words, no question, no move, and no way for the player to tell a companion that
        // had nothing to say from one that had stopped working. Half an answer is an answer and
        // is left alone, since the player can see it was cut off and ask for the rest; this is
        // the case where there is nothing on the screen to ask about.
        //
        // Asked once, in words that match the tools it is holding: an act is still an act here,
        // so the brief does not forbid one. `thisIsWork` because the answer was asked for — the
        // allowance is for what the companion spends on itself, and this is the player's turn
        // being finished rather than its own idea. The short overload is deliberate: the act
        // permission it defaults to is "the player is not holding the line", which is the same
        // rule everywhere else, and there is no movement history worth paying for here.
        var saidNothing = string.IsNullOrWhiteSpace(reply.Content)
            && string.IsNullOrWhiteSpace(Argument(reply, CompanionTools.Ask, "text"));
        var didNothing = commands.Count == 0 && pressed is null;
        if (truncated && saidNothing && didNothing && _cutOffAsks == 0)
        {
            _cutOffAsks++;
            _log.Warn(
                "the reply was cut off before it said anything; asking again",
                new { purpose, ceiling = _config.MaxTokensFor(purpose), escalated = _config.MaxTokensFor(purpose, escalated: true) });
            NoteADrop(
                "cut-off",
                $"the provider's length limit cut your reply off at {_config.MaxTokensFor(purpose)} "
                + "tokens before it had said anything, so nothing of it reached your friend");
            // The retry's own outcome is this reply's outcome: a caller asking "did anything come
            // of this pass" wants the answer about the words that were actually said, which is the
            // same thing the repair for a dropped call hands back.
            //
            // The ask says *in full*, and that is the escalation's other half: the sentence used to
            // say "in as few words as it takes", because the retry had the same ceiling and brevity
            // was the only lever left. Handing back the same ceiling and asking for fewer words is
            // how a sentence and a gate come to disagree, and this loop has paid for that twice.
            return await RespondAsync(
                    "your last reply was cut off by the length limit before you had said anything, so "
                    + "nothing of it reached the player and the screen has not changed. Say the thing you "
                    + "were going to say, in full — and if there was a move in it "
                    + "as well, make it, because nothing else of that reply was kept.",
                    "cut-off",
                    cancellationToken,
                    thisIsWork: true,
                    escalate: true)
                .ConfigureAwait(false);
        }

        var said = false;

        // Did the companion actually do something, as opposed to only asking? It decides
        // whether it is now waiting on the player (issue #23).
        var acted = false;

        // The player's approval (if any) applies to this reply only.
        var approvedByPlayer = _pendingApproval;
        _pendingApproval = false;

        // Thinking out loud comes first, so the player reads the reasoning and then what it made
        // of it. Published rather than private: the working is the company, and hiding it is what
        // made the companion read as terse and wooden (04_companion.md §4 (`C26`–`C28`)).
        if (!string.IsNullOrWhiteSpace(reply.Reasoning) && !repairSaidNothing)
        {
            await _seat
                .SendAsync(MessageTypes.Thought, new { text = reply.Reasoning })
                .ConfigureAwait(false);
        }

        // Say the announcement before anything is sent to the game, so the player reads the
        // intent before the consequence.
        var speech = reply.Content.Trim();
        if (speech.Length > 0 && !repairSaidNothing)
        {
            _log.CompanionLine("say", speech);
            await _seat.SendAsync(MessageTypes.Chat, new { text = speech }).ConfigureAwait(false);
            // Its own words are the room moving, and the idle clock is measured from the last
            // thing *either* of them did (04_companion.md §5 (`C29`–`C41`)). The pace bounds how long they take to arrive —
            // twenty seconds at the ceiling, well inside any window anything asks about — so
            // "it has spoken" is enough to know the room is not silent.
            _state.NoteTheExchangeMoved("the companion's own words");
            said = true;
        }

        var ask = Argument(reply, CompanionTools.Ask, "text")?.Trim();
        if (ask is not null && ask.Length == 0)
        {
            ask = null;
        }

        // What the companion said out loud is what gets shown if it wants the player's blessing
        // on something; its private reasoning only stands in when it said nothing at all.
        var why = speech.Length > 0 ? speech : "(no reasoning given)";
        var hasAnnouncement = speech.Length > 0 || ask is not null;

        string? act = commands.Count > 0 ? commands[0].StringArgument("line")?.Trim() : null;
        if (act is not null && act.Length == 0)
        {
            // An empty `type` is a model mistake, not a keystroke: pressing a key has a call of
            // its own, and this one is dropped so the two can never be confused.
            act = null;
        }

        // A keystroke is an act, and the empty one is the whole point of it. Not trimmed: a line
        // of one space is a space *key*, and trimming it would turn it into a Return, which the
        // game sees as a different character.
        var keystroke = false;
        if (pressed is not null && act is null)
        {
            act = pressed.StringArgument("key") ?? string.Empty;
            keystroke = true;
        }

        // A reply that is not the companion's to move in is enforced here, not merely asked for
        // in the constraint. Dropped rather than retried: this is our own instruction being
        // overridden, not a policy refusal — and the speech in the same reply still goes out,
        // so the player is never left with nothing.
        //
        // The hold is read here *as well as* at the start of the pass, because the two are not
        // the same moment: `_allowAct` is decided when the call goes out and the reply lands a
        // second or two later, and a space typed in between is a hand held up mid sentence.
        // Measured on 2026-09-22: a pass that had been handed the turn before the space arrived
        // typed `north` after it, which is the one thing the hold is for (04_companion.md §6 (`C42`–`C47`)).
        if ((!_allowAct || _state.IsHeld) && act is not null)
        {
            _log.Info(
                "the companion tried to move when the turn was not its to move in",
                new { line = act, purpose, held = _state.IsHeld });
            NoteADrop(
                "not-its-turn",
                _state.IsHeld
                    ? $"you wrote `{act}` while your friend's line began with a space, so the "
                      + "harness held it back: that space is how they say wait while I write this"
                    : $"you wrote `{act}` in a turn that was theirs to move in, so the harness did "
                      + "not type it");
            act = null;
        }

        // A9: the same line again, when nothing has answered the last one.
        //
        // Measured in the 2026-09-21 UAT: 9 non-movement lines typed twice in a row, 3 of them real
        // double executions — `examine case`, `give envelope to woman`, `take can`. The mechanism is
        // always this: the first line goes, a turn arrives *for something else*, and the next pass
        // reads that turn as the answer and types the same line again. A direction duplicated in a
        // row is legitimate walking and is untouched: walking means the first step was answered
        // (`north`, the room's answer to `north`, then `north`).
        //
        // The move is not refused and not repaired: it is already on its way, which is exactly what
        // the companion meant to happen. It is held, logged, and the pass counts as having acted —
        // so the errand carries on and no second call is spent asking why nothing moved.
        if (act is not null
            && !keystroke
            && string.Equals(act, _lineInFlight, StringComparison.Ordinal)
            && !_lineInFlightAnswered)
        {
            _log.Event(
                "line_held",
                new
                {
                    line = act,
                    purpose,
                    why = "the same line is in the game's hands and has not been answered",
                });
            _log.Info(
                "the same line was already sent and has not been answered; holding it",
                new { line = act, purpose });
            acted = true;
            act = null;
        }

        if (act is not null)
        {
            var decision = DecideAct(act, hasAnnouncement, approvedByPlayer);
            switch (decision.Decision)
            {
                case ActDecision.Execute:
                    _log.Event(
                        "act",
                        new
                        {
                            line = act,
                            keystroke,
                            channel = approvedByPlayer ? "approved" : "sent",
                        });

                    // Words with the move are no longer required of it, and no longer logged as a
                    // prompt problem when they are missing (04_companion.md §12: the screen echoes every
                    // command, so a caption under each one is narration). Whether it spoke is in
                    // the act decision's own detail, for the log's sake and nothing else.
                    _refusedActs = 0;
                    _lastAttemptMissed = false;
                    // A step that moved is the errand carrying on, so the stutter count starts again.
                    _missedSteps = 0;
                    await SendGameInputAsync(act, byAgent: true, cancellationToken, key: keystroke)
                        .ConfigureAwait(false);
                    acted = true;
                    act = null;
                    break;

                case ActDecision.Propose:
                {
                    var proposal = new PendingProposal(
                        $"p{Interlocked.Increment(ref _proposals)}",
                        act,
                        why,
                        RiskLevels.Label(Vocabulary.RiskOf(act)),
                        DateTimeOffset.UtcNow);
                    _state.NoteProposal(proposal);
                    _log.Event("proposal", new { id = proposal.Id, action = proposal.Action, risk = proposal.Risk });
                    // The core renders the proposal for the player, so there is no need
                    // for a question here — and no line of ours if the model wrote none.
                    await _seat
                        .SendAsync(
                            MessageTypes.Proposal,
                            new { id = proposal.Id, action = proposal.Action, reasoning = proposal.Reasoning, risk = proposal.Risk })
                        .ConfigureAwait(false);
                    acted = true;
                    act = null;
                    break;
                }

                default:
                    _log.Warn("the companion's action was not sent", new { line = act, reason = decision.Detail });
                    NoteADrop(
                        "refused",
                        $"you wrote `{act}` and the harness did not send it: {decision.Detail}. "
                        + "That is the harness's doing rather than the game's, so nothing on the "
                        + "screen will explain it.");

                    // The explanation *is* the ask, so the repair for an announced move must not
                    // follow it with a second one: the model would be told twice that nothing was
                    // sent, in two different sets of words, and asked twice what it meant.
                    _missedActs++;
                    await RetryAfterRefusalAsync(decision.Detail, cancellationToken).ConfigureAwait(false);
                    act = null;
                    break;
            }
        }

        if (ask is not null)
        {
            _log.CompanionLine("ask", ask);
            await _seat.SendAsync(MessageTypes.Ask, new { text = ask }).ConfigureAwait(false);
            _state.NoteTheExchangeMoved("the companion's own question");
            said = true;
        }

        // Issue #23: a question is state, not just a line of prose. Nothing used to record
        // that an `[ask]` had gone out, so the idle clock carried on counting from the
        // player's last line and the companion talked over the question it had just asked.
        // Recording it is also what stops it answering its own question from the context of
        // the next turn.
        if (acted)
        {
            _state.ClearAnswer();
        }
        else if (ask is not null)
        {
            _state.NoteAsked(ask);
            _log.Event("awaiting_answer", new { question = ask });
        }

        if (act is not null)
        {
            // Belt and braces: every path above either acts or clears the act.
            _log.Warn("an unhandled action was dropped", new { line = act });
        }

        if (said)
        {
            _triggers.NoteSpoke();
        }
        else
        {
            _triggers.NoteTurn();
        }

        var repaired = await NoteAMissedMoveAsync(acted, ask, truncated, purpose, reply, cancellationToken)
            .ConfigureAwait(false);

        // The repair has had its go. A step that moved nothing even after being asked once is the
        // report, in the companion's own words, and the errand is done.
        //
        // A report *called* in ends it there and then, whether or not the repair was needed and
        // whether or not the same reply also carried a move: the companion has said it is
        // finished, so the errand is finished, and the move goes out on its own account.
        if (reportedBack)
        {
            _log.Event(
                "report_back",
                new
                {
                    goal = _mission?.Goal,
                    steps = _mission?.StepsTaken,
                    speech_chars = speech.Length,
                    moved,
                    purpose,
                });
            _log.Info("the companion called the errand in", new { purpose });
            EndMission("the companion called the errand in");
        }
        else if (theErrandEndsWithThisStep && !repaired)
        {
            // A step that moved nothing, after being asked once, is the report — unless it is the
            // first one. The errand stands where it stood and the tick takes the next step from
            // there, which is what the hold does and for the same reason (04_companion.md §6
            // (`C42`–`C47`)): a single dropped call is a pause, and reading it as "the companion has
            // finished" ended the job out loud while the companion was still saying it was going.
            if (purpose == "mission-step" && ++_missedSteps < MissedStepsBeforeTheErrandEnds)
            {
                _log.Info(
                    "the step moved nothing and the errand stands where it stood",
                    new
                    {
                        step = _mission?.StepsTaken,
                        missed_steps = _missedSteps,
                        budget = _mission?.Budget,
                    });
            }
            else
            {
                EndMission("the companion reported back");
            }
        }

        return acted || repaired;
    }

    /// <summary>
    /// Is this pass a step of something the player asked for, rather than the companion's own
    /// idea? The errand's budget is what bounds work; the allowance is only for what it spends
    /// on itself.
    /// </summary>
    private static bool IsWorkInHand(string purpose) => purpose == "mission-step";

    private static bool SilenceIsAnAnswer(string purpose) =>
        purpose is "handover" or "veto" or "act-refused" or "mission-report";

    /// <summary>
    /// Is this pass one that writes rather than moves?
    /// <para>
    /// One question rather than three spellings, because the tool subset is what *enforces* "a note and
    /// never a lever" (04_companion.md §3.3 (`C24.1`)): a pass that may not move MUST not be offered
    /// anything that can, and a purpose added to a repair list but not to this one becomes a request
    /// instead of a rule.
    /// </para>
    /// </summary>
    /// <summary>
    /// Is this pass the harness talking to itself rather than to the player?
    /// <para>
    /// The maintenance pair and the introspection pass answer nobody: what they produce is the
    /// notebook, the memories, a page on disk, or a note about the machinery. Two readers want the
    /// answer — the plain line for a silent pass (04_companion.md §3 (`C16.1`), which is for a pass
    /// whose job is to answer the player) and the tool subset.
    /// </para>
    /// </summary>
    private static bool IsTheHarnessTalkingToItself(string purpose) =>
        IsMaintenance(purpose) || IsIntrospection(purpose);

    private static bool WritesRatherThanMoves(string purpose) =>
        IsTheHarnessTalkingToItself(purpose) || purpose == SilentReportPurpose;

    /// <summary>
    /// Does this pass owe somebody a line? (04_companion.md §2 (`C16.1`), `C70`)
    /// <para>
    /// Read from the role table rather than kept as a second list of names, for the reason the
    /// annotations are read for the read-only question: the day a pass is registered as a reply, this
    /// answers with it. `silent-report` is the one exception the table cannot express — it is a *write*
    /// because a report's length is bounded like a document's, and it owes the player the report's words.
    /// </para>
    /// </summary>
    private static bool OwesThePlayerALine(string purpose) =>
        Passes.RoleFor(purpose) == PassRole.Reply || purpose == SilentReportPurpose;

    /// <summary>
    /// May this pass spend itself on rewriting a file it keeps?
    /// <para>
    /// Not when it owes the player a line, unless the rewrite is forced: a notebook over its budget is
    /// being paid for on every call already, so pruning it is worth a turn's words, and nothing else is.
    /// </para>
    /// </summary>
    private bool MayRewriteTheNotebook(string purpose) =>
        !OwesThePlayerALine(purpose) || _notebook.OverBudget(_notebook.Read());

    /// <summary>
    /// Something dropped on the harness's side, said in the harness's own words (05_memory.md §7 (`M37`–`M39`)).
    /// <para>
    /// Nothing here reads the model's prose: every row is an event this loop already wrote —
    /// a move the policy would not send, a reply the provider cut off, an errand step that moved
    /// nothing even after being asked once — which is the same rule the whole of 02_game_boundary.md §8 (`G41`–`G45`) rests
    /// on. The event goes to the log whether or not the row fits, because the count of these in an
    /// evening is what the pass below is judged by.
    /// </para>
    /// </summary>
    private void NoteADrop(string kind, string row)
    {
        if (_harnessReport.Note(kind, row))
        {
            _log.Event("harness_drop", new { kind, row });
        }
    }

    /// <summary>
    /// Hand the companion the harness's own account of what has gone wrong, and ask it what it
    /// makes of that (05_memory.md §7 (`M37`–`M39`)).
    /// <para>
    /// This is the piece the 2026-09-21 measurement asked for. `meta_note` was offered on every
    /// pass and called <b>0 times in 349 turns</b>, while 163 of the companion's own thinking lines
    /// were about the machinery — <i>"the harness says one call per turn"</i>, <i>"the harness
    /// insists"</i>, <i>"no record of the actual call being sent"</i>. The moments were there and the
    /// surface was there, so the description was never the lever, and the spec said what that
    /// would mean: 0 calls again in the next bundle means the channel is the wrong shape. The
    /// 2026-09-22 bundle was that bundle. This is the shape instead: the harness says what it
    /// knows, at the moment it happens, and the pass that follows exists to write the note rather
    /// than merely to allow it.
    /// </para>
    /// <para>
    /// It costs a call, so it is bounded four ways: once per turn, only when there is something to
    /// say, paid for from the companion's own allowance rather than the errand's — this is its own
    /// curiosity, not the player's work — and it never moves anything, because the tools that
    /// move are not in the list it is offered.
    /// </para>
    /// </summary>
    private async Task LookAtWhatDroppedAsync(string purpose, CancellationToken cancellationToken)
    {
        if (_introspected || _harnessReport.IsEmpty)
        {
            return;
        }

        // Not for its own reply, which would be the companion reading its own notes about its own
        // notes, and not while the model is away: the call would not be made, and the report would
        // have been cleared on the way past with nobody having read it.
        if (IsIntrospection(purpose) || !IsModelAvailable(IntrospectPurpose))
        {
            return;
        }

        // And it waits for the player to let go of the line: a pass that speaks while they are
        // writing is a pass that talks over them (03_session.md §6 (`S41`–`S43`)), and nothing about a refused
        // move stops being true ten seconds later. The report is *not* cleared by the wait, so the
        // space coming off the front of their line is enough for it to be read.
        if (_state.IsHeld)
        {
            return;
        }

        _introspected = true;
        var report = _harnessReport.Render();
        _harnessReport.Clear();
        _log.Event("introspect", new { because = purpose });
        _log.Info(
            "the harness's account of what went wrong is in front of the companion",
            new { because = purpose });

        // A number this report cannot carry, because it is not about this stretch of play (`§30` item
        // 3). Measured 2026-10-02: **25 notes in one sitting, eight of them the same self-diagnosis**
        // — a call that went out with no words beside it — while the repair they described fired 16
        // times. The channel works; nothing consumes it, which is the 2026-09-29 finding unchanged.
        //
        // Counting is deliberate, and it is the difference `M37` rests on: the note's own words are
        // never put back in front of the model, but "you have written about this before, this many
        // times" is a fact about the sitting rather than a reading of the note.
        var alreadyNoted = _metaNotesThisSitting
            .Where(pair => pair.Value > 1)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"`{pair.Key}` ×{pair.Value}")
            .ToList();
        var repeats = alreadyNoted.Count == 0
            ? string.Empty
            : "\n\nAnd one thing the report cannot tell you, because it is not about this stretch of "
                + "play: you have written notes about this sitting before — "
                + string.Join(", ", alreadyNoted)
                + ". A note about something you have already noted is a note nobody has acted on. If it "
                + "is still true and it still matters, say what you are going to do differently, in one "
                + "sentence; otherwise write nothing and say nothing.";

        await RespondAsync(
                report + "\n\n"
                + "That is the harness and not the game: nothing on the screen explains it and your "
                + "notes cannot answer it. You have the tools that record and none that move, so there "
                + "is nothing here for you to try in the game — and one thing worth doing. If this "
                + "tells you something about the machinery, or about your own play rather than about the "
                + "story, write it down as a `meta_note`: one or two sentences, in your own words, for "
                + "whoever has to work out what went wrong with the session. If it tells you nothing you "
                + "did not already know, write nothing and say nothing.\n\n"
                + "One thing the report cannot tell you from here: this pass is the only one without "
                + "the acting tools. The pass it is about had them, so a note saying the harness took "
                + "your `type` tool away is a note about the wrong pass — measured 2026-09-25, two of "
                + "one evening's six notes said exactly that."
                + NothingHereIsTheirsToExplain + "\n\nNothing you write on this pass is for them "
                + "either: the note is the whole of the output, and anything said beside it is said "
                + "to somebody who is reading a game."
                + repeats,
                IntrospectPurpose,
                cancellationToken,
                wantMovements: null,
                // Not its turn to move in, said in this pass's own words rather than the generic
                // ones (see <see cref="IntrospectNote"/>) — and the recording tools mean the
                // sentence and the gate are saying the same true thing rather than one of them
                // carrying the load alone.
                allowAct: false)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// It said it was going, and it did not go.
    /// <para>
    /// Measured at two replies in seventeen, and it is the one failure that kills the illusion
    /// outright: a friend who says "going in" and then does not move. The game is still sitting
    /// at its prompt, so nothing has happened — while the reply contained speech, so the player
    /// has every reason to think something did.
    /// </para>
    /// <para>
    /// Asked once, on any pass that allowed a move and ended in neither a call nor a question.
    /// It used to exclude the ones the player caused, on the reasoning that a reply which moves
    /// nothing is a legitimate answer to being spoken to. It is — but the model is the one who
    /// gets to decide that, and it does not decide it by speaking. Measured in one evening of
    /// live play: three of eight replies to the player announced the move in prose and emitted
    /// no call whatever — <i>"South out of the copse first."</i>, <i>"Going."</i>, <i>"I'll put a
    /// question to him"</i> — and the player sat watching a screen that never changed, typed
    /// "the screen seems slow", typed again, and got the move on the third ask. The deciding had
    /// happened and the doing never did.
    /// </para>
    /// <para>
    /// The question is asked in words that are true of any silent reply, and the answer is only
    /// shown when it contains a call (<see cref="NothingCameOfTheRepair"/>): a repair that
    /// decides there was nothing to do is allowed to decide that without the player having to
    /// read about it.
    /// </para>
    /// <para>
    /// Two events, because the log's headline is read by counting one name: <c>act_missed</c> for a
    /// reply that called nothing at all, and <c>called_but_did_not_move</c> for one that called
    /// something which does not move the game. Measured in the 2026-09-21 UAT: 167 misses over 612
    /// calls, of which 27 were the second kind, and this rule was in the repair's own words all
    /// along — the brief was the half that disagreed.
    /// </para>
    /// </summary>
    /// <returns>
    /// Whether the pass made a move in the end — <paramref name="acted"/>, or a call the repair
    /// produced. The errand logic needs to tell a report from a dropped call, and this is the
    /// only thing that can.
    /// </returns>
    private async Task<bool> NoteAMissedMoveAsync(
        bool acted,
        string? ask,
        bool truncated,
        string purpose,
        ChatResult reply,
        CancellationToken cancellationToken)
    {
        // The hold is read here as well as at the act gate, and for the same reason: a line begun
        // with a space can arrive while the companion is mid thought, and the repair asked for
        // then would be asking it to move in a turn that stopped being its own the moment the
        // player started writing (04_companion.md §6 (`C42`–`C47`)).
        if (!_allowAct || _state.IsHeld || acted || ask is not null || truncated || _missedActs > 0)
        {
            return acted;
        }

        // Two failures share this shape and they are not the same failure: a reply that called
        // nothing at all — the model said it was going and forgot to go — and a reply that called
        // something that does not move the game: a notebook rewrite, a search, an errand declared
        // in the same breath. Both need the same repair, so both are still asked, and the split is
        // in the *name of the event*, because that is what the next evening's headline number is
        // read from. Measured 2026-09-21: 167 of these read as 167 forgotten calls, and 27 of them
        // were replies that had called tools — 19 of the 19 that only recorded were recovered by
        // the repair 13 times, so the repair is right about all of them and only the label was
        // wrong.
        //
        // Computed here rather than where it is logged, because two rules read it now: the label,
        // and whether the call left the player without a word (immediately below).
        var calledButDidNotMove = reply.ToolCalls.Count > 0
            && !reply.ToolCalls.Any(call => CompanionTools.ActsOnTheGame(call.Name));

        // The reply called the errand in: that *is* the report, and there is nothing to ask it for a
        // move. Checked here rather than passed in, because the condition and the conclusion belong
        // together — the call is what makes the ending deliberate rather than inferred.
        //
        // The words are the whole of it, though (06_tool_surface.md §2.2 (`T13.1`)): *the words beside
        // the call are the report and MUST stand on their own*. A call with nothing beside it ends the
        // errand and tells the player nothing at all, so the only thing that reaches them is the
        // harness's line about nothing having come of it — said about an errand that may well have
        // been done. Measured 2026-09-30: six of nine reports in one sitting carried no words, and
        // four of those six ended an errand that had in fact succeeded. Asked once, and for the words
        // only: the errand is over either way, so there is nothing to decide and only something to say.
        //
        // No bound of its own beyond the once: `report_back` is offered only while an errand is in
        // hand, and this ask is made before the errand is recorded as ended, so a report that never
        // finds its words would otherwise go on asking for them for ever. If the ask comes back
        // empty, the *next* pass is the one the plain line is for, and there it is true.
        if (ReportedBack(reply))
        {
            if (reply.Content.Trim().Length == 0 && _silentReports == 0)
            {
                _silentReports++;
                _log.Warn("the errand was called in with nothing said beside it", new { purpose });
                NoteADrop(
                    "silent-report",
                    "the companion called the errand in with no words beside the call, so the player "
                    + "was told the errand was over by nobody");
                await RespondAsync(
                        "you called the errand in with nothing beside it, so your friend has been told "
                        + "nothing at all. Say the report now, in your own words and nothing else: what "
                        + "the job was, what you did, and what you found. There is no call to make — the "
                        + "errand is already over."
                        + NothingHereIsTheirsToExplain,
                        SilentReportPurpose,
                        cancellationToken,
                        wantMovements: null,
                        allowAct: false,
                        thisIsWork: true)
                    .ConfigureAwait(false);

                // True, and it is not a claim about a move: this pass ended the errand *and* went to
                // get the words for it, so the room is not quiet and `C16.1`'s plain line would be
                // the harness talking over the companion.
                return true;
            }

            return false;
        }

        // **A call with nothing beside it is not an answer** (06_tool_surface.md §2.2 (`T13.1`)). The
        // branch above is that rule where it was measured — the report that says nothing — and this is
        // the same rule where the sitting of 2026-10-01 found it: a pass answering the player called
        // `errand`, said nothing, and the only thing that reached them was the harness's line about
        // nothing having come of it. Measured live, as a pair of rows, which is what made it findable:
        // `called_but_did_not_move {purpose: player-line, tools: [errand], speech_chars: 0}` beside
        // `harness_drop {the companion called the errand in with no words beside the call}`.
        //
        // Only where the words are the player's to read: a maintenance pass writing the notebook owes
        // nobody a sentence, and asking one for words would be the harness inventing a reader
        // (04_companion.md §3 (`C16.1`)). The ask is bounded by the same counter as the report's, which
        // a new turn and the player's arrival both reset, so a pass that never finds its words is asked
        // once per turn and not for ever.
        if (calledButDidNotMove && reply.Content.Trim().Length == 0)
        {
            if (IsTheHarnessTalkingToItself(purpose))
            {
                return false;
            }

            if (_silentReports == 0)
            {
                _silentReports++;
                var called = string.Join(", ", reply.ToolCalls.Select(call => call.Name));
                _log.Warn("the companion called something with nothing said beside it", new { purpose, tools = called });
                NoteADrop(
                    "silent-call",
                    $"the companion called {called} with no words beside the call, so the player was left "
                    + "with nothing but the harness saying nothing had come of it");
                await RespondAsync(
                        $"your last reply called {called} and carried no words at all, so your friend has "
                        + "been told nothing. Say it now, in your own words and nothing else: what you are "
                        + "doing, and why. If the move you were describing is still to be made, make the "
                        + "call in the same reply."
                        + NothingHereIsTheirsToExplain,
                        SilentCallPurpose,
                        cancellationToken,
                        wantMovements: null,
                        allowAct: true,
                        thisIsWork: IsWorkInHand(purpose))
                    .ConfigureAwait(false);

                // True, and it is not a claim about a move: this pass put a call in the model's own
                // reply and has gone to get the words for it, so the room is not quiet and the plain
                // line (`C16.1`) would be the harness talking over the companion.
                return true;
            }

            return false;
        }

        if (SilenceIsAnAnswer(purpose))
        {
            return false;
        }

        // Nor is silence from an errand whose budget is spent: the brief has just asked for the
        // report in as many words, and a report is a reply that moves nothing. Asking again for a
        // move contradicts the brief the model is holding — measured live on 2026-09-14, the
        // companion said "Walking it", the policy refused the act ("the errand's budget is spent,
        // so it is time to report back"), the retry that followed was refused by the allowance,
        // and the player was left looking at a companion that had promised to walk and had not.
        if (_mission is { Active: true, BudgetExhausted: true })
        {
            return false;
        }

        _missedActs++;

        // What the reply actually carried, so the next evening's log can say which failure this
        // is: prose that forgot the call, a pass with nothing to do, or a reply that was cut off
        // before it reached the call. Measured 2026-09-16: 148 of these in one evening, 153 repair
        // calls and 24% of the spend — instrument before touching the wording, because the
        // wording is already in the constraint block the failed pass carried.
        _log.Event(
            calledButDidNotMove ? "called_but_did_not_move" : "act_missed",
            new
            {
                purpose,
                speech_chars = reply.Content.Length,
                thinking_chars = reply.Reasoning.Length,
                finish_reason = reply.FinishReason.ToString().ToLowerInvariant(),
                tools = reply.ToolCalls.Select(call => call.Name).ToArray(),
                after_another_miss = _lastAttemptMissed,
            });
        _lastAttemptMissed = true;
        _log.Info("the companion announced a move and typed nothing; asking once", new { purpose });
        // **The third outcome is named here, and it was missing.** The ask above offers a move, a
        // report, or "nothing to do" — and not the fourth thing a reply in front of the player can be:
        // a question put to *them*. `NothingCameOfTheRepair` counts `ask` as a call of consequence all
        // along, so the surface and the brief disagreed, and the model that wanted the player to
        // choose had no call named for it. Measured 2026-10-01, in the replay sitting: a player line
        // answered with **1,522 characters** of briefing that ended "Say which and I'll do it" — an
        // offer, in prose, with no call — so the harness read a complete answer as a failed move, spent
        // a whole prompt repairing it, and the player got a second, smaller line ("Fair enough — I said
        // the signpost costs nothing") instead of the answer standing on its own.
        var recovered = await RespondAsync(
                "your last reply carried no move, so nothing new was typed and the game has "
                + "not been given anything since. If there was a move in what you were saying, "
                + "make it now, and nothing else: one call in this reply (a `type` line with a "
                + "single game command, or `press_key` if the game is waiting for a key). If you "
                + "were putting a choice or a question to your friend, `ask` is the call for that — "
                + "a question left in prose leaves the harness unable to see what you are waiting "
                + "for, and your friend unable to answer it. If your last reply was complete and "
                + "there was nothing to do, call `report_back` to finish the errand — or, with no "
                + "errand in hand, make no call at all and answer with only: nothing to do."
                + NothingHereIsTheirsToExplain,
                "act-missed",
                cancellationToken,
                thisIsWork: IsWorkInHand(purpose))
            .ConfigureAwait(false);

        // And if the ask did not take either, that is the fact the companion is missing: it
        // announced a move and nothing moved, twice. A plain answer to being spoken to is not a
        // drop and is left alone (— there is nothing for the game to have missed), but a reply
        // that reached for its own notes instead of moving, or a step of work the player asked
        // for, is exactly the shape of "it says it is going and does not go" (05_memory.md §7 (`M37`–`M39`)).
        if (!recovered && (IsWorkInHand(purpose) || purpose == "game-prompt" || calledButDidNotMove))
        {
            NoteADrop(
                "no-move",
                IsWorkInHand(purpose)
                    ? "you said what you were going to do and no move was typed; the harness asked "
                      + "once more and that reply carried none either, so the errand is where it stood"
                    : "your reply called things that do not move the game and carried no move; the "
                      + "harness asked once more and that reply carried none either");
        }

        return recovered;
    }

    /// <summary>
    /// Did the companion call its errand in?
    /// <para>
    /// The tool is only offered while there is an errand in hand, so an errand is part of the
    /// question rather than a separate check at each call site.
    /// </para>
    /// </summary>
    private bool ReportedBack(ChatResult reply) =>
        _mission is { Active: true }
        && reply.ToolCalls.Any(call => call.Name == CompanionTools.ReportBack);

    /// <summary>
    /// Did the repair for a move that was announced and never made come to nothing?
    /// <para>
    /// The repair exists to produce a call. When it produces none its words are not for the
    /// player: the reply being repaired has already been read, and a paragraph explaining that
    /// there was nothing to do is a machine thinking out loud at a screen nobody is watching.
    /// The calls still happen — notes, memories, an answer asked for — and only the words are
    /// held back, from the screen and from the transcript alike, so that the record the model
    /// reads back is the conversation the player actually had.
    /// </para>
    /// <para>
    /// <c>report_back</c> counts as a call of consequence rather than as a way of saying nothing:
    /// the words beside it <em>are</em> the report the errand ends with, so they are the one thing
    /// in this pass the player must read.
    /// </para>
    /// </summary>
    private static bool NothingCameOfTheRepair(string purpose, ChatResult reply) =>
        purpose == "act-missed"
        && !reply.ToolCalls.Any(call => call.Name
            is CompanionTools.Type or CompanionTools.PressKey or CompanionTools.Ask
            or CompanionTools.ReportBack);

    /// <summary>
    /// Did this pass give the player something to read, somewhere that is not the conversation?
    /// <para>
    /// One call does that: the write-up writes the page they asked for into `catchup/`, which is the
    /// pane they are reading it in, and says nothing in the chat. That makes it neither speech nor a
    /// move, so `C16.1`'s guard would otherwise call it nothing — measured 2026-09-30, where the
    /// plain line arrived directly under a log row saying the page had just been written.
    /// </para>
    /// <para>
    /// `report_back` is deliberately not here. Its words *are* the report and they belong in the
    /// conversation, so a wordless one is asked for rather than excused
    /// (<see cref="NoteAMissedMoveAsync"/>), and if the ask comes back empty the plain line is the
    /// right answer about that pass.
    /// </para>
    /// </summary>
    private static bool AnsweredOutsideTheChat(ChatResult reply) =>
        reply.ToolCalls.Any(call => call.Name == CompanionTools.Report);

    /// <summary>
    /// Did this pass put a question to the player? (04_companion.md §2 (`C16.1`), `C16.3`)
    /// <para>
    /// The question is the delivery: `ask` carries its words, the harness shows them and waits for the
    /// answer, so a pass that asked has said something to somebody. Measured 2026-10-02 — of a
    /// sitting's five *Nothing came of that, and nothing moved.* lines, three arrived under a live
    /// question, and the harness went on waiting for an answer to the thing it had just denied.
    /// </para>
    /// </summary>
    private static bool AskedThePlayer(ChatResult reply) =>
        reply.ToolCalls.Any(call => call.Name == CompanionTools.Ask);

    /// <summary>
    /// Read one string argument out of a tool call, or null when the model omitted it or wrote
    /// something that will not parse. A malformed call is a refusal to report, not a crash in the
    /// middle of a turn: the model is free to be wrong, and the harness has to survive it.
    /// </summary>
    private static string? Argument(ChatResult reply, string tool, string argument) =>
        reply.ToolCalls.FirstOrDefault(call => call.Name == tool)?.StringArgument(argument);

    private (ActDecision Decision, string Detail) DecideAct(string act, bool announced, bool approvedByPlayer)
    {
        var now = DateTimeOffset.UtcNow;

        // The rule itself is in `ActPolicy`, where it can be read and tested without a loop.
        // What it no longer contains is the game's dictionary and its past refusals: those are
        // help text for the model, not a veto on what a person at the keyboard may type.
        return ActPolicy.Decide(
            act,
            announced,
            approvedByPlayer,
            vetoedRecently: _state.IsVetoed(act, now),
            budgetSpent: _mission is { Active: true, BudgetExhausted: true },
            awaitingAnswer: _state.IsAwaitingAnswer,
            standing: _standingRisk);
    }

    /// <summary>
    /// An act was refused. The harness will not write the companion's lines and will not leave it
    /// with a world where its typing has no consequences either: it is told why, and asked again
    /// once. After that it is asked to report instead.
    /// <para>
    /// Told whatever the pass was. This used to happen only inside an errand, so a refusal
    /// anywhere else — a line in the interpreter's namespace, a move in the turn it was told not
    /// to move in, an act honouring a veto — was logged and never mentioned, and the companion
    /// went on believing it had acted.
    /// </para>
    /// <para>
    /// Everything here is work in hand rather than the companion's own chatter, so it is bounded
    /// by the errand and not by the allowance. Measured live: the ending of an errand was refused
    /// by the allowance, and the player got silence where a report was due.
    /// </para>
    /// </summary>
    private async Task RetryAfterRefusalAsync(string reason, CancellationToken cancellationToken, bool escalate = false)
    {
        // One explanation, or one more attempt, between successful acts — however many times the
        // policy refuses in a row. Reset by a successful act and by the player arriving.
        _refusedActs++;

        if (_mission is not { Active: true })
        {
            if (_refusedActs > _config.MaxRefusedActs)
            {
                _log.Info("the refused action was dropped; nothing more said about it", new { reason });
                return;
            }

            await RespondAsync(
                    $"your last move was not sent: {reason}. That is the whole of it — find another way round, or say what it means and "
                    + "move nothing.",
                    "act-refused",
                    cancellationToken,
                    thisIsWork: true,
                    escalate: escalate)
                .ConfigureAwait(false);
            return;
        }

        if (_refusedActs <= _config.MaxRefusedActs)
        {
            await RespondAsync(
                    $"your last move was not sent: {reason}. Find another way that works — or, if you have learned something worth telling, say it and move nothing.",
                    "act-refused",
                    cancellationToken,
                    thisIsWork: true,
                    escalate: escalate)
                .ConfigureAwait(false);
            return;
        }

        await ReportAfterRefusalAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An act was refused and the errand cannot say anything more about it: the companion is
    /// asked to report what it has, in its own words, and move nothing.
    /// </summary>
    private async Task ReportAfterRefusalAsync(string reason, CancellationToken cancellationToken)
    {
        EndMission($"the companion could not act: {reason}");
        await RespondAsync(
                $"you could not do that ({reason}). Tell the player plainly what you tried and what you found, in your own words, and move nothing.",
                "mission-report",
                cancellationToken,
                thisIsWork: true)
            .ConfigureAwait(false);
    }

    /// <summary>A short single-line gist of a turn's output, for an errand's notes.</summary>
    private static string Excerpt(string output)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return line.Length <= 120 ? line : line[..120];
    }

    /// <param name="key">
    /// Is this a keystroke rather than a command line? The bytes are the same either way, but the
    /// core has to know the difference: an empty line is a bare Return, which is what a
    /// "press any key" prompt is asking for and is otherwise a mistake it drops, and a line of
    /// one space is a space key rather than a typo, so it must not be trimmed on the way through.
    /// </param>
    private async Task SendGameInputAsync(
        string line,
        bool byAgent,
        CancellationToken cancellationToken,
        bool key = false)
    {
        _ = cancellationToken;
        _state.NoteInputSent(byAgent);
        _awaitingTurn = true;
        if (byAgent)
        {
            _state.NoteActionTaken(line);
        }

        // A9: the line that is now in the game's hands, and the fact that nothing has answered it
        // yet. Keystrokes are not tracked — a second key at the same prompt is a second key, and a
        // form is filled one character at a time — and neither is a player-directed line, which is
        // the player typing rather than the companion.
        if (byAgent && !key)
        {
            _lineInFlight = line;
            _lineInFlightAnswered = false;
        }

        await _seat.SendAsync(MessageTypes.GameInput, new { line, key }).ConfigureAwait(false);
    }

    /// <summary>
    /// Nothing of ours is in the game's hands any more — the world moved under it, or the core
    /// refused it before it reached the story (A9).
    /// <para>
    /// A refusal is not an answer, but it is the end of that line's life: it never reached the
    /// game, so typing it again is a fresh decision rather than a repeat, and holding it would be
    /// the harness arguing with a companion that has already been told what went wrong.
    /// </para>
    /// </summary>
    private void ForgetTheLineInFlight()
    {
        _lineInFlight = null;
        _lineInFlightAnswered = false;
    }

    // -- model availability -------------------------------------------------

    private bool IsModelAvailable(string purpose)
    {
        if (_llm is null)
        {
            if (!_warnedAboutLlm)
            {
                _warnedAboutLlm = true;
                _log.Error("no API key, so no model calls are possible", new { purpose });
            }

            return false;
        }

        if (DateTimeOffset.UtcNow < _llmPausedUntil)
        {
            _log.Warn("model calls are paused after repeated failures", new { until = _llmPausedUntil, purpose });
            return false;
        }

        return true;
    }

    private async Task NoteModelFailureAsync(LlmException ex, string purpose)
    {
        _consecutiveLlmFailures++;
        var fatal = ex is LlmFatalException;
        var pauseFor = TimeSpan.FromSeconds(Math.Max(30, _config.LlmFailurePauseSeconds));

        _log.Error(
            "model call failed",
            new { purpose, fatal, failures = _consecutiveLlmFailures, error = ex.Message });

        if (_consecutiveLlmFailures == 1)
        {
            // The beginning of an outage, and the only place it is ever dated (04_companion.md §10 (`C56`–`C60`)). What
            // the catch-up needs is *where the game had got to* when this started: everything after
            // this turn is the window the companion missed.
            _awaySince = DateTimeOffset.UtcNow;
            _lastAwayLine = _awaySince;
            _awayFromTurn = _lastTurnSeen;
            _awayPlayerLines.Clear();
            _awayPlayerLinesDropped = 0;
            _modelBackLogged = false;
        }

        if (!fatal && _consecutiveLlmFailures < 3)
        {
            return;
        }

        _llmPausedUntil = DateTimeOffset.UtcNow + pauseFor;
        _log.Warn("pausing model calls", new { seconds = pauseFor.TotalSeconds });

        if (!_warnedAboutLlm)
        {
            _warnedAboutLlm = true;
            await NotifyPlayerAsync(
                "I cannot reach the model right now, so I will stay quiet for a bit. "
                + "Keep playing — I will rejoin when it is back.")
                .ConfigureAwait(false);
            _log.Event("away", new { line = 0, reason = "the model went away" });
        }
    }

    /// <summary>
    /// One honest line about the model being unreachable, now and then (04_companion.md §10 (`C56`–`C60`)).
    /// <para>
    /// The player is in a game with somebody who has stopped answering, and from the keyboard that
    /// is indistinguishable from a companion that has lost interest. So while the model is away —
    /// and only then — the companion says so, in its own voice, on a slow rotation: the first line
    /// says why it has gone quiet, and the ones after it say it is still away, that the game is
    /// theirs, and that nothing of theirs is waiting. Deliberately not a turn: no call is attempted,
    /// nothing is queued and no clock is restarted, because the model is what is broken and the
    /// model cannot be the messenger. Returns whether a line was said, so a tick that has done this
    /// does nothing else with itself.
    /// </para>
    /// </summary>
    private async Task<bool> NoteTheAway()
    {
        if (!TheModelIsAway)
        {
            return false;
        }

        var every = TimeSpan.FromSeconds(_config.AwayNoticeSeconds);
        if (every <= TimeSpan.Zero || DateTimeOffset.UtcNow - _lastAwayLine < every)
        {
            return false;
        }

        _lastAwayLine = DateTimeOffset.UtcNow;
        var line = AwayLines[_awayLine % AwayLines.Length];
        _awayLine++;
        await NotifyPlayerAsync(line).ConfigureAwait(false);
        _log.Event(
            "away",
            new
            {
                line = _awayLine,
                away_seconds = Math.Round((DateTimeOffset.UtcNow - _awaySince).TotalSeconds),
                turns_missed = Math.Max(0, _lastTurnSeen - _awayFromTurn),
            });
        return true;
    }

    /// <summary>
    /// The four things the companion says while the model is unreachable.
    /// <para>
    /// Its own voice, because the alternative — a machine notice in the middle of a conversation —
    /// reads as somebody else having taken over the keyboard. All four are true without the model,
    /// none of them pretends anything is fine, and none of them asks the player a question they
    /// cannot answer. The wording is deliberately unremarkable: this is what a friend says when
    /// they have stepped out of the room for a moment, not an apology.
    /// </para>
    /// </summary>
    private static readonly string[] AwayLines =
    [
        "Still no model — I'm here, just can't think out loud. The game is all yours.",
        "Whatever I say next will have to wait: the model is still unreachable. Nothing of yours is held up.",
        "Still away. Read me the screen when I'm back and I'll pick it up from there.",
        "I'm watching the same game you are. It's the thinking that's broken, not the company.",
    ];

    /// <summary>
    /// Catch up on the window the companion missed (04_companion.md §10 (`C56`–`C60`)).
    /// <para>
    /// One maintenance pass, run once per outage, reading the record forward from the turn the game
    /// had reached when the model went away: what the screen said, what the player typed, and the
    /// instruction to bring its notes up to date with it. It is not a turn — <c>allowAct</c> is
    /// false, so it cannot move anything — but it does get the notebook tools, because the notebook
    /// is exactly what is being brought up to date. It does not spend the silence allowance either
    /// (<c>thisIsWork</c>): this is the harness catching itself up after a fault, not the companion
    /// deciding to talk, and its bound is the outage rather than a counter.
    /// </para>
    /// </summary>
    private async Task<bool> CatchUpAsync(CancellationToken cancellationToken)
    {
        var turns = ObservationLog.Since(
            _notebook.StateDirectory,
            _notebook.GameStem,
            afterTurn: _awayFromTurn,
            limit: _config.CatchUpMaxTurns,
            quoteChars: _config.ObservationQuoteLimit,
            run: _run,
            generation: _generation);

        var say = _awayPlayerLines.Count == 0
            ? "(they said nothing to you while you were away)"
            : string.Join("\n", _awayPlayerLines.Select(line => "> " + line))
                + (_awayPlayerLinesDropped == 0
                    ? string.Empty
                    : $"\n(only the last {_awayPlayerLines.Count} of "
                        + $"{_awayPlayerLines.Count + _awayPlayerLinesDropped} things they said are here, "
                        + "in order — earlier lines from the same outage are not)");

        var minutes = Math.Max(1, Math.Round((DateTimeOffset.UtcNow - _awaySince).TotalMinutes));
        var latest =
            $"For about {minutes} minutes you could not reach the model at all, and the game went on "
            + "without you. You are back now, and before you say anything to your friend you have to "
            + "know where the evening got to:\n\n"
            + (turns ?? "Nothing was played while you were away.")
            + "\n\nWhat your friend said while they were playing:\n" + say
            + "\n\nBring your notes up to date in your own words — where you are, what is true now, "
            + "what you were in the middle of, and what you would do next. Then say one short line to "
            + "your friend about being back. Do not apologise at length and do not replay the whole "
            + "evening back at them: they lived it.";

        var before = _awaySince;
        await RespondAsync(
                latest,
                "catchup",
                cancellationToken,
                wantMovements: null,
                allowAct: false,
                thisIsWork: true)
            .ConfigureAwait(false);

        if (TheModelIsAway || _consecutiveLlmFailures > 0)
        {
            // It failed again, so the window is still open and nothing is forgotten: the next
            // recovery tries once more, with the record read forward from the same turn.
            return false;
        }

        _log.Info(
            "the companion caught up with the window it missed",
            new
            {
                away_seconds = Math.Round((before == DateTimeOffset.MinValue
                    ? TimeSpan.Zero
                    : DateTimeOffset.UtcNow - before).TotalSeconds),
                from_turn = _awayFromTurn,
                to_turn = _lastTurnSeen,
            });
        _log.Event(
            "catchup",
            new { from_turn = _awayFromTurn, to_turn = _lastTurnSeen, player_lines = _awayPlayerLines.Count });

        _catchupDue = false;
        _awaySince = DateTimeOffset.MinValue;
        _awayFromTurn = -1;
        _awayPlayerLines.Clear();
        _awayPlayerLinesDropped = 0;
        // Per outage rather than per process: "tell the player once" is about not saying it every
        // turn, and an outage that begins again in an hour is a new thing that has happened to them.
        _warnedAboutLlm = false;
        return true;
    }

    /// <summary>Is the model unreachable right now, which is the one thing this file says out loud?</summary>
    private bool TheModelIsAway => DateTimeOffset.UtcNow < _llmPausedUntil;

    /// <summary>
    /// The write-up a returning player asked for (05_memory.md §6 (`M27`–`M36`)).
    /// <para>
    /// One pass, on its own purpose, whose whole input is what the harness holds about a game and
    /// whose whole output is one file. It is not a turn: <c>allowAct</c> is false, so it cannot move
    /// anything, and it does not spend the silence allowance (<c>thisIsWork</c>) because the player
    /// asked for it by name. It keeps the recording tools as well as <c>report</c>, so a game whose
    /// notebook is out of date can be written up *and* corrected in the same pass.
    /// </para>
    /// <para>
    /// The game named may not be the one in front of the player: the browser's list writes up
    /// whichever game was clicked, which is the whole point of a page for coming back to something.
    /// Everything here therefore reads by stem rather than from the loop's own stores.
    /// </para>
    /// </summary>
    private async Task WriteUpTheGameAsync(string stem, CancellationToken cancellationToken)
    {
        var game = string.IsNullOrWhiteSpace(stem) ? _notebook.GameStem : stem.Trim();
        if (string.IsNullOrWhiteSpace(game))
        {
            _log.Warn("a write-up was asked for and nothing said which game");
            return;
        }

        _writingUp = game;

        // Three attempts, because the first can be spent on the checks rather than the page.
        //
        // Measured, 2026-09-20: the second write-up of an evening produced nothing at all. The
        // reply carried a `saves()` call and no `report`, and a reply that reaches for anything
        // other than `recall` or `read_notes` ends the pass — so the work of gathering was done,
        // the notes were corrected, and the page was never written. It read from the browser as
        // "generate does nothing when a report already exists", because a game written up a second
        // time is exactly the game whose notes have gone stale enough to check first.
        //
        // The remedy is not to take the recording tools away — correcting the notebook in the same
        // pass is the point — and not to keep the pass alive on their behalf, because a turn's
        // reply is a different thing from a pass's (see `CompleteWithRetrievalAsync`). It is to
        // *repair*: whatever the reply asked to be recorded has been recorded by the time this
        // comes round again, so the second attempt has nothing left to do but write.
        for (var attempt = 1; attempt <= WriteUpAttempts && _writingUp is not null; attempt++)
        {
            if (attempt > 1)
            {
                _log.Info(
                    "the write-up pass is being asked again",
                    new { stem = game, attempt, of = WriteUpAttempts });
            }

            // On the last attempt the pass is offered the report tool and nothing else. The earlier
            // ones have to be able to check a save and correct a stale note; by the last there is
            // nothing left to check, and a one-tool list says so more plainly than a paragraph can.
            _forcingTheReport = attempt == WriteUpAttempts;
            try
            {
                await RespondAsync(
                        TheWriteUpRequest(game, attempt),
                        WriteUpPurpose,
                        cancellationToken,
                        wantMovements: null,
                        allowAct: false,
                        thisIsWork: true)
                    .ConfigureAwait(false);
            }
            finally
            {
                _forcingTheReport = false;
            }
        }

        if (_writingUp is not null)
        {
            // The pass did not produce a report: the model was unreachable, or it would not stop
            // doing something else. The stem is cleared here so the next pass is never written to a
            // page the player did not ask for, and the log says so, because a generate button that
            // appears to do nothing is the one thing this feature cannot afford.
            _log.Warn(
                "the write-up pass ended without a report",
                new { stem = game, attempts = WriteUpAttempts });

            // And the failure is named in the one place a person will look (02_game_boundary.md §9 (`G46`–`G49`)). The
            // companion cannot be asked to report this one: it has no way to know that the harness
            // gave up on its behalf, so the harness says it itself.
            MetaNotes.AppendFromTheHarness(
                _notebook.StateDirectory,
                game,
                $"the write-up pass for `{game}` ended without a report after {WriteUpAttempts} "
                + "attempts. The pass ran and the model answered, but no `report` call came back, so "
                + "nothing was written and an existing page was left alone. A `saves()`, "
                + "`update_notes()` or `write_notes()` call in the reply is the usual cause: those "
                + "end a pass and are recorded, and the writing is meant to come next.",
                DateTimeOffset.UtcNow);

            _writingUp = null;
        }
    }

    /// <summary>How many times a write-up is asked for before it is called a failure.</summary>
    private const int WriteUpAttempts = 3;

    /// <summary>
    /// Is a write-up pass on its last attempt, and therefore offered only the report tool?
    /// <para>
    /// A field rather than a parameter because the tool list is built three frames down, in the one
    /// place that decides what every pass is offered (see <c>RespondAsync</c>). Cleared in a
    /// <c>finally</c>: an exception here must not leave the next ordinary turn unable to type.
    /// </para>
    /// </summary>
    private bool _forcingTheReport;

    /// <summary>
    /// How many of this prompt's lines the core has stopped for wanting a command prompt
    /// (02_game_boundary.md §4.2–§4.3 (`G20`–`G24`)).
    /// <para>
    /// Reset by the next turn, because a turn means the prompt was answered and the next one is a
    /// new question.
    /// </para>
    /// </summary>
    private int _refusedLines;

    /// <summary>
    /// The harness's own account of why it has stopped counting this session as play (02_game_boundary.md
    /// §10.4), or <c>null</c> while the world is moving.
    /// <para>
    /// Held rather than said once, because it is a *standing* fact: it is true of every turn until
    /// the world moves again, and the point of it is that the companion should be reasoning about
    /// how it is playing rather than about the story. The core sends <c>unstuck</c> when the world
    /// moves, so this is cleared by a fact rather than by a guess.
    /// </para>
    /// </summary>
    private string? _stuckNote;

    /// <summary>Has the companion already been asked to think its way out of this one?</summary>
    private bool _stuckAnswered;

    /// <summary>
    /// What a write-up pass is told, on its first attempt and on the ones after it.
    /// <para>
    /// The first attempt says what the pass is for and hands over everything the harness holds. An
    /// attempt after that says what happened — no report came back — and that the checks are
    /// behind us, which is the fact the model was missing rather than the willingness to write.
    /// </para>
    /// </summary>
    private string TheWriteUpRequest(string game, int attempt)
    {
        var opening = attempt == 1
            ? $"You have been asked to write up {game} as a document for your friend, who is coming "
              + "back to it after a long absence and will read this before playing again.\n\n"
              + "It is not a message and not a turn: nobody is at the keyboard, nothing you say here "
              + "reaches anyone as speech, and you cannot move. What you write goes onto a page of its "
              + "own, in your words but not in your voice — the reader wants to know where they are, "
              + "what is left to do, what has already been solved, and how the places they have been "
              + "join up. Everything you know about the game, gathered for you, follows. Nothing else "
              + "exists: there is no second record anywhere in the system."
            : $"{game} still has no write-up: your last reply carried no `report` call, so the page "
              + "was not written. Anything that reply asked to be recorded **has** been recorded — the "
              + "notes, the memories and the save list are all in hand, and calling those tools again "
              + "will tell you nothing you have not just been told.\n\n"
              + "The writing is the only thing left. Everything you know about the game, gathered "
              + "again, follows.";

        return opening
            + "\n\n"
            + EverythingTheHarnessHolds(game)
            + "\n\nWrite the report with the `report` tool, in the five sections it describes. Only "
            + "what you actually know: a report that guesses is worse than a short one, and the "
            + "reader has no way to tell the difference until they are deep in the game. Then be "
            + "silent — there is nobody to speak to.";
    }

    /// <summary>
    /// Everything the harness holds about a game, as one block of text (05_memory.md §6 (`M27`–`M36`)).
    /// <para>
    /// Gathered from the files rather than from the loop's own state, because the game being written
    /// up is often not the one in front of the player. Each part says so when it is empty: "no
    /// notebook yet" is a fact the report should not have to invent, and a section that quietly goes
    /// missing is how a write-up comes to read as a complete picture of a game nobody has played.
    /// </para>
    /// <para>
    /// The screen itself is deliberately not in here. The core writes every byte of it to the
    /// playback as it happens, and an evening of it is hundreds of kilobytes: the turn record is the
    /// same story in a size the model can actually read, and it carries the fingerprint that
    /// <c>recall</c> searches with.
    /// </para>
    /// </summary>
    private string EverythingTheHarnessHolds(string game)
    {
        var state = _notebook.StateDirectory;
        var text = new StringBuilder();

        text.AppendLine($"### The notebook for {game}");
        text.AppendLine(
            ReadFile(Path.Combine(state, "notebook", $"{game}.md"))
            ?? "(there is no notebook for this game: nothing has been written down about it)");

        text.AppendLine();
        text.AppendLine("### What outlives the game — the memories about the player");
        var memories = _memory.Read().Trim();
        text.AppendLine(memories.Length == 0 ? "(nothing written down about the player yet)" : memories);

        text.AppendLine();
        text.AppendLine($"### The turn record — the game's own answers, up to {_config.ReportTurns} turns");
        text.AppendLine(
            ObservationLog.Recent(
                state,
                game,
                limit: _config.ReportTurns,
                quoteChars: _config.ObservationQuoteLimit,
                run: _run,
                generation: _generation)
            ?? "(the core has not watched any turns of this game)");

        text.AppendLine();
        text.AppendLine("### Words this story's parser refused and accepted, most recent first");
        text.AppendLine(Tail(Path.Combine(state, "vocabulary", $"{game}.jsonl"), VocabularyTail)
            ?? "(nothing recorded: the parser has not been asked for anything it did not know, or nothing has been learned yet)");

        text.AppendLine();
        text.AppendLine("### How many times this game has been attempted");
        text.AppendLine(
            ReadFile(Path.Combine(state, "runs", $"{game}.json"))
            ?? "(no record of an attempt)");

        text.AppendLine();
        text.AppendLine("### The last checkpoint, and what the core wrote beside it");
        text.AppendLine(
            ReadFile(Path.Combine(state, "saves", game, $"{game}.json"))
            ?? "(no checkpoint: this game has no saved position the core knows about)");

        return text.ToString();
    }

    /// <summary>A file's contents, or null when it is missing or unreadable.</summary>
    private static string? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How many turns the record holds for a game — the number the report's own masthead shows.
    /// Counted rather than taken from the loop's counters, because the game being written up is
    /// usually not the one being played.
    /// </summary>
    private int TurnsOnRecord(string game)
    {
        try
        {
            var path = ObservationLog.PathFor(_notebook.StateDirectory, game);
            return File.Exists(path)
                ? File.ReadLines(path).Count(line => line.Trim().Length > 0)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The last few lines of a file, oldest first, or null when there is nothing to read.</summary>
    private static string? Tail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var all = File.ReadAllLines(path).Where(line => line.Trim().Length > 0).ToArray();
            if (all.Length == 0)
            {
                return null;
            }

            var tail = all.Length <= lines ? all : all[^lines..];
            return string.Join('\n', tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
    /// <summary>
    /// One short, honest line to the player. Deliberately not via the model: when the
    /// model is what is broken, the model cannot be the messenger.
    /// </summary>
    private Task NotifyPlayerAsync(string text)
    {
        _log.CompanionLine("say", "(system notice) " + text);
        return _seat.SendAsync(MessageTypes.Chat, new { text }).AsTask();
    }

    // -- helpers ------------------------------------------------------------

    /// <summary>
    /// Is the session past its cost bound (04_companion.md §6 (`C42.7`))? Say so once, then answer yes.
    /// <para>
    /// Never a kill, and never a silence: the game keeps playing and the player keeps being
    /// answered. What stops is the companion running itself, which is the part that spends money
    /// with nobody asking for it.
    /// </para>
    /// </summary>
    private async Task<bool> OverTheSessionCostBoundAsync()
    {
        var limit = _config.SessionCostLimitUsd;
        if (limit <= 0 || _log.Cost < limit)
        {
            return false;
        }

        if (!_warnedCostBound)
        {
            _warnedCostBound = true;
            _log.Warn(
                "the session is over its cost bound; the companion is parked",
                new { cost_usd = Math.Round(_log.Cost, 4), limit });
            await NotifyPlayerAsync(
                    $"That last stretch has taken this session past ${limit:0.00}, which is where I stop "
                    + "spending on my own. I will still answer you — I just will not take turns nobody "
                    + "asked for. Say the word and I will pick it up.")
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// What the game showed this turn: the framed output, or the interpreter's own screen when the
    /// core sent one.
    /// <para>
    /// The screen is not a decoration beside the output. On the turns that carry it, the output is
    /// usually empty — a chapter menu, a licence form, a <c>(R)estore?</c> offer: all of them are
    /// on the screen for the whole game and in the output for none of it. That is the turn where
    /// the companion has to answer, so the screen is the turn.
    /// </para>
    /// <para>
    /// When both are present the screen wins, because it is what the player is looking at. The
    /// output is kept only if it says something the screen does not: the interpreter's dumb
    /// interface re-prints a row when it has changed and is not blank, so on these turns the output
    /// is normally a partial re-print of the screen and nothing is lost by preferring the screen.
    /// </para>
    /// </summary>
    private static string GameShowed(TurnCompletePayload turn)
    {
        var output = turn.Output ?? string.Empty;
        if (string.IsNullOrWhiteSpace(turn.Screen))
        {
            return output;
        }

        var screen = turn.Screen.TrimEnd();
        if (string.IsNullOrWhiteSpace(output) || EveryLineIsOnTheScreen(output, screen))
        {
            return screen;
        }

        return screen + "\n\n(the interpreter also re-printed:)\n" + output.TrimEnd();
    }

    /// <summary>Does the screen already say everything the output says?</summary>
    private static bool EveryLineIsOnTheScreen(string output, string screen)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !screen.Contains(trimmed, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private string LatestEventFor(TurnCompletePayload turn, TriggerDecision decision)
    {
        var builder = new StringBuilder();
        builder.Append("game turn ").Append(turn.Turn).AppendLine(" finished.");
        if (!string.IsNullOrWhiteSpace(turn.Input))
        {
            builder.Append("it was caused by: ").AppendLine(turn.Input);
        }

        var showed = GameShowed(turn);
        builder.AppendLine("the game showed:");
        builder.AppendLine(string.IsNullOrWhiteSpace(showed) ? "(no output)" : showed);
        builder.Append("why you are being asked now: ").Append(decision.Reason.ToString().ToLowerInvariant());
        if (decision.Reason == SpeakReason.DecisionPoint)
        {
            // This used to end "...and ask the player", and that one clause is where "shall I
            // do this or that?" came from (issue #29): combined with a trigger that fired on
            // any line of dialogue, almost every reply became a question about what the player
            // would like the companion to do. The player has already given it the wheel; what
            // they want from it is a view.
            builder.AppendLine().Append(
                "This is a decision point: say what the situation is, name the option you like best, "
                + "and give an honest read of the odds. Then do it, or ask only if the decision is "
                + "genuinely the player's — something risky, irreversible, or expensive. Do not lay "
                + "the choices out for them to pick from.");
        }

        if (decision.Reason == SpeakReason.GoingInCircles)
        {
            // The remedy the moment wants (04_companion.md §5 (`C29`–`C41`)): a decision, not a remark. Whether it
            // may move is the caller's to say, and it may.
            builder.AppendLine().Append(GoingInCirclesAsk());
        }

        if (decision.Reason == SpeakReason.GoingNowhere)
        {
            // `C39.4`'s macro fact, and the same remedy: the record's own sentence (in `Detail`, so
            // the prompt and the consult say the same words) plus the ask. It says nothing about what
            // the stretch *means*, because whether to stop is the player's (`F20`).
            builder.AppendLine().Append(GoingNowhereAsk(decision.Detail));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The sentence that turns the record's fact into something to decide about (`C39.4`): the fact,
    /// then the same remedy the circle consult asks for — a decision rather than a remark (`C39.1`).
    /// <para>
    /// Shared by the two places the fact is acted on — an errand's step and an unprompted pass — so
    /// that neither can drift into asking for something different from the other.
    /// </para>
    /// </summary>
    private static string GoingNowhereAsk(string fact) =>
        fact + " If you have a plan for this, make the move now — that is what this turn is for. If "
        + "you have not, say what you are missing and ask, or try the thing you have been avoiding.";

    /// <summary>
    /// What a line from the room is for (`C71`): not work, not a question, and nothing at all when
    /// there is nothing to say.
    /// <para>
    /// Every clause is doing a job. *Not work* and *nothing is waiting on you* keep it away from the
    /// nudge's job; *must not ask them a question* is what makes it company rather than more business;
    /// and *an empty reply is the right answer* is what stops the pass inventing something to fill the
    /// room, which is the failure the persona's own anti-padding rule exists to prevent.
    /// </para>
    /// </summary>
    private static string ALineFromTheRoomAsk(double quietSeconds) =>
        $"nothing has happened for {quietSeconds:F0} seconds: nobody has asked you anything, nothing "
        + "is waiting on you, and there is no work in hand. This pass is not work — it is the part of "
        + "an evening where two people at one keyboard say something to each other. If there is "
        + "anything you would say out loud to the person next to you right now — how the evening is "
        + "going, something you noticed and did not mention, how late it is, a complaint about the "
        + "game's cheap tricks — say it in a sentence or two. It does not have to be useful, it must "
        + "not ask them a question, and it must not set anything going. If there is nothing worth "
        + "saying, say nothing at all: an empty reply is the right answer, and you will not be asked "
        + "again for a while.";

    /// <summary>
    /// The same ask for a circle (`C39.1`), in one place for the same reason: the unprompted pass and an
    /// errand's step both put it to the companion, and they must not come to ask differently.
    /// </summary>
    private static string GoingInCirclesAsk() =>
        "You are going in circles: the same move keeps coming back without anything changing. "
        + "If you have a plan for this, make the move now — that is what this turn is for. If "
        + "you have not, say what you are missing and ask, or try the thing you have been avoiding.";

    /// <summary>
    /// The stretch with nothing new in it, if this is the moment to put it to the companion — and the
    /// record of having done so (`C39.4`, `§25` item 4).
    /// <para>
    /// Null while the fact does not hold, and null inside the window of the last consult. The window
    /// is <see cref="Stagnation.Window"/> rather than a second number, because what makes the tell
    /// honest is that it measures the same stretch the sentence reports.
    /// </para>
    /// </summary>
    private Stagnation.Stretch? StagnationWorthConsulting(long turn)
    {
        if (Stagnation.Look(TheWorld(), turn, TurnsPlayedInTheWindow(turn)) is not { } stretch)
        {
            ExplainTheQuietStretch(turn);
            return null;
        }

        if (turn - _consultedAboutStagnationAt < Stagnation.Window)
        {
            return null;
        }

        _consultedAboutStagnationAt = turn;
        return stretch;
    }

    /// <summary>
    /// Why the stretch is not being put to the companion, once per window (`C39.9`).
    /// <para>
    /// A delta that *cannot* fire and a delta with nothing to say produced the same log — none — and
    /// two sittings on 2026-10-01 were spent working out which: an errand that kept finding new
    /// ground, and then a twenty-two-turn sweep of one room built to have nothing in it. Both
    /// plausible; neither readable. The numbers are `Look`'s own, so the explanation cannot drift
    /// from the decision it explains, and the rate is bounded by the window because the answer
    /// changes only when the window does.
    /// </para>
    /// </summary>
    private void ExplainTheQuietStretch(long turn)
    {
        if (turn <= 0 || turn - _explainedTheStretchAt < Stagnation.Window)
        {
            return;
        }

        _explainedTheStretchAt = turn;
        var counts = Stagnation.Counts(TheWorld(), turn, TurnsPlayedInTheWindow(turn));
        _log.Event(
            "stagnation_quiet",
            new
            {
                turn,
                turns_played = counts?.Turns,
                new_places = counts?.NewPlaces,
                new_things = counts?.NewThings,
                window = Stagnation.Window,
                why = counts?.Why ?? "no world to read",
            });
    }

    /// <summary>
    /// How many turns the record holds inside the window that ends at this turn.
    /// <para>
    /// Counted from the observation log rather than from the document's ledger, and that is the whole
    /// of why this method exists: the ledger holds the lines worth reporting on, and a stretch is made
    /// of turns that were worth nothing, so counting it there gave two turns for a nineteen-turn walk
    /// of Wishbringer's Steep Trail — and a count under half the window is "too young to be a wander",
    /// so the fact could never fire on the one stretch it was written for. Measured 2026-10-01.
    /// </para>
    /// <para>
    /// Held per turn, because every pass in a turn asks and the file is the whole record of the game.
    /// </para>
    /// </summary>
    private int TurnsPlayedInTheWindow(long turn)
    {
        if (turn == _turnsCountedAt)
        {
            return _turnsPlayed;
        }

        var generation = TheWorld()?.Header.Generation;
        var from = turn - Stagnation.Window;
        _turnsPlayed = ObservationLog
            .All(_notebook.StateDirectory, _notebook.GameStem)
            .Count(recorded =>
                recorded.Turn > from
                && recorded.Turn <= turn
                && (generation is not { } current || recorded.Generation == current));
        _turnsCountedAt = turn;

        return _turnsPlayed;
    }

    /// <summary>
    /// What the companion is told when a turn did not come back to a command prompt
    /// (issue #19).
    /// <para>
    /// The game is waiting for something in its own words — a chapter menu, a licence form,
    /// a <c>(R)estore?</c> offer — and the only correct reply is the one a person would type.
    /// </para>
    /// </summary>
    private static string GamePromptEvent(TurnCompletePayload turn)
    {
        var builder = new StringBuilder();
        builder.Append("game turn ").Append(turn.Turn).AppendLine(" finished without coming back to its prompt.");
        if (!string.IsNullOrWhiteSpace(turn.Input))
        {
            builder.Append("the last command was: ").AppendLine(turn.Input);
        }

        var showed = GameShowed(turn);
        builder.AppendLine("the game is showing:");
        builder.AppendLine(string.IsNullOrWhiteSpace(showed) ? "(no output)" : showed);
        builder.Append(
            "The game has not given you a command prompt, so it is asking you something and waiting. "
            + "It is waiting for a **key**, not a command: answer with `press_key`, which sends the "
            + "character you give it and nothing else — a digit, a `y`, a space, or a bare Return. A "
            + "one-word answer to a menu is still a key: `press_key` with `1`, not `type` with `1`. "
            + "An answer longer than a key — the letters of a name — is a run of keys, and `press_key` "
            + "with the whole word sends them in order, one prompt at a time. A `type` line here is "
            + "refused before it reaches the story, because the first character would answer the "
            + "prompt and the rest would be typed into whatever the game counts next. If you "
            + "genuinely cannot tell what it wants, say so in a line and press nothing.");
        return builder.ToString();
    }

    /// <summary>
    /// Pick up the instructions the current game shipped with, or the standing note for a game that
    /// shipped with none.
    /// <para>
    /// Called once per game rather than per reply: these are files that belong to the story (or to
    /// nobody) and that nothing in this program writes, so re-reading them every turn would be a
    /// syscall per model call for a document that cannot have changed.
    /// </para>
    /// </summary>
    private void LoadGameBrief(string stem)
    {
        var (text, problem) = GameBrief.Read(_gamesDirectory, stem, _config.GameBriefMaxChars);

        if (problem is not null)
        {
            // Loud, because the alternative is an operator who wrote instructions for a game,
            // got the permissions or the name wrong, and never found out they were unused. The
            // standing note still applies underneath: a broken file must not cost the player a
            // working companion.
            _log.Warn("the game's sidecar exists but could not be read", new { problem });
        }

        var standing = false;
        if (text is null)
        {
            // The normal case for most games. Without this the absence is silence, and silence
            // reads as "there is nothing to know" rather than "nobody here has this box" — so the
            // stand-in says so, and asks the one person who might still have it.
            (text, problem) = GameBrief.ReadDefault(_gamesDirectory, _config.GameBriefMaxChars);
            standing = text is not null;

            if (problem is not null)
            {
                _log.Warn("the standing note for a game with no sidecar could not be read", new { problem });
            }
        }

        _gameBrief = text;
        _gameBriefIsStanding = standing;

        if (text is null)
        {
            return;
        }

        _log.Info(
            standing ? "this game has no sidecar; the standing note was used" : "this game came with its own instructions",
            new
            {
                file = standing ? GameBrief.DefaultFileName : GameBrief.FileNameFor(stem),
                characters = text.Length,
                truncated = text.EndsWith("in full)_", StringComparison.Ordinal),
            });
    }

    /// <summary>
    /// The player did something, so the companion may start afresh.
    /// <para>
    /// Every reset of the unattended allowance goes through here rather than setting the field,
    /// because the two things that have to happen together are the counter and the flag that
    /// stops the log explaining itself over and over.
    /// </para>
    /// </summary>
    private void NoteThePlayerIsHere()
    {
        _callsSinceThePlayer = 0;
        _maintenanceCallsSinceThePlayer = 0;
        _reportedWaiting = false;
        _missedActs = 0;
        _lastAttemptMissed = false;
        _missedSteps = 0;
        _refusedActs = 0;
        _cutOffAsks = 0;
        _silentReports = 0;
        _introspected = false;
        _harnessReport.Clear();
    }

    /// <summary>
    /// Take stock: one pass in which the companion may check its notes against what has
    /// actually happened, correct them, and write down what it wants to keep (issues #22
    /// and #27).
    /// <para>
    /// Everything else the companion does is a reaction to something — and every reaction has
    /// to produce prose, because a turn has landed and the player is owed an answer. This is
    /// the one call where thinking is the whole job: no act, no announcement. It is also the
    /// only place the memories are normally written, because "what did this evening mean" is
    /// not a question to answer mid-turn.
    /// </para>
    /// </summary>
    private async Task ReconcileAsync(string why, CancellationToken cancellationToken)
    {
        _turnsSinceReconcile = 0;
        _reconciledFor = _notebook.GameStem;
        _log.Event("trigger", new { reason = "reconcile", detail = why });

        var turns = ObservationLog.Recent(
            _memory.StateDirectory,
            _notebook.GameStem,
            limit: _config.ReconcileRecentTurns,
            quoteChars: _config.ObservationQuoteLimit,
            run: _run,
            generation: _generation);
        var brief = new StringBuilder();
        brief.Append("Time to take stock: ").Append(why).AppendLine(".");
        brief.AppendLine(
            "Read your notebook against what has actually happened. Fix anything that is now wrong. "
            + "Fill in anything missing. If something does not add up — a note that contradicts the "
            + "transcript, a room you are unsure of, a loose end nobody has pulled — say so, in one "
            + "short line, and say what you would do about it.");
        brief.Append(
            "Use `write_memories` for anything worth carrying past this game, and `write_notes` if the "
            + "notebook changed. Both take the whole file, so leave out the one that has not changed. "
            + "If your reply is cut off, neither is written — half a notebook is worse than the one "
            + "you already have.");

        await RespondAsync(brief.ToString(), "reconcile", cancellationToken, wantMovements: turns, allowAct: false)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Is this purpose a maintenance pass — one that neither speaks to the player nor moves?
    /// <para>
    /// Those are the calls that gain nothing from private reasoning, and the ones
    /// <c>thinking.off_for_maintenance</c> applies to (10_delivery.md §8.3 (`D21`) and 05_memory.md §2 (`M11`)). Keeping the list here rather
    /// than in the config means that switch cannot be pointed at a purpose that talks.
    /// </para>
    /// <para>
    /// <c>thinking.off_for</c> — the experiment instrument — *can* name a purpose that talks, and
    /// that is deliberate rather than an oversight: the question it exists to answer is about the
    /// errand step, which narrates itself. What it must not do is go unnamed, so
    /// <see cref="ThinkingFor"/> says in one place which pass is being silenced. Naming a talking
    /// purpose stops the companion thinking out loud on that pass only; the transcript, its notes
    /// and its moves are untouched, and a deployment that wants no reasoning anywhere sets
    /// <c>thinking.type</c> instead.
    /// </para>
    /// </summary>
    private static bool IsMaintenance(string purpose) => purpose is "reconcile" or "catchup";

    /// <summary>
    /// Does this pass owe a call whatever it says? (`C23.8`)
    /// <para>
    /// True for the errand's step and nothing else. Every outcome the step's brief names is a call —
    /// the move (`type`), the key (`press_key`), or the report that ends the errand (`report_back`,
    /// which the brief asks for by name when the budget is spent) — so a step that answers with
    /// prose alone has not answered at all. That is what makes requiring one honest here and
    /// dishonest everywhere else: a reply in front of the player may legitimately be speech and
    /// nothing else (`C16`), and a maintenance pass is writing an artifact rather than taking a turn.
    /// </para>
    /// </summary>
    private static bool AStepMustCallSomething(string purpose) => purpose == "mission-step";

    /// <summary>
    /// Is this the pass that greets somebody who has just arrived (`C16.2`)? Both spellings, because
    /// the purpose is written at the call site and the trigger's own verdict is written without the
    /// hyphen — the same two shapes the role table carries, and for the same reason.
    /// </summary>
    private static bool IsAGreeting(string purpose) => purpose is "player-joined" or "playerjoined";

    /// <summary>
    /// Should this pass think before it answers? <c>null</c> means no opinion — the provider's own
    /// default stands — and it is what every pass gets unless somebody configured otherwise.
    /// <para>
    /// Two ways to be silenced, and they are different in kind. <c>off_for_maintenance</c> is a cost
    /// decision with a fixed list behind it (see <see cref="IsMaintenance"/>). <c>off_for</c> is the
    /// measurable one: a purpose named there loses its private reasoning, and it is how the
    /// 2026-09-25 question is asked — 64 of 199 turns ended with an announced move and no call,
    /// with the longest reasoning on exactly the turns that missed.
    /// </para>
    /// </summary>
    private bool? ThinkingFor(string purpose, bool maintenance)
    {
        var thinking = _config.Thinking;
        if (thinking is null)
        {
            return null;
        }

        if ((maintenance && thinking.OffForMaintenance)
            || thinking.OffFor.Contains(purpose, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    /// <summary>
    /// Is this pass the write-up a returning player asked for (05_memory.md §6 (`M27`–`M36`))?
    /// <para>
    /// Its own purpose rather than a flavour of maintenance, because it is the only pass that is
    /// allowed to call <c>report</c> — a page nobody asked for is money spent on the companion's own
    /// account — and the only one whose output goes to a file instead of to a pane.
    /// </para>
    /// </summary>
    private static bool IsTheWriteUp(string purpose) => purpose == WriteUpPurpose;

    /// <summary>What the introspection pass is called in the log, the events and the configuration.</summary>
    private const string IntrospectPurpose = "introspect";

    /// <summary>
    /// A line from the room (`C71`), named here rather than derived: the purpose is a string other
    /// parts read (`Passes`, the role table, the pass table in the documents) and the enum's own
    /// lowercasing is an implementation detail of the trigger.
    /// </summary>
    private const string OutLoudPurpose = "outloud";

    /// <summary>
    /// What the ask for a report that arrived without words is called (06_tool_surface.md §2.2 (`T13.1`)).
    /// Its own purpose rather than part of the move repair's: the reply has already ended the errand, so
    /// there is nothing to decide and only something to say, and the log can count what wordless reports
    /// cost separately from what a forgotten move costs.
    /// </summary>
    private const string SilentReportPurpose = "silent-report";

    /// <summary>
    /// What the ask for the words beside a call that is not a report is called (06_tool_surface.md §2.2 (`T13.1`)).
    /// <para>
    /// The report's ask above is about an errand ending unannounced. This one is about any other call
    /// that answered the player and said nothing — measured 2026-10-01, an `errand` call on a player
    /// line with <c>speech_chars: 0</c>, whose only trace on the screen was the harness's line about
    /// nothing having come of it. A purpose of its own so the log can count the two separately, which
    /// is the whole reason the first one was separated out.
    /// </para>
    /// </summary>
    private const string SilentCallPurpose = "silent-call";

    /// <summary>
    /// Is this pass a call the loop makes because something else already failed?
    /// <para>
    /// Asked for the sake of <see cref="LookAtWhatDroppedAsync"/>: the harness's account of what
    /// went wrong is for the end of the pass that was trying to do something, and a repair is that
    /// pass's own answer to the same problem. Read from inside one of these and the companion is
    /// asked to reason about a failure before it has finished answering it — and the evening pays
    /// for both calls.
    /// </para>
    /// </summary>
    private static bool IsARepair(string purpose) =>
        purpose is "act-missed" or "act-refused" or "cut-off" or "silent-report" or IntrospectPurpose;

    /// <summary>
    /// Is this the pass in which the companion reads the harness's account of its own trouble?
    /// <para>
    /// Asked by name for the same reason <see cref="IsMaintenance"/> is: three places want the
    /// answer — the tools it is offered, the sentence it is given instead of the generic one, and
    /// the guard that stops it reading its own report — and a fourth spelling of the string would
    /// be the one that disagreed.
    /// </para>
    /// </summary>
    private static bool IsIntrospection(string purpose) => purpose == IntrospectPurpose;

    private static bool IsALineFromTheRoom(string purpose) => purpose == OutLoudPurpose;

    /// <summary>
    /// Is the notebook allowed to change on this pass? The rule is in
    /// <see cref="NotebookPolicy.RewriteIsDue"/> so it can be tested without a loop, a model or a
    /// game; what this does is gather the facts it needs.
    /// </summary>
    private bool NotebookHasPlan()
    {
        var notebook = _notebook.Read();
        var heading = "## Current goals and plan";
        var index = notebook.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var body = notebook[(index + heading.Length)..];
        var end = body.IndexOf("\n## ", StringComparison.Ordinal);
        if (end >= 0)
        {
            body = body[..end];
        }

        body = body.Replace("_(nothing yet)_", string.Empty, StringComparison.Ordinal).Trim();
        return body.Length > 8;
    }

    private static string Sanitise(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "unknown";
        }

        // A stem doubles as a filename, so keep it to characters a filesystem likes.
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.ToString().Trim('-');
    }

    private abstract record Incoming;

    private sealed record EnvelopeEvent(Envelope Value) : Incoming;

    private sealed record TickEvent : Incoming;
}
