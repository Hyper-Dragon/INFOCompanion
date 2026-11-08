using Agent.Core.Config;
using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// A line from the room (04_companion.md §5 (`C71`)): the one reason the companion speaks that the game
/// did not cause, and the one pass that is allowed to want nothing.
/// <para>
/// Measured 2026-10-02: every trigger that spoke was caused by game state (`AgentActed`,
/// `GoingInCircles`, `GoingNowhere`) or by a lead (`IdleNudge` is *"the player has been quiet **and the
/// companion has a lead**"*), so the companion only ever sounded like a person when it needed a
/// decision. Sixteen times as much interiority sat in its reasoning as in its speech — 988,529
/// characters against 61,759 — and none of it could reach the room. The operator's own read of the
/// evening was that it felt locked into the game rather than like somebody in the room.
/// </para>
/// </summary>
public class OutLoudTests
{
    private static SpeakingTriggers Triggers(int seconds) =>
        new(new AgentConfig { OutLoudSeconds = seconds });

    [Fact]
    public void A_line_from_the_room_needs_no_lead_and_no_reason_the_game_caused()
    {
        // The difference from the idle nudge is the whole point of the pass: the nudge answers *"the
        // player has gone quiet **and** the companion has a lead"*, and this answers only *"nothing has
        // happened for a while"*. No plan, no lead and no game state is an input to it.
        var triggers = Triggers(seconds: 300);

        Assert.False(triggers.OnQuietForItsOwnSake(TimeSpan.FromSeconds(50)).Speak);
        Assert.False(triggers.OnQuietForItsOwnSake(TimeSpan.FromSeconds(299)).Speak);

        var due = triggers.OnQuietForItsOwnSake(TimeSpan.FromSeconds(300));
        Assert.True(due.Speak);
        Assert.Equal(SpeakReason.OutLoud, due.Reason);

        // And it spends itself: one line per quiet window of that length rather than one per tick.
        Assert.False(triggers.OnQuietForItsOwnSake(TimeSpan.FromSeconds(305)).Speak);

        // Until the exchange moves, which is any event at all — the same guard the nudge uses, and for
        // the same reason: "do not nag" is a limit on how often it may speak into a silence, not a
        // licence to stop speaking for ever.
        triggers.NoteTheExchangeMoved();
        Assert.True(triggers.OnQuietForItsOwnSake(TimeSpan.FromSeconds(601)).Speak);
    }

    [Fact]
    public void It_is_off_when_the_key_is_zero()
    {
        // The switch a deployment gets: an evening where the player wants no small talk sets it to zero,
        // and the silence is the answer rather than a thing to be worked around.
        var triggers = Triggers(seconds: 0);
        Assert.False(triggers.OnQuietForItsOwnSake(TimeSpan.FromHours(2)).Speak);
    }

    [Fact]
    public void The_surface_it_gets_can_read_and_nothing_else()
    {
        // Enforced by the tool list rather than asked for, which is `C16.2`'s lesson said once: a
        // surface that cannot write the notebook cannot spend the line on it. No move and no write — and
        // deliberately no `ask`, because the one pass allowed to want nothing must not be able to put a
        // question. What is left is what it may look at before it speaks.
        var tools = CompanionTools.ForALineFromTheRoom;

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.True(tool.Annotations.ReadOnly, tool.Name));
        Assert.DoesNotContain(tools, tool => tool.Name == CompanionTools.Ask);
        Assert.DoesNotContain(tools, tool => tool.Name == CompanionTools.Type);
        Assert.DoesNotContain(tools, tool => tool.Name == CompanionTools.PressKey);
        Assert.DoesNotContain(tools, tool => tool.Name == CompanionTools.WriteNotes);
        Assert.DoesNotContain(tools, tool => tool.Name == CompanionTools.UpdateNotes);
        Assert.Contains(tools, tool => tool.Name == CompanionTools.Recall);
    }
}
