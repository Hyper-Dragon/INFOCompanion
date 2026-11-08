namespace Agent.Core.Loop;

/// <summary>
/// A delegated intent: something the player has asked the companion to go and do over
/// several steps (§iteration 01 §3).
/// <para>
/// This replaces the step cap, which could not tell <c>look</c> from <c>burn candle</c>
/// and so fired on the safe actions while leaving the dangerous ones to the risk rule.
/// What actually needs the player's consent is the <em>intent</em>, and once that has
/// been given the companion should get on with it.
/// </para>
/// <para>
/// The mission is harness state. Its plan is prose, written by the companion into its
/// notebook's <c>Current goals and plan</c> section — no new reply tags, and nothing
/// the harness has to parse out of the model's answer.
/// </para>
/// </summary>
public sealed class Mission
{
    private readonly List<string> _findings = [];

    public Mission(string goal, int budget, string source, string? declaredOnPass = null)
    {
        Goal = goal.Trim();
        Budget = Math.Max(1, budget);
        StartedBy = source;
        FromThePlayer = ThePlayerSetTheGoal(source, declaredOnPass);
    }

    /// <summary>
    /// Did the player set this errand, rather than the companion inventing it?
    /// <para>
    /// The one place these source strings are read, so the ownership rule here and the budget rule
    /// in the loop cannot drift apart: a delegation is the player's errand and is not rationed in
    /// steps, and the companion's own idea is bounded.
    /// </para>
    /// </summary>
    public static bool SourceIsThePlayer(string source) =>
        source is "the player's instruction" or "delegated by the player";

    /// <summary>
    /// The passes whose text is the player's own words arriving.
    /// <para>
    /// `player-line` and its batched twin are the ordinary line; `suggestion` is the same line when
    /// the tables read it as thinking aloud, which is the case the read-back exists for; and
    /// `handover` is the player asking for something to be typed.
    /// </para>
    /// </summary>
    private static bool ThePlayerWasSpeaking(string? pass) =>
        pass is "player-line" or "player-line-batched" or "suggestion" or "handover";

    /// <summary>
    /// Who set the goal — which is not the same question as which way the errand arrived
    /// (04_companion.md §6 (`C42.3`, `C42.6`)).
    /// <para>
    /// A job the companion declares while answering the player is the job the player has just asked
    /// for. The tool's own description says so — <i>"take on a job your friend has handed you … and
    /// not for something you decided to do yourself"</i> — so the declaration is the same act as the
    /// line that earns a move (§6.2), read by the model rather than by the tables, and it takes the
    /// player's bound.
    /// </para>
    /// <para>
    /// Measured 2026-09-30: three long player instructions arrived as declarations, were charged the
    /// companion's own bound, and all three ended at exactly eight steps of eight, mid-plan — while
    /// three throwaway questions, which the tables could read, were given the player's. The most
    /// careful instruction of the evening got the smallest allowance. `StartedBy` still records
    /// which way each errand arrived, because C42.3 asks for that; it is not what decides the bound.
    /// </para>
    /// </summary>
    public static bool ThePlayerSetTheGoal(string source, string? declaredOnPass) =>
        SourceIsThePlayer(source)
        || (source == "declared by the companion" && ThePlayerWasSpeaking(declaredOnPass));

    /// <summary>What the player asked for, in their words.</summary>
    public string Goal { get; }

    /// <summary>How many steps the companion may take before it must report.</summary>
    public int Budget { get; }

    /// <summary>Why the mission started: a delegation, or a proposal the player granted.</summary>
    public string StartedBy { get; }

    /// <summary>
    /// Did the player set this goal?
    /// <para>
    /// The goal belongs to whoever set it (04_companion.md §6 (`C42`–`C47`)): while the player's errand stands, a
    /// declaration of the companion's own is a *step change*, not a new job. Measured on
    /// 2026-09-17: the player's "keep going until you get to the fountain" was replaced by the
    /// companion's own declaration five seconds later, with no step taken, and the journey to the
    /// fountain became a trip back for a blanket.
    /// </para>
    /// <para>
    /// Read from the source <em>and</em> the pass it was declared on, because those are two
    /// questions and only one of them is about ownership — see <see cref="ThePlayerSetTheGoal"/>.
    /// </para>
    /// </summary>
    public bool FromThePlayer { get; }

