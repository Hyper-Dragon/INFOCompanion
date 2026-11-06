namespace Agent.Core.Loop;

/// <summary>
/// What a pass is for, in the one sense a completion ceiling can be set by: whether its reply is
/// something said to somebody during the play, or something composed at length and kept.
/// <para>
/// The two are bounded differently, which is why a single number could never be right for both. A
/// reply is bounded by what a turn sounds like — a sentence or two, or a step's line and its call —
/// and it is spoken or it is not. A write is bounded by the *artifact* it is writing: a whole notebook
/// file, a report page, a note. That is thousands of tokens when the pass has something to say and
/// nothing at all when it does not.
/// </para>
/// <para>
/// Measured on 2026-09-30 (`§25`): a reply averages 366 tokens and the whole of a sitting's completion
/// spend is 2.5% of its tokens. A ceiling is therefore the cheapest thing in the system to be generous
/// with, and what it must not be is a number that has to be raised by hand whenever the context
/// grows — which is what happened, twice.
/// </para>
/// </summary>
public enum PassRole
{
    /// <summary>
    /// Answers the player, answers the game, or takes a step. Bounded by what a turn sounds like.
    /// </summary>
    Reply,

    /// <summary>
    /// Composes prose and keeps it: a notebook file, a report page, a note. Bounded by the artifact.
    /// </summary>
    Write,
}

/// <summary>
/// Every pass the loop runs, and the role each one has.
/// <para>
/// One place, so a ceiling can be derived from what a pass *is* rather than from a table edited by
/// hand. A purpose that is not named here has **no** role — <see cref="RoleFor"/> returns null rather
/// than guessing — and a pass with no role gets the global ceiling, which is the fallback
/// `max_tokens` is now for (`C23.6`).
/// </para>
/// <para>
/// The shares reproduce the shape the hand-written numbers already had: 16,000 against a 65,536 window
/// is a quarter, and the write-up's 32,000 is a half. That is where the two numbers come from, and
/// they are stated against the window rather than as totals so that a bigger model follows without an
/// edit.
/// </para>
/// </summary>
public static class Passes
{
    /// <summary>The role name `max_tokens_share_by_role` is keyed by (`D11`).</summary>
    public const string ReplyName = "reply";

    /// <summary>The role name `max_tokens_share_by_role` is keyed by (`D11`).</summary>
    public const string WriteName = "write";

    private static readonly Dictionary<string, PassRole> RolesByName = new(StringComparer.Ordinal)
    {
        // The passes that answer somebody, or take a step: bounded by what a turn sounds like.
        ["player-line"] = PassRole.Reply,
        ["player-line-batched"] = PassRole.Reply,
        ["game-prompt"] = PassRole.Reply,
        ["mission-step"] = PassRole.Reply,
        ["act-missed"] = PassRole.Reply,
        ["act-refused"] = PassRole.Reply,
        ["cut-off"] = PassRole.Reply,
        // Its sibling in shape but not in role: `silent-report` answers nobody (the errand is over and
        // only the words are missing), while this one asks for the words *and* leaves the move open,
        // because the call that carried no words may have been the beginning of one. A turn's worth of
        // length is therefore the right bound rather than an artifact's.
        ["silent-call"] = PassRole.Reply,
        ["suggestion"] = PassRole.Reply,
        ["handover"] = PassRole.Reply,

        // The unprompted passes, which the triggers name after themselves (`SpeakReason`, lowercased
        // by the loop): the companion's own line, spoken of its own accord, and never longer than a
        // turn's worth of it. `GoingInCircles` is the one that may move, and that is a permission on
        // the pass rather than a licence to write at length.
        //
        // The names come in two shapes, and that is the loop's own history rather than a rule: a verdict
        // that becomes a purpose by `SpeakReason.ToString().ToLowerInvariant()` is unhyphenated
        // (`agentacted`, `goingincircles`, `goingnowhere`), and a pass that names itself at its call site
        // is hyphenated (`player-joined`, `idle-nudge`). `decisionpoint` and `novelty` are verdicts no
        // trigger returns today and are kept for the same direction of safety: were one to start firing,
        // it would be named this way, and the gate's check reads the call sites rather than this table.
        ["agentacted"] = PassRole.Reply,
        ["decisionpoint"] = PassRole.Reply,
        ["novelty"] = PassRole.Reply,
        ["goingincircles"] = PassRole.Reply,
        ["goingnowhere"] = PassRole.Reply,
        ["idlenudge"] = PassRole.Reply,
        ["playerjoined"] = PassRole.Reply,
        ["player-joined"] = PassRole.Reply,
        ["idle-nudge"] = PassRole.Reply,

        // The other named passes, which answer a turn somebody caused: the game waiting for a key
        // rather than a line, the core's own stuck verdict, and the line the core refused to write.
        ["refused-line"] = PassRole.Reply,
        ["stuck"] = PassRole.Reply,

        // A line from the room (`C71`): the only pass that answers nobody and is not housekeeping —
        // it says something to the person next to the companion because they are both there. A reply
        // rather than a write, because it is one sentence to somebody and not a document.
        ["outloud"] = PassRole.Reply,
        // The player refusing a risky act the companion proposed: the pass answers them in words, which
        // is why it is a reply rather than a write. Registered late (2026-10-02): measured in that
        // sitting's bundle, `veto` was the one purpose in play with no entry here, so its five calls
        // went out on the global `max_tokens` fallback rather than the reply share — the seam section
        // 25 predicted, found by reading a bundle rather than by a test.
        ["veto"] = PassRole.Reply,

        // The passes that compose something and keep it: bounded by the artifact, not by a turn.
        ["report"] = PassRole.Write,
        ["reconcile"] = PassRole.Write,
        ["introspect"] = PassRole.Write,
        ["silent-report"] = PassRole.Write,
        ["catchup"] = PassRole.Write,

        // The errand's own report after an act the harness could not make — *"tell the player plainly
        // what you tried and what you found, in your own words"* — which is a report and reads like
        // one. Measured 2026-10-01, the one live sitting where a pass sat on the global fallback and
        // it mattered: 16,000 against the 32,768 a write is entitled to.
        ["mission-report"] = PassRole.Write,
    };

    /// <summary>Every purpose this table knows, for the tests and for the gate's floor (`C23.7`).</summary>
    public static IReadOnlyCollection<string> All => RolesByName.Keys;

    /// <summary>
    /// The role a pass has, or null for a purpose nobody has named — which is a pass added without one,
    /// and the global ceiling is the honest answer for it rather than a guess at which of the two roles
    /// it would have had.
    /// <para>
    /// It is deliberately strict: a misspelled purpose returns null, and the gate checks both that every
    /// name here is a pass the loop runs and that every shipped exception names a purpose that is here.
    /// </para>
    /// </summary>
    public static PassRole? RoleFor(string purpose) =>
        RolesByName.TryGetValue(purpose, out var role) ? role : null;

    /// <summary>Whether this is a pass the table knows.</summary>
    public static bool Knows(string purpose) => RolesByName.ContainsKey(purpose);

    /// <summary>The name a role is configured under, so the number and the enum cannot drift.</summary>
    public static string NameOf(PassRole role) => role switch
    {
        PassRole.Write => WriteName,
        _ => ReplyName,
    };
}
