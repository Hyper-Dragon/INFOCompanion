using Agent.Core.Config;

namespace Agent.Core.Loop;

/// <summary>Why the companion decided to speak (§10.6).</summary>
public enum SpeakReason
{
    None,

    /// <summary>The companion acted and is reporting back (§10.3 steer).</summary>
    AgentActed,

    /// <summary>A blocked path, an NPC, a choice: the most important trigger.</summary>
    DecisionPoint,

    /// <summary>A new room, something found, a score change.</summary>
    Novelty,

    /// <summary>The same action keeps failing: surface it instead of grinding.</summary>
    GoingInCircles,

    /// <summary>A stretch of play that has found nothing new: the macro form of the same rule (`C39.4`).</summary>
    GoingNowhere,

    /// <summary>The player has been quiet and the companion has a lead.</summary>
    IdleNudge,

    /// <summary>The player just sat down.</summary>
    PlayerJoined,

    /// <summary>
    /// Nothing has happened for a long time and the companion has something of its own to say — not
    /// about the game (04_companion.md §5 (`C71`)). The one reason to speak here that is not caused by
    /// the game, and the only pass that exists for company rather than for work.
    /// </summary>
    OutLoud,
}

/// <summary>The trigger's verdict, with the evidence that produced it.</summary>
public sealed record TriggerDecision(bool Speak, SpeakReason Reason, string Detail)
{
    public static TriggerDecision No(string detail) => new(false, SpeakReason.None, detail);
}

/// <summary>
/// Speaking triggers and cadence (§10.6).
/// <para>
/// Some triggers are prompt-level (the decision-point consult is the <c>[ask]</c>
/// output contract) and some are code-level; this class is the code-level half,
/// which is exactly the split the spec asks to be documented in the companion's rules.
/// </para>
/// <para>
/// The bias throughout is towards silence: one genuine observation beats three
/// fillers, so novelty needs a cooldown and an idle nudge happens once and then
/// waits.
/// </para>
/// </summary>
public sealed class SpeakingTriggers
{
    private readonly AgentConfig _config;
    private int _turnsSinceUtterance;

    /// <summary>
    /// How long the player had been quiet when the last nudge went out, so the next one can be
    /// paced — or null if none has gone out since they last spoke.
    /// <para>
    /// This replaced a bool that was cleared only by the player typing (<c>NotePlayerSpoke</c>),
    /// which is the bug that stopped every unattended run: one nudge, then no trigger could
    /// ever fire again for the life of the session, because nobody was ever going to type. Any
    /// turn where the companion did not act then stopped the game for good. Measured three
    /// times, the last of them on a clean container at turn 23, after a reply of untagged prose
    /// that neither acted nor asked.
    /// </para>
    /// <para>
    /// "Do not nag" is a limit on how often the companion may speak into a silence, not a
    /// licence to stop speaking for ever. The quiet clock is compared against the reading it had
    /// at the last nudge, so the pause between nudges is <c>idle_nudge_seconds</c> and does not
    /// depend on the player having said anything.
    /// </para>
    /// </summary>
    private double? _nudgedAtIdle;

    /// <summary>
    /// How quiet it was when the companion last said something for its own sake, so the next one can
    /// be paced — or null if the exchange has moved since. The same shape as
    /// <see cref="_nudgedAtIdle"/> and for the same reason: "do not nag" is a limit on how often the
    /// companion may speak into a silence, not a licence to stop speaking for ever.
    /// </summary>
    private double? _spokeForItsOwnSakeAt;

    private bool _greeted;

    public SpeakingTriggers(AgentConfig config)
    {
        _config = config;
        _turnsSinceUtterance = config.UnpromptedCooldownTurns;
    }

    /// <summary>The companion spoke (any reason), so the cadence window restarts.</summary>
    public void NoteSpoke() => _turnsSinceUtterance = 0;

    /// <summary>
    /// Anything either of them did, so the next quiet window starts from here (04_companion.md §5 (`C29`–`C41`)).
    /// <para>
    /// <see cref="OnIdle"/> guards against nagging by remembering how quiet it was at the last
    /// nudge, and that guard is measured against the quiet clock — which now restarts whenever
    /// anybody does anything. Without this the guard would count against a clock that had gone
    /// back to zero, and the second nudge of an evening could never arrive: measured at 50s of
    /// quiet, the companion speaks, the clock restarts, and the arithmetic compares the new 50
    /// with the old 50 and reads "nudged recently" for ever.
    /// </para>
    /// </summary>
    public void NoteTheExchangeMoved()
    {
        _nudgedAtIdle = null;
        _spokeForItsOwnSakeAt = null;
    }

    /// <summary>A turn went by without the companion saying anything.</summary>
    public void NoteTurn() => _turnsSinceUtterance++;

    /// <summary>The player said something, so an idle nudge starts over.</summary>
    public void NotePlayerSpoke() => _nudgedAtIdle = null;

    /// <summary>Was the player greeted already this session?</summary>
    public bool Greeted
    {
        get => _greeted;
        set => _greeted = value;
    }

