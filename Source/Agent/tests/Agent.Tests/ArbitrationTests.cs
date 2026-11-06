using Agent.Core.Config;
using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// The arbitration state machine (§10.4) and the speaking triggers (§10.6). These are
/// the rules that make the companion a good co-player rather than a bot, so they are
/// tested as behaviour rather than as fields.
/// </summary>
public class ArbitrationTests
{
    private static ArbitrationState NewState() => new()
    {
        VetoCooldown = TimeSpan.FromSeconds(90),
        CirclesThreshold = 2,
    };

    // -- quiet is a property of the exchange, not of the player (04_companion.md §5 (`C29`–`C41`)) --------------------------

    [Fact]
    public void Quiet_is_measured_from_the_last_thing_either_of_them_did()
    {
        // Asked by the requester: "why do we need wallclock time?" The only clock was the
        // player's last line, so the companion's own words, its own question and a turn the game
        // had just handed over all read as silence — 103 firings and one call on the measured
        // 09-17 evening (04_companion.md §5 (`C29`–`C41`)).
        var state = NewState();
        Thread.Sleep(60);
        Assert.True(
            state.QuietFor >= TimeSpan.FromMilliseconds(50),
            "quiet grows while nothing happens");

        state.NoteTheExchangeMoved("its own words");
        Assert.True(state.QuietFor < TimeSpan.FromMilliseconds(50), "its own words are not silence");
        Assert.Equal("its own words", state.LastEventWas);

        state.NoteTurnCompleted();
        Assert.Equal("the game's answer", state.LastEventWas);

        state.NotePlayerLine("hello", grantedOrVetoed: false);
        Assert.Equal("the player's line", state.LastEventWas);

        state.NoteInputSent(byAgent: true);
        Assert.Equal("the companion's own command", state.LastEventWas);

        state.NotePlayerTyping();
        Assert.Equal("the player typing", state.LastEventWas);
    }

    [Fact]
    public void A_question_of_its_own_is_an_event_and_not_silence()
    {
        // Issue #23's mechanism, stated at the clock. Before this, asking a question left the
        // quiet clock counting from the player's last line, so fifty seconds later the companion
        // spoke over the question it had just asked.
        var state = NewState();
        Thread.Sleep(60);

        state.NoteAsked("the trail, or the shore?");

        Assert.True(state.QuietFor < TimeSpan.FromMilliseconds(50));
        Assert.Equal("the companion's own question", state.LastEventWas);
    }

