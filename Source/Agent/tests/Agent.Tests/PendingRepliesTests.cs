using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// Prose waits for the facts; state does not (§iteration 01 §5).
/// <para>
/// Reported from play: the player asked "what did Monica say?" while the companion's own
/// <c>ask monica about…</c> was still running. Answered immediately, the only honest reply
/// was "I have nothing back yet" — and the core's between-turns queueing then printed that
/// reply directly underneath Monica's answer, so the companion looked as though it had
/// ignored what was on screen.
/// </para>
/// </summary>
public class PendingRepliesTests
{
    [Fact]
    public void Nothing_is_held_to_begin_with()
    {
        var replies = new PendingReplies();

        Assert.False(replies.Any);
        Assert.Equal(0, replies.Count);
        Assert.Null(replies.Drain());
    }

    [Fact]
    public void A_held_reply_comes_back_when_the_turn_lands()
    {
        var replies = new PendingReplies();
        replies.Hold("player said: what did Monica say?", "player-line");

        Assert.True(replies.Any);
        Assert.Equal(1, replies.Count);

        var drained = replies.Drain();

        Assert.NotNull(drained);
        Assert.Equal("player said: what did Monica say?", drained!.Value.LatestEvent);
        Assert.Equal("player-line", drained.Value.Purpose);
        // Draining empties it: the question is answered once.
        Assert.False(replies.Any);
        Assert.Null(replies.Drain());
    }

    [Fact]
    public void The_work_kept_with_a_held_reply_is_the_players_own_words()
    {
        // An instruction that earns a move becomes the work in hand, and what starts it has to
        // be what was actually asked for. It is kept at the moment the line arrives, because by
        // the time the turn lands the player may well have said something else.
        var batch = new PendingReplies();
        batch.Hold("player said: try the post office", "player-line", "try the post office");
        batch.Hold("player said: are you there?", "player-line", "are you there?");

        Assert.Equal("try the post office / are you there?", batch.Drain()!.Value.Work);

        // A line that arrived carrying no wording of its own contributes none: its event string
        // is the harness describing it, not something the player said.
        var one = new PendingReplies();
        one.Hold("player has joined", "player-joined");

        Assert.Equal("player has joined", one.Drain()!.Value.Work);
    }

    [Fact]
    public void Several_things_asked_mid_turn_are_answered_together()
    {
        // Two questions asked while one command ran usually have one answer once the
        // result is in, and answering them separately would cost two model calls to say
        // the same thing.
        var replies = new PendingReplies();
        replies.Hold("player said: what did Monica say?", "player-line");
        replies.Hold("player suggests: we should ask about the will", "suggestion");

        var drained = replies.Drain();

        Assert.NotNull(drained);
        Assert.Contains("what did Monica say", drained!.Value.LatestEvent);
        Assert.Contains("ask about the will", drained.Value.LatestEvent);
        Assert.Equal("player-line-batched", drained.Value.Purpose);
    }

    [Fact]
    public void A_flood_is_bounded_and_keeps_the_newest()
    {
        // Context is a budget, and the oldest questions are the ones already losing their
        // thread; dropping them is better than letting a burst grow the prompt.
        var replies = new PendingReplies();
        for (var i = 1; i <= 10; i++)
        {
            // Bracketed so one label can never be a substring of another: "question 1"
            // would match inside "question 10".
            replies.Hold($"question [{i}]", "player-line");
        }

        Assert.Equal(4, replies.Count);

        var drained = replies.Drain();
        Assert.NotNull(drained);
        Assert.DoesNotContain("question [1]", drained!.Value.LatestEvent);
        Assert.DoesNotContain("question [6]", drained.Value.LatestEvent);
        Assert.Contains("question [7]", drained.Value.LatestEvent);
        Assert.Contains("question [10]", drained.Value.LatestEvent);
    }

    [Fact]
    public void A_reset_or_a_game_change_throws_held_replies_away()
    {
        // They were questions about a run that no longer exists.
        var replies = new PendingReplies();
        replies.Hold("player said: where did Phong go?", "player-line");

        replies.Clear();

        Assert.False(replies.Any);
    }

    [Fact]
    public void The_wait_is_measured_from_the_first_hold_and_starts_again_when_the_replies_go()
    {
        // The deadline over a turn that never lands. Measured 2026-09-24: one line the core refused
        // left a live session silent — the replies held behind it were held for ever, no model call
        // was made again, and nothing on the screen said so. The deadline is measured from the
        // *oldest* held reply, because the oldest is the one already losing its thread.
        //
        // The clock belongs to the caller rather than to the class, so the questions below are asked
        // about a time in the future instead of by sleeping through the wait being measured.
        var replies = new PendingReplies();

        Assert.Null(replies.HeldFor(DateTime.UtcNow));

        replies.Hold("player said: are you there?", "player-line");

        // Two holds with one turn of the wall clock between them: the claim is that a second hold
        // does not restart the wait, and two stamps either side of a clock tick are the smallest
        // honest way to tell "from the first" apart from "from the newest" — the two differ only by
        // that gap, so the gap is what the assertion has to be built on.
        var gap = WaitForTheClockToMove();
        replies.Hold("player suggests: we should ask about the will", "suggestion");
        Assert.Equal(2, replies.Count);

        var inTenMinutes = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        var waited = replies.HeldFor(inTenMinutes);

        Assert.NotNull(waited);
        Assert.True(
            waited.Value - TimeSpan.FromMinutes(10) >= gap / 2,
            $"the wait runs from the first hold, not the newest: waited {waited}, gap {gap}");

        // Answered once, and the clock starts again with the next line rather than standing: a clock
        // that was not reset here would release a fresh reply the moment it arrived.
        replies.Drain();
        Assert.Null(replies.HeldFor(DateTime.UtcNow));

        replies.Hold("player said: where did Phong go?", "player-line");
        Assert.NotNull(replies.HeldFor(DateTime.UtcNow));

        // And a reset or a game change throws the held replies away, the wait with them.
        replies.Clear();
        Assert.Null(replies.HeldFor(DateTime.UtcNow));
    }

    /// <summary>
    /// Wait for the wall clock to tick over once, so that two stamps taken either side of it differ.
    /// Bounded, and a wait for the clock's own resolution rather than for a duration a test measures.
    /// </summary>
    private static TimeSpan WaitForTheClockToMove()
    {
        var start = DateTime.UtcNow;
        var moved = SpinWait.SpinUntil(() => DateTime.UtcNow > start, TimeSpan.FromSeconds(2));

        Assert.True(moved, "the clock did not move, so two holds cannot be told apart");
        return DateTime.UtcNow - start;
    }
}
