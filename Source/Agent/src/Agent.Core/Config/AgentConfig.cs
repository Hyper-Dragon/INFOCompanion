using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Core.Loop;
using Agent.Llm;
using Agent.Notebook;

namespace Agent.Core.Config;

/// <summary>
/// Runtime configuration, loaded from <c>agent-config.json</c> so tuning never
/// needs a rebuild (§9.3, §11).
/// <para>
/// The fields in the specification's sample are all here with the same names. The
/// extra fields are co-play tuning knobs that the issues describe numerically
/// (idle-nudge seconds, cadences, thresholds); they all have defaults, so the
/// documented sample file still loads unchanged.
/// </para>
/// </summary>
public sealed record AgentConfig
{
    /// <summary>
    /// The chat endpoint. The code's own default is the dialect's reference endpoint and nothing
    /// more: the **shipped** file is what a deployment gets, and any OpenAI-compatible URL belongs
    /// here (10_delivery.md §2.4 (`D11`), §8.1 (`D18`)).
    /// </summary>
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>Which model plays: a general chat model that can call tools (`C6`).</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "gpt-4o-mini";

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 600;

    [JsonPropertyName("top_p")]
    public double TopP { get; init; } = 0.9;

    [JsonPropertyName("presence_penalty")]
    public double PresencePenalty { get; init; } = 0.2;