    [Fact]
    public void A_boot_and_a_new_game_move_the_clock_too()
    {
        // A boot that came back to a position was handed a conversation, and a switched game is
        // not fifty seconds of silence either — both are the room having just moved.
        var state = NewState();
        Thread.Sleep(60);
        state.NoteTheExchangeMoved("the conversation it was handed at boot");
        Assert.True(state.QuietFor < TimeSpan.FromMilliseconds(50));

        Thread.Sleep(60);
        state.ResetForNewGame();
        Assert.Equal("a new game", state.LastEventWas);
        Assert.True(state.QuietFor < TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void A_nudge_guard_follows_the_clock_it_is_measured_against()
    {
        // The guard remembers the quiet seconds at the last nudge, and the clock it is measured
        // against now restarts on every event. Left alone, the arithmetic compared a fresh 50 with
        // an old 50 and read "nudged recently" for ever, so the second nudge of an evening could
        // never arrive — the sort of breakage that looks like a quiet companion rather than a bug.
        var triggers = new SpeakingTriggers(new AgentConfig());
        var quiet = TimeSpan.FromSeconds(60);

        Assert.True(triggers.OnIdle(quiet, hasLead: true).Speak);
        Assert.False(triggers.OnIdle(quiet, hasLead: true).Speak);

        // Something happened — the companion spoke, the game answered, the player typed.
        triggers.NoteTheExchangeMoved();

        Assert.True(triggers.OnIdle(quiet, hasLead: true).Speak);
    }

    // -- the player holding the companion with a leading space (03_session.md §6 (`S41`–`S43`)) ---------------------------

    [Fact]
    public void A_space_on_the_front_of_the_line_is_a_hold_and_taking_it_off_releases_it()
    {
        // Nothing is killed and nothing is cancelled: what waits is the next turn the companion
        // would have taken, which is the difference between a pause and a stop.
        var state = NewState();
        Thread.Sleep(60);

        state.NoteHeld(true);
        Assert.True(state.IsHeld);
        Assert.Equal("the player holding the companion", state.LastEventWas);
        Assert.True(
            state.QuietFor < TimeSpan.FromMilliseconds(50),
            "holding is the player doing something, so the room is not quiet");

        state.NoteHeld(false);
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void A_new_game_is_not_held()
    {
        // A game switch must not leave the companion waiting on a line nobody is typing.
        var state = NewState();
        state.NoteHeld(true);
        state.ResetForNewGame();
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void A_question_puts_the_ball_in_the_players_court()
    {
        // Issue #23. Nothing recorded that an [ask] had gone out, so the idle clock carried
        // on counting from the player's last line and the companion spoke over its own
        // question about fifty seconds later — twice in one measured session.
        var state = NewState();
        state.NoteAsked("Take the trail up, or keep to the shore?");

        Assert.True(state.IsAwaitingAnswer);
        Assert.Equal(CompanionState.WaitingForAnswer, state.State);
        Assert.Equal("Take the trail up, or keep to the shore?", state.AwaitingAnswer);
    }

    [Fact]
    public void The_players_line_answers_the_question()
    {
        var state = NewState();
        state.NoteAsked("which way?");

        state.NotePlayerLine("let's try the stream", grantedOrVetoed: false);

        Assert.False(state.IsAwaitingAnswer);
    }

    [Fact]
    public void Doing_something_means_it_is_no_longer_waiting()
    {
        // A reply with both an [ask] and an [act] has acted: the question was rhetorical
        // courtesy, not the whole of what it did, so it must not freeze itself.
        var state = NewState();
        state.NoteAsked("going up, unless you say otherwise");
        state.ClearAnswer();

        Assert.False(state.IsAwaitingAnswer);
    }

    [Fact]
    public void A_new_game_does_not_leave_a_question_hanging()
    {
        var state = NewState();
        state.NoteAsked("which way?");

        state.ResetForNewGame();

        Assert.False(state.IsAwaitingAnswer);
    }

    [Fact]
    public void A_question_records_when_it_went_out_so_the_wait_can_be_bounded()
    {
        // Issue #23, second half. "It asked, so it waits" was implemented as "waits for ever",
        // and on an unattended container that meant the game stopped advancing at turn six and
        // stayed stopped: the companion had asked the empty room a question. Waiting needs a
        // start time before it can have a limit.
        var state = NewState();
        var before = DateTimeOffset.UtcNow;

        state.NoteAsked("Follow the gully down, or try the road instead?");

        Assert.NotNull(state.AwaitingAnswerSince);
        Assert.InRange(state.AwaitingAnswerSince!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void A_proposal_records_when_it_went_out_too()
    {
        // The same patience, on the other gate. Measured: it proposed `light lamp` at turn 18
        // and the run stopped there for ever, because a grant needs a player and there was none.
        var state = NewState();

        state.NoteProposal(new PendingProposal("p1", "light lamp", "it is dark", "medium", DateTimeOffset.UtcNow));

        Assert.NotNull(state.ProposalSince);
    }

    [Fact]
    public void Acting_on_a_question_does_not_wipe_the_proposals_clock()
    {
        // The bug that cost this iteration a whole run: `ClearAnswer` is called whenever the
        // companion acted, and proposing *is* acting. With one shared clock it erased the start
        // time of the proposal it had just made, so the grant wait read "elapsed: zero" on every
        // tick, the give-up could never fire, and the log promised "giving up in 90 seconds" for
        // ever. Two waits, two clocks.
        var state = NewState();
        state.NoteProposal(new PendingProposal("p1", "light lamp", "it is dark", "medium", DateTimeOffset.UtcNow));

        // Exactly what the loop does after a reply that both proposed and cleared the answer.
        state.ClearAnswer();

        Assert.NotNull(state.ProposalSince);
        Assert.True(state.IsAwaitingGrant);
    }

    [Fact]
    public void Settling_the_question_clears_its_own_clock()
    {
        // Otherwise a later question inherits the old timestamp and expires on arrival.
        var state = NewState();
        state.NoteAsked("which way?");

        state.ClearAnswer();

        Assert.Null(state.AwaitingAnswerSince);
    }

    [Fact]
    public void Granting_or_refusing_a_proposal_stops_its_clock()
    {
        // Both halves matter: a stale clock would make the next proposal expire the instant it
        // was made, which is the same bug in the opposite direction.
        var granted = NewState();
        granted.NoteProposal(new PendingProposal("p1", "light lamp", "dark", "medium", DateTimeOffset.UtcNow));
        granted.Approve();
        Assert.Null(granted.ProposalSince);

        var vetoed = NewState();
        vetoed.NoteProposal(new PendingProposal("p1", "light lamp", "dark", "medium", DateTimeOffset.UtcNow));
        vetoed.Veto(DateTimeOffset.UtcNow);
        Assert.Null(vetoed.ProposalSince);
    }

    [Fact]
    public void The_players_line_stops_both_clocks()
    {
        var answering = NewState();
        answering.NoteAsked("which way?");
        answering.NotePlayerLine("left", grantedOrVetoed: false);
        Assert.Null(answering.AwaitingAnswerSince);

        var proposing = NewState();
        proposing.NoteProposal(new PendingProposal("p1", "light lamp", "dark", "medium", DateTimeOffset.UtcNow));
        proposing.NotePlayerLine("don't", grantedOrVetoed: true);
        Assert.Null(proposing.ProposalSince);
    }

    [Fact]
    public void A_proposal_waits_for_the_player()
    {
        var state = NewState();
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "he wants treasure", "medium", DateTimeOffset.UtcNow));

        Assert.Equal(CompanionState.AwaitingGrant, state.State);
        Assert.True(state.IsAwaitingGrant);
    }

    [Fact]
    public void A_grant_clears_the_pending_proposal_once()
    {
        var state = NewState();
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "why not", "medium", DateTimeOffset.UtcNow));

        Assert.Equal("bribe troll", state.Approve());
        Assert.False(state.IsAwaitingGrant);

        // Nothing is pending any more, so there is nothing to approve.
        Assert.Null(state.Approve());
    }

    [Fact]
    public void A_veto_freezes_that_action_for_a_while()
    {
        var state = NewState();
        var now = DateTimeOffset.UtcNow;
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "why not", "medium", now));

        Assert.Equal("bribe troll", state.Veto(now));
        Assert.False(state.IsAwaitingGrant);

        // Rule 4: do not re-propose it immediately…
        Assert.True(state.IsVetoed("bribe troll", now));
        Assert.True(state.IsVetoed("BRIBE   troll", now + TimeSpan.FromSeconds(30)));

        // …but it is not a permanent ban.
        Assert.False(state.IsVetoed("bribe troll", now + TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void Any_player_line_preempts_the_companion()
    {
        var state = NewState();
        state.BeginDecision();
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "why not", "medium", DateTimeOffset.UtcNow));

        state.NotePlayerLine("actually, let's look around first", grantedOrVetoed: false);

        Assert.Equal(CompanionState.PlayerInterrupt, state.State);
        Assert.False(state.IsAwaitingGrant); // the plan is dropped, not queued
    }

    [Fact]
    public void Steps_are_counted_for_diagnostics_but_never_capped()
    {
        // There is no step cap any more (§iteration 01 §2.1): a cap could not tell
        // `look` from `burn candle`, so it stopped the harmless wandering while the
        // dangerous actions were already covered by the risk rule. What gets consent
        // is the errand, and the errand has its own budget.
        var state = NewState();

        for (var step = 0; step < 20; step++)
        {
            state.NoteInputSent(byAgent: true);
        }

        Assert.Equal(20, state.ChainDepth);

        // The player speaking still resets the count, because it is about how long
        // the companion has been driving.
        state.NotePlayerLine("ok", grantedOrVetoed: true);
        Assert.Equal(0, state.ChainDepth);
    }

    [Fact]
    public void Going_in_circles_is_detected()
    {
        var state = NewState();

        state.NoteActionTaken("open grate");
        Assert.Null(state.StuckAction());

        state.NoteActionTaken("open grate");
        Assert.Null(state.StuckAction()); // twice is not yet a rut

        state.NoteActionTaken("open grate");
        var stuck = state.StuckAction();
        Assert.NotNull(stuck);
        Assert.Equal("open grate", stuck!.Value.Action);
        Assert.Equal(3, stuck.Value.Count);

        Assert.Equal(3, state.RepetitionsOf("open  GRATE"));
    }

    [Fact]
    public void Player_direct_clears_the_plan()
    {
        var state = NewState();
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "why not", "medium", DateTimeOffset.UtcNow));

