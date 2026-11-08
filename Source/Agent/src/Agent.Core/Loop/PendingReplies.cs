namespace Agent.Core.Loop;

/// <summary>
/// Replies that are waiting for a game turn in flight to land.
/// <para>
/// The rule this exists to enforce: **prose waits for the facts, state does not.** A
/// player line that arrives mid-turn still takes effect immediately — a veto cancels, a
/// grant is dispatched, a delegation starts an errand — but the *reply* waits, because
/// answering before the turn completes means answering blind.
/// </para>
/// <para>
/// Measured, and reported from play: the player asked "what did Monica say?" while the
/// companion's own <c>ask monica about…</c> was still running. The model was asked before
/// any output existed, so it truthfully said it had nothing back yet — and the core's
/// between-turns queueing then printed that reply directly underneath Monica's answer.
/// On screen it read as the companion ignoring what was in front of it.
/// </para>
/// </summary>
public sealed class PendingReplies
{
    /// <summary>
    /// Enough to hold a burst of chatter without letting a flood grow the context: the
    /// oldest questions are the ones already losing their thread.
    /// <para>
    /// The value in force is <c>max_held_replies</c> in <c>agent-config.json</c>, set by
    /// <see cref="AgentLoop"/> when it builds this; the default here is the shipped one.
    /// </para>
    /// </summary>
    public int MaxHeld { get; init; } = Config.ConfigDefaults.MaxHeldReplies;

    private readonly List<(string LatestEvent, string Purpose, string? Work)> _held = [];

    /// <summary>
    /// When the first of the replies now waiting arrived, or <c>null</c> when nothing is held. Once
    /// <see cref="MaxHeld"/> has trimmed the list this is older than the oldest reply still waiting,
    /// which errs towards releasing early — the safe direction for a deadline whose failure mode is
    /// a session that never speaks again.
    /// </summary>
    private DateTime? _heldSince;

    public int Count => _held.Count;

    public bool Any => _held.Count > 0;

    /// <summary>
    /// Hold a reply until the turn lands.
    /// </summary>
    /// <param name="work">
    /// What the player actually said, for the work-in-hand rule (<see cref="AgentLoop"/>): an
    /// instruction that produces a move becomes the work in hand, and this is the wording that
    /// was asked for. Kept here rather than re-read later, because by the time the reply goes
    /// out the player may have said something else.
    /// </param>
    public void Hold(string latestEvent, string purpose, string? work = null)
    {
        _heldSince ??= DateTime.UtcNow;
        _held.Add((latestEvent, purpose, work));
        while (_held.Count > MaxHeld)
        {
            _held.RemoveAt(0);
        }
    }

    /// <summary>
    /// Take everything held, oldest first, as one question to answer. Returning them
    /// together matters: several things asked mid-turn usually have one answer once the
    /// result is in.
    /// </summary>
    public (string LatestEvent, string Purpose, string Work)? Drain()
    {
        if (_held.Count == 0)
        {
            return null;
        }

        var purpose = _held.Count == 1 ? _held[0].Purpose : "player-line-batched";
        var text = string.Join("\n", _held.Select(item => item.LatestEvent));

        // Only the lines that carried their own wording contribute: a line that arrived with
        // none has no words of its own to offer, and its event string is the harness describing
        // it, not something the player said.
        var said = _held
            .Select(item => item.Work)
            .Where(work => !string.IsNullOrWhiteSpace(work))
            .ToList();
        var work = said.Count > 0 ? string.Join(" / ", said) : text;

        _held.Clear();
        _heldSince = null;
        return (text, purpose, work);
    }

    public void Clear()
    {
        _held.Clear();
        _heldSince = null;
    }

    /// <summary>
    /// How long the oldest held reply has waited, or <c>null</c> when nothing is held. The clock is
    /// the caller's, so that a test can ask a question about the future instead of sleeping through
    /// it.
    /// </summary>
    public TimeSpan? HeldFor(DateTime now) => _heldSince is { } since ? now - since : null;
}