    [JsonPropertyName("frequency_penalty")]
    public double FrequencyPenalty { get; init; } = 0.2;

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; init; } = 60;

    /// <summary>Stream replies over SSE (lower latency to first token).</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Thinking mode, in whatever shape the profile says this backend takes it
    /// (10_delivery.md §8.1 (`D18`–`D19`)).
    /// <para>
    /// Absent leaves the provider's default alone — and on the backend the numbers below were
    /// measured on, that default is **on**. Reasoning is billed as <em>output</em>, the dearest
    /// class, and it is reasoning the player never reads: what they see is the companion's own
    /// `[think]` line. Measured, enabling it took a call from ~40 completion tokens to ~3,000.
    /// </para>
    /// </summary>
    [JsonPropertyName("thinking")]
    public ThinkingConfig? Thinking { get; init; }

    /// <summary>`low`, `high` or `max`. Absent leaves it to the provider default, which is high.</summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("pricing_per_1k_tokens")]
    public PricingConfig PricingPer1kTokens { get; init; } = new();

    /// <summary>
    /// The published per-model price table (10_delivery.md §8.1 (`D18`–`D19`)).
    /// <para>
    /// Providers publish per <em>million</em> tokens and some halve the rate outside published peak
    /// hours — so the table is kept in the units it is published in, with the peak windows
    /// beside it. A rate can then be copied off the pricing page without arithmetic, and
    /// arithmetic in a config file is how a cost figure ends up quietly wrong.
    /// </para>
    /// </summary>
    [JsonPropertyName("pricing")]
    public PricingTableConfig Pricing { get; init; } = new();

    /// <summary>
    /// Which OpenAI-compatible backend this is, so the two things a dialect decides can be resolved
    /// (10_delivery.md §8.1 (`D18`)): where the cache split is read out of the usage object, and in what shape a
    /// request for private reasoning travels. `deepseek` and `openai` are known by name; anything
    /// else runs fine, reports no cache figure, and is sent none of the dialect's own fields.
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "openai";

    /// <summary>
    /// How many recent turns make up the verbatim window. The floor (05_memory.md §2 (`M9`–`M11`)) may
    /// widen it — every player line and the newest items are never dropped — but nothing
    /// widens it beyond this by recency alone.
    /// </summary>
    [JsonPropertyName("transcript_turns")]
    public int TranscriptTurns { get; init; } = 40;

    /// <summary>
    /// Token ceiling for the play in front of the model, excluding the fixed prompt and
    /// excluding the floor (05_memory.md §2 (`M9`–`M11`)). Sized against what the corpus actually is: an
    /// entire evening measures ~10k tokens, so the whole of it fits with room to grow —
    /// and the floor is never dropped whatever this says.
    /// </summary>
    [JsonPropertyName("transcript_token_budget")]
    public int TranscriptTokenBudget { get; init; } = 24000;

    /// <summary>
    /// Attention bound for the fixed prompt — instructions, game brief, notebook and
    /// memories (05_memory.md §2 (`M9`–`M11`)). Over it the harness warns; it never trims. The fixed
    /// parts are what prefix caching caches, so they are cheap in money and expensive
    /// only in attention; a number here is a prompt to prune the notebook, not a
    /// mechanism to delete it.
    /// </summary>
    [JsonPropertyName("fixed_token_budget")]
    public int FixedTokenBudget { get; init; } = 16000;

    /// <summary>
    /// How many local retrieval rounds one pass may make (04_companion.md §3 (`C14`–`C25`)): each `recall` or
    /// `read_notes` is answered from the files and fed back, and the model is called again,
    /// up to this many times. A cap rather than a law — an action aimed at the game ends the
    /// rounds anyway, because the game answers asynchronously.
    /// </summary>
    [JsonPropertyName("local_tool_rounds")]
    public int LocalToolRounds { get; init; } = 4;

    /// <summary>Notebook budget; over this the companion is asked to prune (§10.5).</summary>
    /// <summary>
    /// How large the companion's own notes may get before it is told to trim them.
    /// <para>
    /// This is an <b>attention</b> bound, not a money bound, and the original 2500 was a
    /// number picked out of the air. Money is the wrong axis now: the notebook sits at the
    /// front of the prompt and changes rarely, so it is exactly what prefix caching caches,
    /// and a cached token costs a fraction of a fresh one. Meanwhile the old behaviour —
    /// warning on every call, asking the model to spend a call rewriting its notes to save a
    /// few hundred tokens — cost far more than the tokens it saved.
    /// </para>
    /// <para>
    /// What is worth bounding is attention: the notes compete with the transcript for the
    /// model's regard, and a companion buried in its own notes plays worse. Half the
    /// transcript window is the rule — so the record of what actually happened always has at
    /// least as much room as the notes about it.
    /// </para>
    /// </summary>
    [JsonPropertyName("notebook_token_budget")]
    public int NotebookTokenBudget { get; init; } = NotebookStore.DefaultTokenBudget;

    /// <summary>
    /// Budget for the companion's memories of the player, which outlive a game (issue #27).
    /// Deliberately much smaller than the notebook: it travels in every call, and a long
    /// memory of old evenings crowds out the one it is meant to help with.
    /// </summary>
    [JsonPropertyName("memory_token_budget")]
    public int MemoryTokenBudget { get; init; } = PersistentMemory.DefaultTokenBudget;

    /// <summary>
    /// Turns between "take stock" passes, where the companion is asked to check its notes
    /// against what has happened and to write down what it wants to keep (issue #22/#27).
    /// `0` disables the cadence: the pass still runs when the game changes or restarts.
    /// </summary>
    [JsonPropertyName("reconcile_every_turns")]
    public int ReconcileEveryTurns { get; init; } = 20;

    /// <summary>
    /// How many times the companion may be asked to answer a game that has not returned a
    /// command prompt before it is left alone (issue #19). A guard, not a limit: a game
    /// stuck on a menu nobody can answer must not spin the model forever.
    /// </summary>
    [JsonPropertyName("max_game_prompt_answers")]
    public int MaxGamePromptAnswers { get; init; } = 3;

    /// <summary>Quiet seconds before a single idle nudge is allowed (§10.6).</summary>
    [JsonPropertyName("idle_nudge_seconds")]
    public int IdleNudgeSeconds { get; init; } = 50;

    /// <summary>
    /// Quiet seconds before the companion may say something **for its own sake** — a line from the
    /// room rather than a nudge about the game (04_companion.md §5 (`C71`)). Zero switches it off.
    /// <para>
    /// Much longer than the nudge on purpose, and it has to be: this is the one thing in the system
    /// that is not allowed to be useful, so its whole value is in being rare. Measured 2026-10-02:
    /// every trigger that spoke was caused by game state, and the companion only sounded like a person
    /// when it needed a decision — sixteen times as much interiority sat in its reasoning as in its
    /// speech, and none of it could reach the room.
    /// </para>
    /// </summary>
    [JsonPropertyName("out_loud_seconds")]
    public int OutLoudSeconds { get; init; } = 300;

    /// <summary>
    /// How long the companion holds everything waiting for the player before deciding it
    /// itself (issues #23, #29).
    /// <para>
    /// Covers both ways it hands the turn over: the answer to a question it asked, and the
    /// grant or veto of a risky action it proposed. An unattended companion is the normal
    /// case, not the exception — nobody is connected to answer, so an unbounded wait is a
    /// dead run. Measured twice on a clean container: it asked the empty room to choose
    /// between two leads and stopped at turn six, then proposed <c>light lamp</c> and stopped
    /// at turn eighteen, in the dark.
    /// </para>
    /// <para>
    /// Long enough that a player who is reading is never talked over — the clock starts when
    /// the question or proposal goes out, and any player line clears it — and short enough
    /// that a stopped game restarts itself inside a minute and a half. <c>0</c> waits for
    /// ever, which is only correct if someone is always at the keyboard.
    /// </para>
    /// </summary>
    [JsonPropertyName("player_patience_seconds")]
    public int PlayerPatienceSeconds { get; init; } = 90;

    /// <summary>Minimum turns between unprompted utterances (§10.6).</summary>
    [JsonPropertyName("unprompted_cooldown_turns")]
    public int UnpromptedCooldownTurns { get; init; } = 3;

    /// <summary>
    /// How many times the companion is asked to try again after the game refused an act.
    /// <para>
    /// One: a parser that said no once said it for a reason, and a companion that keeps rephrasing
    /// spends the player's money arguing with a 1983 dictionary.
    /// </para>
    /// </summary>
    [JsonPropertyName("max_refused_acts")]
    public int MaxRefusedActs { get; init; } = ConfigDefaults.MaxRefusedActs;

    /// <summary>How often the loop looks at its timers (§10.6).</summary>
    [JsonPropertyName("tick_seconds")]
    public int TickSeconds { get; init; } = ConfigDefaults.TickSeconds;

    /// <summary>
    /// How many turns back the autosave's tail reaches when it rebuilds a checkpoint's meaning.
    /// </summary>
    [JsonPropertyName("tail_turns_for_autosave")]
    public int TailTurnsForAutosave { get; init; } = ConfigDefaults.TailTurnsForAutosave;

    /// <summary>How many learned parser words the catch-up report reads.</summary>
    [JsonPropertyName("vocabulary_tail")]
    public int VocabularyTail { get; init; } = ConfigDefaults.VocabularyTail;

    /// <summary>
    /// How many player lines are kept while the player is away (§5.2).
    /// <para>
    /// A bound rather than a transcript: somebody who has been gone for an hour has said a great
    /// deal, and the point of the list is to say what they got up to, not to replay it.
    /// </para>
    /// </summary>
    [JsonPropertyName("away_player_line_limit")]
    public int AwayPlayerLineLimit { get; init; } = ConfigDefaults.AwayPlayerLineLimit;

    /// <summary>
    /// How many replies may wait for a game turn in flight before the oldest is dropped
    /// (see <c>PendingReplies</c>).
    /// </summary>
    [JsonPropertyName("max_held_replies")]
    public int MaxHeldReplies { get; init; } = ConfigDefaults.MaxHeldReplies;

    /// <summary>
    /// How long the companion may go on holding a reply for a game turn that never lands.
    /// <para>
    /// The net under every path that can drop a line — a refusal, a crash mid-dispatch, something
    /// nobody has thought of yet. Without it a dropped line is a silent session: the replies are held
    /// for ever, the model is never called again, and nothing on the screen says so (measured
    /// 2026-09-24). Checked on the loop's tick; <c>0</c> turns the deadline off.
    /// </para>
    /// </summary>
    [JsonPropertyName("turn_flight_seconds")]
    public int TurnFlightSeconds { get; init; } = ConfigDefaults.TurnFlightSeconds;

    /// <summary>
    /// The longest line that may still be a single game command (§10.4, issue #40).
    /// <para>
    /// "unlock the grate with the keys" is six words; a sentence that merely opens with a verb runs
    /// to ten or more, and telling those apart is the whole job of this number.
    /// </para>
    /// </summary>
    [JsonPropertyName("max_command_words")]
    public int MaxCommandWords { get; init; } = ConfigDefaults.MaxCommandWords;

    /// <summary>How many of the companion's recent actions the arbitration history keeps.</summary>
    [JsonPropertyName("arbitration_history_length")]
    public int ArbitrationHistoryLength { get; init; } = ConfigDefaults.ArbitrationHistoryLength;

    /// <summary>
    /// The longest line that may still be an acknowledgement rather than a question (§10.3).
    /// <para>
    /// "yes", "go on then", "ok" are answers; a sentence with a clause in it is somebody thinking
    /// aloud, and classifying that as consent is how a companion does something nobody asked for.
    /// </para>
    /// </summary>
    [JsonPropertyName("short_phrase_words")]
    public int ShortPhraseWords { get; init; } = ConfigDefaults.ShortPhraseWords;

    /// <summary>
    /// The ceiling on retry backoff, in seconds.
    /// <para>
    /// A provider that is down stays down; without a cap the ladder would push the next attempt
    /// past the end of the evening.
    /// </para>
    /// </summary>
    [JsonPropertyName("max_backoff_seconds")]
    public int MaxBackoffSeconds { get; init; } = ConfigDefaults.MaxBackoffSeconds;

    /// <summary>
    /// How long the seat's writer is waited for on the way out, in seconds (10_delivery.md §2.4 (`D11`)).
    /// <para>
    /// Best effort: the child is being disposed either way, and a writer that has not finished in
    /// this long is not going to.
    /// </para>
    /// </summary>
    [JsonPropertyName("seat_writer_wait_seconds")]
    public int SeatWriterWaitSeconds { get; init; } = ConfigDefaults.SeatWriterWaitSeconds;

    /// <summary>How many actions the companion may chain before handing the wheel back.</summary>
    [JsonPropertyName("mission_budget")]
    public int MissionBudget { get; init; } = 8;

    /// <summary>
    /// How many actions an errand <em>the player</em> set may take (04_companion.md §6 (`C42`–`C47`)).
    /// <para>
    /// A turn limit cannot tell the player's own instruction from the companion's own idea, and it
    /// should. What an allowance is for is the companion talking itself into a long walk — not the
    /// player asking for something and having to ask again every eight steps. Measured 2026-09-19:
    /// the *same ground* took <b>9 errands to walk the first time and 21 the second</b>, because
    /// every eighth step was a hard stop, and six of that evening's player lines were literally
    /// "keep going". The companion was not lost. It was interrupted, over ground it knew.
    /// </para>
    /// <para>
    /// Set out of the way rather than infinite, because the two bounds on an unattended errand
    /// should be the ones a person would choose: the companion reports back, or the player halts it
    /// by holding the line (a leading space). A genuine runaway is caught by
    /// <see cref="SessionCostLimitUsd"/>.
    /// </para>
    /// </summary>
    [JsonPropertyName("mission_budget_delegated")]
    public int MissionBudgetDelegated { get; init; } = 10000;

    /// <summary>
    /// The session's cost bound (04_companion.md §6 (`C42.7`)): past this the companion warns once and stops
    /// starting anything of its own. Never a kill — the game keeps playing and the player keeps
    /// being answered (01_frame.md §3 (`F12`)).
    /// <para>
    /// It is here because a delegated errand is no longer bounded by its own step count, which
    /// makes this the only bound standing between a confused companion and an unbounded bill. The
    /// 2026-09-19 evening cost $0.28 with eight-step errands.
    /// </para>
    /// </summary>
    [JsonPropertyName("session_cost_limit_usd")]
    public double SessionCostLimitUsd { get; init; } = 2.00;

    /// <summary>
    /// The danger level the session starts at (§4.7): <c>low</c>, <c>medium</c> or <c>high</c>.
    /// Anything at or under it proceeds without a proposal; above it, the harness asks once.
    /// <c>medium</c> is the gate as it always was — spending, breaking, burning and giving away
    /// stop for the player, everything else does not. A player who wants it otherwise says so
    /// with <c>/risklow</c>, <c>/riskmedium</c> or <c>/riskhigh</c>, which moves both the gate
    /// and the sentence the companion reads, for that session.
    /// </summary>
    [JsonPropertyName("auto_allow_below")]
    public string AutoAllowBelow { get; init; } = "medium";

    /// <summary>
    /// How much of a game's own sidecar — the manual, the map, the feelies, what came in the
    /// box — goes into the prompt, in characters. Generous by default: in 1983 the box was part
    /// of the game, and this is the mechanism that puts it on the table. Read once per game
    /// load, so it rides the cached prefix rather than costing anything per turn.
    /// </summary>
    [JsonPropertyName("game_brief_max_chars")]
    public int GameBriefMaxChars { get; init; } = GameBrief.DefaultMaxCharacters;

    /// <summary>How many turns of the record one <c>recall</c> search may bring back.</summary>
    [JsonPropertyName("recall_max_hits")]
    public int RecallMaxHits { get; init; } = ObservationLog.SearchLimit;

    /// <summary>How many characters of a turn's output one <c>recall</c> hit quotes.</summary>
    [JsonPropertyName("recall_quote_chars")]
    public int RecallQuoteChars { get; init; } = ObservationLog.SearchQuoteLimit;

    /// <summary>
    /// How many recent turns the take-stock pass is shown.
    /// <para>
    /// The default is <see cref="ObservationLog.DefaultLimit"/> — the same number the record falls
    /// back on when a reader does not say, because a gatherer and a reader disagreeing about "recent"
    /// is a difference nobody can see in the log.
    /// </para>
    /// </summary>
    [JsonPropertyName("reconcile_recent_turns")]
    public int ReconcileRecentTurns { get; init; } = ObservationLog.DefaultLimit;

    /// <summary>How many characters of a turn's output the record quotes (05_memory.md (`M1`–`M39`)).</summary>
    [JsonPropertyName("observation_quote_limit")]
    public int ObservationQuoteLimit { get; init; } = ObservationLog.QuoteLimit;

    /// <summary>How many of the companion's own recent lines the floor always keeps.</summary>
    [JsonPropertyName("floor_companion_lines")]
    public int FloorCompanionLines { get; init; } = 3;

    /// <summary>How many of the newest items of any kind the floor always keeps.</summary>
    [JsonPropertyName("floor_recent_items")]
    public int FloorRecentItems { get; init; } = 5;

    /// <summary>
    /// How long a keystroke keeps the companion holding for an answer it is waiting on (issue #42).
    /// A person typing every few seconds is composing; one who stopped ten seconds ago is reading
    /// what it said, which is a different thing and gets the ordinary patience.
    /// </summary>
    [JsonPropertyName("player_typing_window_seconds")]
    public int PlayerTypingWindowSeconds { get; init; } = 4;

    /// <summary>
    /// The shortest interval between two rewrites of the notebook.
    /// <para>
    /// The notebook sits at the front of the prompt, so changing it invalidates every cached token
    /// behind it. Measured over one evening: 24 rewrites, and the call after each paid 8,530 fresh
    /// prompt tokens against a session mean of 2,587 — and they came in bursts of three or four
    /// inside a minute, each burst paying for the whole prompt again (2026-09-15). A rewrite is
    /// still allowed when it *shrinks* the notebook or when the notebook is over its attention
    /// budget; this is a bound on churn, not a licence to lose what was written.
    /// </para>
    /// </summary>
    [JsonPropertyName("notebook_rewrite_min_seconds")]
    public int NotebookRewriteMinSeconds { get; init; } = 90;

    /// <summary>How long a vetoed action stays vetoed (§10.4 rule 4).</summary>
    [JsonPropertyName("veto_cooldown_seconds")]
    public int VetoCooldownSeconds { get; init; } = 90;

    /// <summary>Repetitions of the same action that count as going in circles (§10.6).</summary>
    [JsonPropertyName("circles_threshold")]
    public int CirclesThreshold { get; init; } = 2;

    /// <summary>
    /// How many model calls the companion may make on its own between two things the player did.
    /// <para>
    /// This is the money rule, and it exists because the thing it bounds is an unbroken chain
    /// rather than a single call. The companion takes a move, the game answers, the answer
    /// triggers a report, the report takes another move — and nothing in the loop ever counted.
    /// Measured on one live session: nine of thirteen calls were the companion starting
    /// something by itself, and the idle brief's "at most two, then hand it back" was enforced
    /// by nothing at all. On a server that runs all day with somebody logged in but away, that
    /// is the difference between a few pence and a few pounds an hour.
    /// </para>
    /// <para>
    /// Four is about two moves and the two reports that follow them, which is what the brief
    /// promises. The counter resets the moment the player says anything, joins, takes the
    /// keyboard, or asks the companion to type a line — so this can never silence the companion
    /// while the two of them are actually playing.
    /// </para>
    /// </summary>
    [JsonPropertyName("unattended_call_limit")]
    public int UnattendedCallLimit { get; init; } = 4;

    /// <summary>
    /// The allowance for maintenance passes — taking stock, with the cursor nowhere near it.
    /// <para>
    /// The social allowance above exists to stop the companion talking to itself in front of a
    /// silent player, and it was doing that job and one it should never have been given at the
    /// same time: measured on 2026-09-16, ten of twenty <c>unattended_limit</c> events were
    /// <c>reconcile</c> — the one pass whose whole purpose is to happen while nobody is typing.
    /// It has its own, bounded budget now, and the social limit keeps its own meaning.
    /// </para>
    /// </summary>
    [JsonPropertyName("maintenance_call_limit")]
    public int MaintenanceCallLimit { get; init; } = 6;

    /// <summary>
    /// Per-purpose completion ceilings, for the passes whose reply is shaped differently from their
    /// role's — and the shipped file names none, because the roles cover every pass the loop runs.
    /// <para>
    /// Measured on the evening of 2026-09-16: three replies hit the global ceiling and were cut
    /// off — one lost a mission-step's act, one cut a whole-file notebook rewrite mid-sentence
    /// (which is one way fragments reach the notes). Taking stock is the pass that thinks hardest
    /// and speaks least, so it is the wrong place to save output tokens.
    /// </para>
    /// <para>
    /// An entry overrides the pass's role share, which is what <see cref="MaxTokensFor"/> reaches for
    /// first. It may **not** be thrift: an entry under <see cref="FloorFor"/> fails the gate, because
    /// there is no saving in a clip — the retry re-sends the whole prompt to save a few hundred tokens
    /// of completion (`C23.7`).
    /// </para>
    /// </summary>
    [JsonPropertyName("max_tokens_by_purpose")]
    public Dictionary<string, int> MaxTokensByPurpose { get; init; } = new();

    /// <summary>
    /// How much of the window a pass may fill, by the **role** it has (`C23.6`).
    /// <para>
    /// This is the policy, and it is stated against the window rather than as a total so that the one
    /// number a deployment changes when it changes model — <see cref="ContextWindowTokens"/> — moves
    /// every ceiling with it. A quarter for a pass that answers somebody and a half for a pass that
    /// composes something: the two numbers reproduce the shape the hand-written table already had
    /// (16,000 ≈ ¼ of 65,536; the write-up's 32,000 = ½), which is where they come from.
    /// </para>
    /// <para>
    /// A deployment whose model gives less than this per reply lowers the shares here rather than
    /// lowering one pass's entry: a share says *how much of the window a reply may use*, and setting it
    /// to a real fraction of a small window is the honest way to say a small answer is wanted.
    /// </para>
    /// </summary>
    [JsonPropertyName("max_tokens_share_by_role")]
    public Dictionary<string, double> MaxTokensShareByRole { get; init; } = new(StringComparer.Ordinal)
    {
        [Passes.ReplyName] = 0.25,
        [Passes.WriteName] = 0.5,
    };

    /// <summary>
    /// How many tokens the endpoint can hold at once: prompt **and** completion together.
    /// <para>
    /// The one hard constraint on any ceiling, and the reason a ceiling is arithmetic rather than a
    /// wish. A request whose prompt plus completion overshoots the window is refused outright, and a
    /// refusal here arrives looking like a provider fault rather than like a setting — so the number
    /// wanted is clamped to what the prompt leaves (<see cref="CeilingThatFits"/>).
    /// </para>
    /// <para>
    /// A property of the *model* rather than of the dialect, which is why it lives here and not on
    /// `ProviderProfile`: two models behind one OpenAI-compatible endpoint have different windows, and
    /// a deployment pointing this at a local server has to be able to say what its own is. The default
    /// is the window the shipped model has (`10_delivery.md` §2).
    /// </para>
    /// </summary>
    [JsonPropertyName("context_window_tokens")]
    public int ContextWindowTokens { get; init; } = 65536;

    /// <summary>
    /// How long a model call may be in flight, with a keyboard at the ready, before the panel
    /// says one quiet word about it.
    /// <para>
    /// Measured on 2026-09-16: p95 was 15.9s, one call took 45.7s, and once the player asked
    /// "james...you there?" — a companion thinking hard and a hung terminal look identical, and
    /// the thought deltas do not start until the model starts producing. Zero disables it.
    /// </para>
    /// </summary>
    [JsonPropertyName("working_indicator_seconds")]
    public int WorkingIndicatorSeconds { get; init; } = 8;

    /// <summary>
    /// How long the room must be quiet before a cadence-triggered taking-stock starts. Taking
    /// stock is a 25-second pass by measurement (2026-09-16: five calls, 24.9s average), and
    /// starting one a moment after the player has typed is correct work in the wrong moment.
    /// </summary>
    [JsonPropertyName("reconcile_min_quiet_seconds")]
    public int ReconcileMinQuietSeconds { get; init; } = 60;

    /// <summary>
    /// Which personality to play: a slug, resolved to <c>&lt;persona_directory&gt;/&lt;slug&gt;.txt</c>.
    /// <para>
    /// A slug rather than a path so that swapping the character is one word — drop
    /// <c>prompts/persona/alice.txt</c> beside the shipped one and set this to <c>alice</c>. The
    /// name comes from the file's own front matter, so that is genuinely the only thing to set.
    /// </para>
    /// </summary>
    [JsonPropertyName("persona")]
    public string Persona { get; init; } = "james";

    /// <summary>Where the personalities live. Overridable so none of this needs a rebuild (§10.1).</summary>
    [JsonPropertyName("persona_directory")]
    public string PersonaDirectory { get; init; } = "/prompts/persona";

    /// <summary>
    /// The engine: the frame, the rules, and the tool contract, shared by every personality.
    /// <para>
    /// Kept separate on purpose. Most of what used to be one persona file was not personality at
    /// all — what a text adventure is, how a parser behaves, what counts as honest — and leaving
    /// it in the persona file meant every future personality had to restate the lot.
    /// </para>
    /// </summary>
    [JsonPropertyName("engine_path")]
    public string EnginePath { get; init; } = "/prompts/engine.txt";

    /// <summary>
    /// Where the story files live, so a game's optional sidecar can be found beside it.
    /// <para>
    /// A game may ship a <c>&lt;stem&gt;.md</c> next to the story file, holding whatever a person
    /// would have had in their hands before sitting down: the manual, the map, the letter, the
    /// feelies, and the odd note about how this one behaves (see <c>GameBrief</c>). Read-only, and
    /// the normal case is that there isn't one: the sidecar is entirely optional and most games
    /// will never have one. The daemon and the agent are told this by the same variable, because
    /// they are looking at the same directory.
    /// </para>
    /// </summary>
    [JsonPropertyName("games_directory")]
    public string GamesDirectory { get; init; } = "/games";

    /// <summary>How many agent log files to keep before rotating (§13).</summary>
    [JsonPropertyName("log_keep")]
    public int LogKeep { get; init; } = 20;

    /// <summary>Pause before retrying the model after repeated failures.</summary>
    [JsonPropertyName("llm_failure_pause_seconds")]
    public int LlmFailurePauseSeconds { get; init; } = 120;

    /// <summary>
    /// How often the companion may say one honest line about the model being unreachable
    /// (04_companion.md §10 (`C56`–`C60`)).
    /// <para>
    /// The first line is said the moment the pause begins. After that the model is not the
    /// messenger — it is the thing that is broken — so these are the harness's own words, and the
    /// only thing they must not do is pretend nothing is wrong: an evening that reads as "the
    /// companion has gone quiet on me" is worse than one that reads as "the model is down, keep
    /// playing". Zero says nothing after the first line.
    /// </para>
    /// </summary>
    [JsonPropertyName("away_notice_seconds")]
    public int AwayNoticeSeconds { get; init; } = 300;

    /// <summary>
    /// How many turns of a missed window the catch-up pass reads (04_companion.md §10 (`C56`–`C60`)). A long outage is
    /// longer than a block of notes is worth: the window is the part of the evening the companion
    /// can still do something about, and the record keeps the rest.
    /// </summary>
    [JsonPropertyName("catchup_max_turns")]
    public int CatchUpMaxTurns { get; init; } = 40;

    /// <summary>
    /// How many turns of the record a *write-up* reads (05_memory.md §6 (`M27`–`M36`)). Larger than the catch-up's,
    /// because a report is about a whole game rather than about one outage: an evening and a bit is
    /// enough for a game played over several sittings, and the block says itself how much of the
    /// whole it is showing.
    /// </summary>
    [JsonPropertyName("report_turns")]
    public int ReportTurns { get; init; } = 200;

    /// <summary>
    /// The completion ceiling for one pass: its own entry if it has one, else its **role**'s share of
    /// the window, else the global number.
    /// <para>
    /// This replaced a single global number plus three hand-written exceptions, and the reason is in
    /// the history of those three: they had to be raised by hand whenever the context grew, the
    /// document and the shipped file drifted apart by a factor of three (`introspect` 4,000 against
    /// 12,000, against a prompt of 28,230), and the failure of getting it wrong is silent to everyone
    /// but the log. A share of the window cannot drift from the window, and it follows a bigger model
    /// without an edit (`C23.6`).
    /// </para>
    /// <para>
    /// An entry in <see cref="MaxTokensByPurpose"/> still wins, because there are passes whose reply
    /// is shaped differently from their role's — but it is a shape decision and never a saving, which
    /// is what <see cref="FloorFor"/> is for (`C23.7`).
    /// </para>
    /// </summary>
    /// <param name="escalated">
    /// Ask for **twice** the room, which is what a pass that has already been cut off once is given
    /// when it is asked again (04_companion.md §3.2 (`C23`)). The doubling is the cheap half of a bad
    /// trade corrected: the retry re-sends the whole prompt whatever the ceiling is — 14,429 tokens per
    /// call in the 2026-09-30 sitting — so a ceiling that bit once and was handed back unchanged bought
    /// a second cut-off for the price of a fresh prompt. Narrowing the ask instead would lose the words
    /// that were being said.
    /// </param>
    public int MaxTokensFor(string purpose, bool escalated = false)
    {
        var wanted = MaxTokensByPurpose.TryGetValue(purpose, out var named) && named > 0
            ? named
            : RoleCeilingFor(purpose);

        return escalated ? wanted * 2 : wanted;
    }

    /// <summary>
    /// What the pass's **role** alone asks for, as a share of the window (`C23.6`), or the global
    /// number for a pass whose purpose has no role.
    /// <para>
    /// This is the number the floor is measured against, so it deliberately ignores
    /// <see cref="MaxTokensByPurpose"/>: an entry cannot be its own floor.
    /// </para>
    /// </summary>
    public int RoleCeilingFor(string purpose)
    {
        if (Passes.RoleFor(purpose) is { } role
            && MaxTokensShareByRole.TryGetValue(Passes.NameOf(role), out var share)
            && share > 0)
        {
            return (int)(ContextWindowTokens * share);
        }

        // A purpose nobody classified, or a deployment that named no shares: one number for every pass,
        // which is the arrangement this replaced and the one an unconfigured harness starts from.
        return MaxTokens;
    }

    /// <summary>
    /// The least a shipped ceiling may be for a pass whose role it has: **half** what the role is
    /// given (`C23.7`).
    /// <para>
    /// Half is a judgement rather than a measurement, and it is stated as a fraction of the role rather
    /// than as a number so that it moves with the window and cannot drift. What it is for: an entry in
    /// <see cref="MaxTokensByPurpose"/> exists to name a pass whose reply is shaped differently, and
    /// below half of what its role already allows it can only be a way of spending less — which is the
    /// one reason a ceiling must never be set by, because a clip costs the whole prompt to re-send
    /// (~14.4k tokens) to save a few hundred of completion.
    /// </para>
    /// <para>
    /// It is enforced by a test that reads the shipped file, so a deployment cannot ship one under it
    /// without the gate saying so.
    /// </para>
    /// </summary>
    public int FloorFor(string purpose) => (int)(RoleCeilingFor(purpose) / 2.0);

    /// <summary>
    /// Slack kept back from the window for the request envelope, and for the prompt figure being an
    /// estimate rather than a count.
    /// </summary>
    private const int CeilingMarginTokens = 2048;

    /// <summary>
    /// The ceiling that will actually fit, given how big the prompt turned out to be.
    /// <para>
    /// Clamping is not thrift: it is the difference between a generous ceiling and a refused request.
    /// Nothing here lowers a ceiling that fits — a small prompt leaves all the room a pass was asked
    /// for.
    /// </para>
    /// </summary>
    public int CeilingThatFits(int wanted, int promptTokens)
    {
        var room = ContextWindowTokens - promptTokens - CeilingMarginTokens;

        return Math.Max(1, Math.Min(wanted, room));
    }

    public LlmSettings ToLlmSettings() => new()
    {
        BaseUrl = BaseUrl,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        TopP = TopP,
        PresencePenalty = PresencePenalty,
        FrequencyPenalty = FrequencyPenalty,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        Stream = Stream,
        MaxRetries = MaxRetries,
        MaxBackoffSeconds = MaxBackoffSeconds,
        Thinking = Thinking?.Enabled,
        ReasoningEffort = ReasoningEffort,
        Provider = ProviderProfile.For(Provider),
        Pricing = ToPricing(),
    };

    /// <summary>
    /// The resolved price book: the published table where it has an entry for the model, and the
    /// flat per-1K rates as the fallback an older config relies on.
    /// </summary>
    public Pricing ToPricing() => new(
        PricingPer1kTokens.Prompt,
        PricingPer1kTokens.Completion,
        PricingPer1kTokens.CacheHit)
    {
        Models = Pricing.Models.ToDictionary(
            pair => pair.Key,
            pair => new ModelPrices(
                pair.Value.OffPeak?.ToPrices() ?? new TokenPrices(0, 0, 0),
                pair.Value.Peak?.ToPrices()
                    ?? pair.Value.OffPeak?.ToPrices()
                    ?? new TokenPrices(0, 0, 0))),
        // Malformed windows are dropped rather than guessed at: a peak window that is wrong puts
        // a wrong number on every call in it.
        PeakWindowsUtc = [.. Pricing.PeakWindowsUtc
            .Where(window => window.Count == 2
                && window[0] >= 0
                && window[1] > window[0]
                && window[1] <= 24)
            .Select(window => new PeakWindow(window[0], window[1]))],
        PeakWeekdaysOnly = Pricing.PeakWeekdaysOnly,
    };

    /// <summary>
    /// Load the configuration: explicit path first, then the state directory (where
    /// §5.2 says it lives), then next to the binary. Missing everywhere is fine —
    /// the defaults above are the documented v1 settings.
    /// </summary>
    public static (AgentConfig Config, string? Source, string? Problem) Load(string stateDirectory)
    {
        foreach (var candidate in Candidates(stateDirectory))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate);
                var config = JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
                return (config, candidate, null);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A broken config must not stop the companion; report it and use
                // the documented defaults instead of guessing at half a file.
                return (new AgentConfig(), null, $"{candidate}: {ex.Message}");
            }
        }

        return (new AgentConfig(), null, null);
    }

    private static IEnumerable<string> Candidates(string stateDirectory)
    {
        var explicitPath = Environment.GetEnvironmentVariable("INFOCOMPANION_AGENT_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
        }

        yield return Path.Combine(stateDirectory, "agent-config.json");
        yield return Path.Combine(AppContext.BaseDirectory, "agent-config.json");
    }
}