    /// <summary>
    /// Should the companion speak unprompted after this turn?
    /// </summary>
    /// <param name="output">What the game printed.</param>
    /// <param name="agentAuthored">Did the companion itself cause this turn?</param>
    public TriggerDecision OnTurn(string output, bool agentAuthored)
    {
        _ = output;

        // The companion acted: it always reports back rather than leaving the
        // player to infer what just happened (§10.3 steer).
        if (agentAuthored)
        {
            return new TriggerDecision(true, SpeakReason.AgentActed, "the companion acted");
        }

        // Four regular expressions used to live here, matching the words an English story
        // prints — "you are standing in", "taken.", "can't go that way", "is locked", and a
        // bare quotation mark. Measured over fifty-seven turns of live play across four
        // sessions, they fired **twice**. The reason is in this class: the companion reports
        // after every move it makes, so by the time a room description or a newly taken object
        // arrives, `_turnsSinceUtterance` is inside the cooldown and the trigger is gated out.
        //
        // They were also, precisely, the thing this project bans. Wording a *story* prints is a
        // fact about one language's games, and this component has to play any story in any
        // language — the same mistake `is_movement` was deleted for, wearing a bigger regex.
        // They cost nothing to remove because they were earning nothing, and "the companion
        // decides from the turn it was given" is both simpler and true in Spanish.
        return TriggerDecision.No(
            $"nothing in this turn is worth interrupting for ({_turnsSinceUtterance} turns since it spoke)");
    }

    /// <summary>A consult because the companion is stuck on the same action.</summary>
    public TriggerDecision GoingInCircles(string action, int count) =>
        new(true, SpeakReason.GoingInCircles, $"`{action}` has been tried {count} times without progress");

    /// <summary>
    /// A consult because a stretch of play has found nothing new (04_companion.md `C39.4`).
    /// <para>
    /// The macro form of <see cref="GoingInCircles"/>: a repeat is one action to stop, and a stretch
    /// is a session to reconsider, so the words differ and the shape does not. The <paramref name="fact"/>.
    /// is the record's own sentence — passed in rather than rebuilt, so the prompt and the consult
    /// cannot say different things about the same stretch.
    /// </para>
    /// </summary>
    public TriggerDecision GoingNowhere(string fact) => new(true, SpeakReason.GoingNowhere, fact);

    /// <summary>
    /// The idle nudge: one short line, and only when the companion actually has a
    /// lead (a plan in the notebook). No nagging (§10.6) — but never silence for ever
    /// either, or the game is left with nobody driving it.
    /// </summary>
    public TriggerDecision OnIdle(TimeSpan idleFor, bool hasLead)
    {
        // One nudge per quiet period of this length, measured against the clock at the last one —
        // and the clock is the exchange's, which restarts on any event at all (04_companion.md §5 (`C29`–`C41`)). What this guard
        // still catches is the case the clock cannot: a nudge that produced no words, which is a
        // nudge that moved nothing and should not be repeated five seconds later.
        var quietSeconds = idleFor.TotalSeconds;
        if (_nudgedAtIdle is not null
            && quietSeconds - _nudgedAtIdle.Value < _config.IdleNudgeSeconds)
        {
            return TriggerDecision.No("nudged recently");
        }

        if (quietSeconds < _config.IdleNudgeSeconds)
        {
            return TriggerDecision.No("the player is not idle yet");
        }

        if (!hasLead)
        {
            return TriggerDecision.No("no lead worth offering");
        }

        _nudgedAtIdle = quietSeconds;
        return new TriggerDecision(true, SpeakReason.IdleNudge, $"the player has been quiet for {quietSeconds:F0}s");
    }

    /// <summary>A greeting when the player sits down, once per session.</summary>
    public TriggerDecision OnPlayerJoined()
    {
        if (_greeted)
        {
            return TriggerDecision.No("already greeted");
        }

        _greeted = true;
        return new TriggerDecision(true, SpeakReason.PlayerJoined, "the player connected");
    }

    /// <summary>
    /// A line from the room, for its own sake (04_companion.md §5 (`C71`)) — the one reason to speak that
    /// is not about the game.
    /// <para>
    /// Deliberately rarer than the idle nudge and deliberately **not** gated on having a lead: the
    /// nudge exists to get the game moving again, and this exists because two people at one machine
    /// say things to each other. It also spends itself rather than repeating — one line per quiet
    /// window of this length, measured against the exchange's clock, which any event at all restarts.
    /// </para>
    /// </summary>
    public TriggerDecision OnQuietForItsOwnSake(TimeSpan idleFor)
    {
        var seconds = _config.OutLoudSeconds;
        if (seconds <= 0)
        {
            return TriggerDecision.No("the room's own line is switched off");
        }

        var quietSeconds = idleFor.TotalSeconds;
        if (_spokeForItsOwnSakeAt is not null
            && quietSeconds - _spokeForItsOwnSakeAt.Value < seconds)
        {
            return TriggerDecision.No("it has said something of its own recently");
        }

        if (quietSeconds < seconds)
        {
            return TriggerDecision.No($"quiet for {quietSeconds:F0}s of {seconds}s");
        }

        _spokeForItsOwnSakeAt = quietSeconds;
        return new TriggerDecision(
            true,
            SpeakReason.OutLoud,
            $"nothing has happened for {quietSeconds:F0}s");
    }
}