        state.NotePlayerDirect(started: true);
        Assert.Equal(CompanionState.PlayerDirect, state.State);
        Assert.False(state.IsAwaitingGrant);
    }

    [Fact]
    public void Switching_games_resets_everything()
    {
        var state = NewState();
        state.NoteActionTaken("open grate");
        state.NoteInputSent(byAgent: true);
        state.NoteProposal(new PendingProposal("p1", "bribe troll", "why not", "medium", DateTimeOffset.UtcNow));

        state.ResetForNewGame();

        Assert.Null(state.Pending);
        Assert.Equal(0, state.ChainDepth);
        Assert.Null(state.StuckAction());
        Assert.Equal(CompanionState.Idle, state.State);
    }
}

/// <summary>Speaking triggers and cadence (§10.6).</summary>
public class SpeakingTriggerTests
{
    private static SpeakingTriggers NewTriggers() => new(new AgentConfig
    {
        IdleNudgeSeconds = 50,
        UnpromptedCooldownTurns = 3,
    });

    [Fact]
    public void The_companions_words_are_never_read_for_meaning()
    {
        // Four regular expressions used to live in `OnTurn`, matching what an English story
        // prints: "you are standing in", "Taken.", "You can't go that way.", and a bare
        // quotation mark. Measured over fifty-seven turns of live play across four sessions,
        // between them they fired **twice** — the companion reports after every move it makes,
        // so by the time a room description or a newly taken object arrives, the cooldown has
        // already gated the trigger out.
        //
        // They were also, precisely, the thing this project bans: wording a *story* prints is a
        // fact about one language's games, inside the component that has to play any story in
        // any language. The same mistake `is_movement` was deleted for, wearing a bigger regex.
        // None of these may make it speak, in English or anywhere else.
        var triggers = NewTriggers();

        foreach (var english in new[]
                 {
                     "You can't go that way.",
                     "The troll says, \"Pay me and you may pass.\"",
                     "Taken.\nYou now have a brass lantern.",
                     "You are standing in a forest.",
                     "It is too dark to see.",
                 })
        {
            var decision = triggers.OnTurn(english, agentAuthored: false);
            Assert.False(decision.Speak, $"matched on a story's wording: {english}");
        }

        // And what *does* make it speak needs no reading of anything: it moved.
        Assert.True(triggers.OnTurn("anything at all", agentAuthored: true).Speak);
    }

