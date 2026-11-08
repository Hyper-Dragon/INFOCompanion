using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// The rule for one act (§iteration 01 §2), and in particular what is <b>not</b> in it.
/// <para>
/// The companion is a person at the keyboard. The game's dictionary and its past refusals are
/// help text for the model, never a veto: this is the test that would have caught a driver
/// being unable to do the simplest thing a player can — answer the game when it asks something.
/// </para>
/// </summary>
public class ActPolicyTests
{
    private static (ActDecision Decision, string Detail) Decide(
        string act,
        bool announced = true,
        bool approved = false,
        bool vetoed = false,
        bool budgetSpent = false,
        bool awaitingAnswer = false,
        RiskLevel standing = RiskLevel.Medium) =>
        ActPolicy.Decide(act, announced, approved, vetoed, budgetSpent, awaitingAnswer, standing);

    [Theory]
    // A chapter menu. Measured live: Border Zone asks "Which chapter would you like to play:
    // 1, 2, 3, or (R)estore?" and `1` is the answer. No dictionary holds it as a verb.
    [InlineData("1")]
    [InlineData("r")]
    // "Are you sure you want to quit?" / "Please answer yes or no."
    [InlineData("y")]
    [InlineData("yes")]
    // An invented verb, the thing the dictionary exists to warn about — warned about, not
    // refused: the game is the authority, and being told costs one turn.
    [InlineData("follow stream")]
    [InlineData("downstream")]
    // A noun the room may not have. Whether it does is the game's answer to give.
    [InlineData("take grue")]
    public void anything_a_person_at_the_keyboard_could_type_is_sent(string act)
    {
        var (decision, _) = Decide(act);

        Assert.Equal(ActDecision.Execute, decision);
    }

    [Fact]
    public void a_risky_act_is_offered_before_it_is_taken()
    {
        // The one per-action gate that survives, because it is about not spending the
        // player's things on a guess.
        var (decision, detail) = Decide("burn candle");

        Assert.Equal(ActDecision.Propose, decision);
        Assert.Contains("risky", detail);
    }

    [Fact]
    public void the_standing_level_is_the_players_own_answer()
    {
        // §4.7, with the same act under three answers. Medium is the gate as it always was,
        // high is trust, and low asks about anything that changes the world — but never about
        // looking, reading, or walking.
        Assert.Equal(ActDecision.Propose, Decide("give bone to poodle").Decision);
        Assert.Equal(ActDecision.Execute, Decide("give bone to poodle", standing: RiskLevel.High).Decision);
        Assert.Equal(ActDecision.Propose, Decide("take the bone", standing: RiskLevel.Low).Decision);
        Assert.Equal(ActDecision.Execute, Decide("north", standing: RiskLevel.Low).Decision);
        Assert.Equal(ActDecision.Execute, Decide("look", standing: RiskLevel.Low).Decision);
        Assert.Equal(ActDecision.Execute, Decide("read leaflet", standing: RiskLevel.Low).Decision);
    }

    [Fact]
    public void an_approved_risky_act_goes_ahead()
    {
        var (decision, _) = Decide("burn candle", approved: true);

        Assert.Equal(ActDecision.Execute, decision);
    }

    [Fact]
    public void the_players_veto_is_the_one_thing_that_stops_it()
    {
        var (decision, detail) = Decide("north", vetoed: true);

        Assert.Equal(ActDecision.Drop, decision);
        Assert.Contains("vetoed", detail);
    }

    [Fact]
    public void a_spent_errand_budget_reports_rather_than_acts()
    {
        var (decision, detail) = Decide("north", budgetSpent: true);

        Assert.Equal(ActDecision.Drop, decision);
        Assert.Contains("run its course", detail);
        // The reason can reach the companion, and a friend does not talk about its allowances:
        // measured, the word came back out of its mouth as "Budget's spent, so here's the whole of
        // it" (issue #36).
        Assert.DoesNotContain("budget", detail);
    }

    [Fact]
    public void an_unannounced_act_is_still_sent_and_says_so()
    {
        // Announce-then-act is transparency, not a gate: the harness will not write the
        // companion's lines, and it will not swallow a move either.
        var (decision, detail) = Decide("look", announced: false);

        Assert.Equal(ActDecision.Execute, decision);
        Assert.Contains("did not announce", detail);
    }

    [Fact]
    public void a_companion_waiting_on_an_answer_does_not_answer_itself()
    {
        // Issue #23: it asked the player a question, so nothing of its own happens until
        // they reply. The player's complaint was precisely that it would not wait.
        var (decision, detail) = Decide("north", awaitingAnswer: true);

        Assert.Equal(ActDecision.Drop, decision);
        Assert.Contains("waiting for the player", detail);
    }

    [Fact]
    public void settling_an_unanswered_question_lets_the_companion_act_again()
    {
        // The fifth stall, as a rule rather than as a log line. While a question is outstanding
        // the companion may not answer itself, so its own acts are refused — which is right. The
        // trap was that the *timeout* only said it was carrying on: the question stayed live, so
        // the refusal stayed too, nothing was ever sent to the game, and the run froze at turn 59
        // while the log reported the give-up every five seconds, for ever.
        var state = new ArbitrationState();
        state.NoteAsked("Open the cage at the snake, or map the hall first?");
        Assert.Equal(ActDecision.Drop, Decide("south", awaitingAnswer: state.IsAwaitingAnswer).Decision);

        // Patience spent: the question is settled, and the refusal goes with it.
        state.ClearAnswer();
        Assert.Equal(ActDecision.Execute, Decide("south", awaitingAnswer: state.IsAwaitingAnswer).Decision);
    }
}