    public bool Active { get; private set; } = true;

    public int StepsTaken { get; private set; }

    public int StepsRemaining => Math.Max(0, Budget - StepsTaken);

    public bool BudgetExhausted => StepsTaken >= Budget;

    public IReadOnlyList<string> Findings => _findings;

    /// <summary>The companion has taken another step of the errand.</summary>
    public void NoteStepTaken(string step)
    {
        StepsTaken++;
        Notes.Add(step);
    }

    private readonly List<string> Notes = [];

    /// <summary>Something learned along the way, kept for the report.</summary>
    public void NoteFinding(string finding)
    {
        if (!string.IsNullOrWhiteSpace(finding) && !_findings.Contains(finding, StringComparer.OrdinalIgnoreCase))
        {
            _findings.Add(finding.Trim());
        }
    }

    /// <summary>The errand is over: finished, out of budget, or cancelled.</summary>
    public void Finish(string reason)
    {
        Active = false;
        FinishReason = reason;
    }

    /// <summary>Why it ended, for the log.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>
    /// What the companion is told about its errand on every step. Written as a reminder
    /// of its own situation rather than an instruction, so it stays in character.
    /// <para>
    /// Deliberately says nothing about steps, limits or budgets. It used to open with "this is step
    /// 3 of at most 8", and the word came back out of the companion's mouth: measured in the UAT
    /// session it announced <i>"Budget's spent, so here's the whole of it"</i> to the player, which
    /// is the harness describing itself in a friend's voice — the one thing the frame forbids
    /// (issue #36). The bound is real and still enforced; it is simply not the companion's subject.
    /// </para>
    /// <para>
    /// The two endings are named as calls, because that is what they are
    /// (<see cref="CompanionTools.ReportBack"/>). The wording used to promise that <i>"a reply that
    /// moves nothing ends the errand"</i>, which was false twice over: the harness asked again
    /// before it believed the report, and a reply that moved nothing was what the repair fired on
    /// — so the brief invited the very reply the harness then complained about. Measured in the
    /// 2026-09-21 UAT: 167 announced-but-unmade moves, 168 repair calls, 20.5% of the evening's
    /// spend. The repair's own words are what the model obeys, so the brief now says the same
    /// thing it does.
    /// </para>
    /// </summary>
    public string Brief()
    {
        var text = new System.Text.StringBuilder();
        text.Append("You are on an errand for the player: \"").Append(Goal).Append("\". ");

        if (Notes.Count > 0)
        {
            text.Append("Since it started you have: ").Append(string.Join("; ", Notes)).Append(". ");
        }

        if (_findings.Count > 0)
        {
            // The most recent few: enough for the report to hang together without the
            // brief growing without bound over a long errand.
            var recent = _findings.TakeLast(5);
            text.Append("What you have learned so far: ").Append(string.Join("; ", recent)).Append(". ");
        }

        if (BudgetExhausted)
        {
            text.Append(
                "You have done about as much of this as is worth doing in one go. Call "
                + "`report_back` and tell the player what you found, in your own words: that is the "
                + "report, and it ends the errand.");
        }
        else
        {
            text.Append(
                "Take the next step. If you are carrying on, this reply has to carry the move "
                + "itself — a `type` line, or `press_key` if the game is waiting for a key — because "
                + "nothing is typed for you and words on their own are not a step. If the errand is "
                + "done, or you have learned something the player should hear now, call "
                + "`report_back` and say what you found: that is the report, and it ends the errand. "
                + "Keep it to a line or two, and do not rewrite your notebook on every step: "
                + "note things down when you report.");
        }

        return text.ToString();
    }
}