/// <summary>Price per 1000 tokens, as configured (§9.3).</summary>
public sealed record PricingConfig{
    [JsonPropertyName("prompt")]
    public double Prompt { get; init; }

    [JsonPropertyName("completion")]
    public double Completion { get; init; }

    /// <summary>
    /// What a cached prompt token costs. Left at the sentinel when the config does not say, in
    /// which case the full prompt rate is used — see <see cref="Pricing.EffectiveCacheHitPer1K"/>.
    /// </summary>
    [JsonPropertyName("cache_hit")]
    public double CacheHit { get; init; } = -1;
}

/// <summary>
/// Thinking mode, in the provider's shape. An unrecognised value means "no opinion", and the
/// provider's own default stands — better than guessing at a boolean from a typo.
/// </summary>
public sealed record ThinkingConfig
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "enabled";

    public bool? Enabled => Type.Trim().ToLowerInvariant() switch
    {
        "enabled" => true,
        "disabled" => false,
        _ => null,
    };

    /// <summary>
    /// Run the maintenance passes without private reasoning.
    /// <para>
    /// Maintenance means the calls that are not the companion talking to anyone and not moving:
    /// taking stock, and later compaction. They gain nothing from reasoning, and reasoning is
    /// billed as output — but this is a cost decision rather than a correctness one, so it is
    /// **off by default** and the shipped behaviour is whatever the provider does.
    /// </para>
    /// <para>
    /// It applies to those purposes only. A maintenance pass is still allowed to write its notes
    /// and say its one line; what it loses is the private working that nobody reads.
    /// </para>
    /// </summary>
    [JsonPropertyName("off_for_maintenance")]
    public bool OffForMaintenance { get; init; }

    /// <summary>
    /// Run these passes without private reasoning, named by purpose (`mission-step`, `player-line`, …).
    /// <para>
    /// The general form of <see cref="OffForMaintenance"/>, and an instrument for one question
    /// rather than a policy. Measured on 2026-09-25: 64 of one evening's 199 turns ended with the
    /// companion announcing a move and typing nothing, and the misses clustered exactly where the
    /// private reasoning was longest — 1,000 to 2,800 characters on the misses, against a few
    /// hundred on the turns that moved. The hypothesis is that a long working-out is what crowds
    /// the call out of the reply. That is a fact about a model and a prompt rather than about this
    /// code, so the switch is here to measure it: **empty by default**, and a deployment that does
    /// not name a purpose behaves exactly as it did before this existed (04_companion.md §12).
    /// </para>
    /// <para>
    /// A purpose named here still speaks, still writes its notes and still moves; what it loses is
    /// the working nobody reads. An unrecognised name is inert rather than an error, because a typo
    /// in an experiment should cost a measurement rather than a session.
    /// </para>
    /// </summary>
    [JsonPropertyName("off_for")]
    public List<string> OffFor { get; init; } = [];
}

