namespace Agent.Tests;

using Agent.Core.Loop;

/// <summary>
/// When the notebook may change (see <see cref="NotebookPolicy"/>). The price of a rewrite is a
/// fresh prompt, so this is about timing rather than about the notes.
/// </summary>
public class NotebookPolicyTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_rewrite_of_a_session_is_allowed()
    {
        Assert.True(NotebookPolicy.RewriteIsDue(
            maintenance: false,
            overBudget: false,
            lastRewrite: DateTimeOffset.MinValue,
            now: Noon,
            minSeconds: 90));
    }

    [Fact]
    public void A_two_token_prune_twenty_two_seconds_later_is_not_worth_a_fresh_prompt()
    {
        // The live run of 2026-09-15: 487 tokens fell to 485, twenty-two seconds after the previous
        // rewrite, and bought the whole prompt again. The rule that admitted it — "shorter notes are
        // always welcome" — has no argument left to be passed here, which is the whole of the fix.
        Assert.False(NotebookPolicy.RewriteIsDue(
            maintenance: false,
            overBudget: false,
            lastRewrite: Noon,
            now: Noon.AddSeconds(22),
            minSeconds: 90));
    }

    [Fact]
    public void A_rewrite_after_the_interval_is_allowed()
    {
        Assert.True(NotebookPolicy.RewriteIsDue(
            maintenance: false,
            overBudget: false,
            lastRewrite: Noon,
            now: Noon.AddSeconds(90),
            minSeconds: 90));
    }

    [Fact]
    public void Taking_stock_may_write_at_any_time()
    {
        // Reconcile is the pass whose whole job is to write down what the evening has come to, so
        // the seam is where the notebook is *supposed* to change (see `IsMaintenance`).
        Assert.True(NotebookPolicy.RewriteIsDue(
            maintenance: true,
            overBudget: false,
            lastRewrite: Noon,
            now: Noon,
            minSeconds: 90));
    }

    [Fact]
    public void A_notebook_over_its_budget_may_write_at_any_time()
    {
        // The bloat is on every call already, so waiting would be paying twice for it.
        Assert.True(NotebookPolicy.RewriteIsDue(
            maintenance: false,
            overBudget: true,
            lastRewrite: Noon,
            now: Noon,
            minSeconds: 90));
    }
}
