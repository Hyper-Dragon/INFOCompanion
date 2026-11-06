namespace Agent.Core.Loop;

/// <summary>What the loop does with an act (§iteration 01 §2).</summary>
public enum ActDecision
{
    /// <summary>Send it to the game.</summary>
    Execute,

    /// <summary>Risky or irreversible: offer it to the player as a proposal to be granted.</summary>
    Propose,

    /// <summary>Not sent, because the player said no or the errand is over.</summary>
    Drop,
}

/// <summary>
/// The rule for one act, on its own so it can be read and tested without a loop, a model or a
/// game — the same reason the core keeps <c>prompts_allow_save</c> out of its actor.
/// <para>
/// Note what is deliberately *not* in here: the game's dictionary, and the words the game has
/// refused. Both are help text for the model (see <see cref="ParserFeedback.ContextNote"/>),
/// never a veto. The companion is a person at the keyboard: it is the one who types, the game
/// is the authority on what it understands, and a word missing from the dictionary is not proof
/// that the game cannot be driven with it. That is exactly how a driver ends up unable to answer
/// "Which chapter would you like to play: 1, 2, 3, or (R)estore?" — because a digit is not a
/// verb in anyone's dictionary.
/// </para>
/// <para>
/// What survives is what is not about the game's vocabulary: the player's veto, the errand's
/// budget, and consent for anything risky or irreversible.
/// </para>
/// </summary>
public static class ActPolicy
{
    public static (ActDecision Decision, string Detail) Decide(
        string act,
        bool announced,
        bool approvedByPlayer,
        bool vetoedRecently,
        bool budgetSpent,
        bool awaitingAnswer = false,
        RiskLevel standing = RiskLevel.Medium)
    {
        // The player's own decision outranks everything: they said not yet.
        if (vetoedRecently)
        {
            return (ActDecision.Drop, "the player vetoed this recently");
        }

        // The companion asked the player something and has not been answered, so it does not
        // answer itself (issue #23). This is belt and braces: the loop does not even make a
        // self-initiated call while a question is outstanding, but the rule belongs here with
        // the others rather than depending on no caller ever getting it wrong.
        if (awaitingAnswer)
        {
            return (ActDecision.Drop, "waiting for the player's answer to the companion's own question");
        }

        // A spent budget means it is time to report, not to ask for more (§iteration 01 §3).
        // Worded without the word "budget": this text can reach the companion, and a friend does
        // not talk about its own allowances (issue #36).
        if (budgetSpent)
        {
            return (ActDecision.Drop, "the errand has run its course, so it is time to report back");
        }

        // The interpreter's namespace is not the game's. A line beginning with a backslash never
        // reaches the story, and what the interface does with it instead is not playing the game:
        // restart it behind the core's checkpoint, quit the interpreter, or replay a recorded
        // command file. Refused here so the companion can be told and try something else, and
        // refused again in the core, which is the boundary that actually talks to frotz and must
        // not depend on a model being polite.
        if (Vocabulary.IsInterpreterLine(act))
        {
            return (ActDecision.Drop, "a line starting with a backslash belongs to the interpreter, not the game");
        }

        // Risky or irreversible: this is the one per-action gate that survives, because it is
        // about not spending the player's things on a guess (§iteration 01 §2.1). How much risk
        // gets through without being asked about is the player's own standing answer (§4.7) —
        // medium by default, which is exactly the behaviour this gate always had.
        if (Vocabulary.RiskOf(act) > standing && !approvedByPlayer)
        {
            return (ActDecision.Propose, "risky enough that the player should decide");
        }

        // Announce-then-act is kept, but only as transparency: a model that forgets to announce
        // is no longer answered with a line the harness invented. There is no step cap either —
        // the errand is what was consented to, not the step.
        var detail = announced
            ? (approvedByPlayer ? "approved by the player" : "announced and safe")
            : "the model did not announce it";
        return (ActDecision.Execute, detail);
    }
}