/// <summary>
/// The published price table, in the units it is published in: per million tokens, with peak
/// windows as `[[startHour, endHour], …]` in UTC.
/// </summary>
public sealed record PricingTableConfig
{
    [JsonPropertyName("models")]
    public Dictionary<string, ModelPricesConfig> Models { get; init; } = DefaultModels();

    [JsonPropertyName("peak_windows_utc")]
    public List<List<int>> PeakWindowsUtc { get; init; } = [[1, 4], [6, 10]];

    [JsonPropertyName("peak_weekdays_only")]
    public bool PeakWeekdaysOnly { get; init; } = true;

    /// <summary>
    /// DeepSeek's published rates, per million tokens, with its peak windows.
    /// <para>
    /// Shipped as the default so cost reporting works without a config file, and repeated in
    /// `agent-config.json` so the numbers can be seen and overridden. Copied from
    /// https://api-docs.deepseek.com/quick_start/pricing/ on 2026-09-14: peak hours are
    /// 01:00-04:00 and 06:00-10:00 UTC Monday-Friday, and off-peak is half of peak.
    /// </para>
    /// <para>
    /// Providers change prices. These are a default, not a fact about the world.
    /// </para>
    /// </summary>
    private static Dictionary<string, ModelPricesConfig> DefaultModels() => new()
    {
        ["deepseek-flash"] = new ModelPricesConfig
        {
            OffPeak = new TokenPricesConfig { CacheHit = 0.003, CacheMiss = 0.15, Output = 0.60 },
            Peak = new TokenPricesConfig { CacheHit = 0.006, CacheMiss = 0.30, Output = 1.20 },
        },
        ["deepseek-v4-pro"] = new ModelPricesConfig
        {
            OffPeak = new TokenPricesConfig { CacheHit = 0.022, CacheMiss = 0.66, Output = 1.98 },
            Peak = new TokenPricesConfig { CacheHit = 0.044, CacheMiss = 1.32, Output = 3.96 },
        },
    };
}

