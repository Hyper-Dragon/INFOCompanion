namespace Agent.Core.Loop;

/// <summary>
/// May the notebook change on this pass? (2026-09-15)
/// <para>
/// The notebook sits at the front of the prompt, so every rewrite invalidates the cached tokens
/// behind it. Measured over one evening: 24 rewrites, and the call that followed each one paid
/// 8,530 fresh prompt tokens against a session mean of 2,587 — in bursts of three or four inside a
/// minute, each burst buying the same prompt again.
/// </para>
/// <para>
/// So the question is not "are these better notes" — the companion is entitled to think so. It is
/// "is this the moment to pay for them". Two reasons say yes on their own: taking stock, which is
/// the pass whose whole job is to write down what the evening has come to, and a notebook over its
/// budget, where the bloat is being paid for on every call already.
/// </para>
/// <para>
/// Note what is deliberately *not* a reason: the notes being shorter. That was the first version of
/// this rule, and the live run of 2026-09-15 shows why it fails — 487 tokens fell to 485, twenty-two
/// seconds after the previous rewrite, and a two-token prune bought a fresh prompt. Pruning inside
/// the budget is churn, and a rewrite that shrinks the notebook busts the same cache as one that
/// grows it. The exception sounded like thrift and was not.
/// </para>
/// </summary>
public static class NotebookPolicy
{
    /// <summary>
    /// Is a rewrite due? A rewrite is *not* weighed against the notes it would replace: the
    /// direction of the edit is the companion's judgement, and the only question here is the price
    /// of changing the prompt.
    /// </summary>
    public static bool RewriteIsDue(
        bool maintenance,
        bool overBudget,
        DateTimeOffset lastRewrite,
        DateTimeOffset now,
        int minSeconds)
    {
        // Taking stock is a seam by definition (see `IsMaintenance`).
        if (maintenance)
        {
            return true;
        }

        // Over budget is a reason that outranks the clock: the bloat is already on every call.
        if (overBudget)
        {
            return true;
        }

        // Otherwise the interval decides — and the first rewrite of a session is always due,
        // because `lastRewrite` starts at `MinValue`.
        return now - lastRewrite >= TimeSpan.FromSeconds(minSeconds);
    }
}