    [Fact]
    public void The_companion_always_reports_back_after_acting()
    {
        var triggers = NewTriggers();
        var decision = triggers.OnTurn("You are in a forest.", agentAuthored: true);

        Assert.True(decision.Speak);
        Assert.Equal(SpeakReason.AgentActed, decision.Reason);
    }

    [Fact]
    public void Nothing_worth_saying_means_silence()
    {
        var triggers = NewTriggers();
        var decision = triggers.OnTurn("You are carrying nothing.", agentAuthored: false);

        Assert.False(decision.Speak);
    }

    [Fact]
    public void The_idle_nudge_needs_a_lead_and_is_paced()
    {
        var triggers = NewTriggers();
        var quiet = TimeSpan.FromSeconds(60);

        // No lead: stay quiet rather than manufacture small talk.
        Assert.False(triggers.OnIdle(quiet, hasLead: false).Speak);

        // With a lead, one line.
        var nudge = triggers.OnIdle(quiet, hasLead: true);
        Assert.True(nudge.Speak);
        Assert.Equal(SpeakReason.IdleNudge, nudge.Reason);

        // And then no nagging: not again while the quiet clock has barely moved.
        Assert.False(triggers.OnIdle(TimeSpan.FromSeconds(70), hasLead: true).Speak);

        // But it does come round again, on its cadence, without the player having to speak.
        // This is the whole point: latched until the player spoke meant an unattended run
        // nudged once and then could never be prompted again, so the first reply that neither
        // acted nor asked stopped the game for good.
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(115), hasLead: true).Speak);
    }

    [Fact]
    public void A_nudge_does_not_need_the_player_to_have_ever_spoken()
    {
        // The case that was broken: nobody is connected, so nobody will ever speak. The
        // companion still has to be able to prompt itself back into moving.
        var triggers = NewTriggers();

        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(60), hasLead: true).Speak);
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(115), hasLead: true).Speak);
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(170), hasLead: true).Speak);
    }

    [Fact]
    public void The_players_line_restarts_the_nudge_window()
    {
        var triggers = NewTriggers();
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(60), hasLead: true).Speak);

        triggers.NotePlayerSpoke();

        // A fresh quiet period begins from the player's line, so the next nudge is not
        // immediate — and the one after it is paced from there.
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(60), hasLead: true).Speak);
        Assert.False(triggers.OnIdle(TimeSpan.FromSeconds(80), hasLead: true).Speak);
        Assert.True(triggers.OnIdle(TimeSpan.FromSeconds(120), hasLead: true).Speak);
    }

    [Fact]
    public void The_idle_nudge_waits_for_actual_idleness()
    {
        var triggers = NewTriggers();
        Assert.False(triggers.OnIdle(TimeSpan.FromSeconds(10), hasLead: true).Speak);
    }

    [Fact]
    public void The_player_is_greeted_once()
    {
        var triggers = NewTriggers();

        Assert.True(triggers.OnPlayerJoined().Speak);
        Assert.False(triggers.OnPlayerJoined().Speak);
    }
}