/// <summary>One model's rates, by time of day.</summary>
public sealed record ModelPricesConfig
{
    [JsonPropertyName("off_peak")]
    public TokenPricesConfig? OffPeak { get; init; }

    [JsonPropertyName("peak")]
    public TokenPricesConfig? Peak { get; init; }
}

/// <summary>Rates per million tokens, exactly as the provider publishes them.</summary>
public sealed record TokenPricesConfig
{
    [JsonPropertyName("cache_hit")]
    public double CacheHit { get; init; }

    [JsonPropertyName("cache_miss")]
    public double CacheMiss { get; init; }

    [JsonPropertyName("output")]
    public double Output { get; init; }

    public TokenPrices ToPrices() => new(CacheHit, CacheMiss, Output);
}

/// <summary>
/// The shipped values for the settings this assembly owns, in one place each.
/// <para>
/// Every number here is the default of a property above *and* the fallback of a class that does the
/// work — the loop's tick, the arbitration history's length, the longest command — so writing it
/// twice would be writing it wrong eventually. Tuning it is still done in <c>agent-config.json</c>;
/// this is only where the number an operator never mentions comes from.
/// </para>
/// <para>
/// The equivalent numbers for the notebook's own classes live on those classes
/// (<see cref="ObservationLog.SearchLimit"/> and friends), because that assembly is
/// referenced *by* this one and so cannot see this type. <see cref="AgentConfig"/>'s initialisers
/// point at them, which is what keeps the two in step.
/// </para>
/// </summary>
public static class ConfigDefaults
{
    public const int TickSeconds = 5;
    public const int TailTurnsForAutosave = 6;
    public const int VocabularyTail = 80;
    public const int MaxRefusedActs = 1;
    public const int AwayPlayerLineLimit = 40;
    public const int MaxHeldReplies = 4;
    public const int TurnFlightSeconds = 120;
    public const int MaxCommandWords = 6;
    public const int ArbitrationHistoryLength = 12;
    public const int ShortPhraseWords = 4;
    public const int MaxBackoffSeconds = 30;
    public const int SeatWriterWaitSeconds = 2;
}
