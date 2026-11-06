using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agent.Core.Config;
using Agent.Core.Logging;
using Agent.Core.Loop;
using Agent.Core.Protocol;
using Agent.Llm;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>A model that says whatever the test tells it to, and remembers what it was asked.</summary>
internal sealed class ScriptedLlm : ILlmClient
{
    private readonly Queue<ChatResult> _replies = new();

    private readonly List<IReadOnlyList<ChatMessage>> _requests = new();
    private readonly List<CallOptions?> _options = new();

    /// <summary>
    /// What it was asked, oldest first — a snapshot, taken under the lock.
    /// <para>
    /// The loop runs on its own task and calls the model from there, so a test that reads this
    /// while a pass it did not ask for is running was enumerating a list the loop was appending
    /// to: measured 2026-09-22 as <c>Collection was modified</c>, in a test whose only sin was
    /// asking what the model had been shown. The passes a test does not cause are the point of
    /// the harness rather than an accident of it, so the record is safe to read.
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>The per-call options each request was made with, so "no tools" can be tested.</summary>
    public IReadOnlyList<CallOptions?> Options
    {
        get
        {
            lock (_options)
            {
                return _options.ToList();
            }
        }
    }

    public string Endpoint => "scripted://";

    /// <summary>Queue a reply: what it says, what it thought, and the tools it calls.</summary>
    public ScriptedLlm Reply(
        string speech = "",
        string reasoning = "",
        FinishReason finish = FinishReason.Stop,
        params (string Name, string ArgumentsJson)[] calls)
    {
        _replies.Enqueue(
            new ChatResult(speech, Usage.Empty, "scripted", finish)
            {
                Reasoning = reasoning,
                ToolCalls = calls
                    .Select((call, index) => new ToolCall($"call_{index}", call.Name, call.ArgumentsJson))
                    .ToList(),
            });
        return this;
    }

    /// <summary>The common case: say this, and type this line.</summary>
    public ScriptedLlm Types(string speech, string line) =>
        Reply(speech, calls: [Tool(CompanionTools.Type, "line", line)]);

    /// <summary>One call, with one string argument.</summary>
    public static (string, string) Tool(string name, string argument, string value) =>
        (name, "{\"" + argument + "\":" + JsonSerializer.Serialize(value) + "}");

    public Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken,
        CallOptions? options = null)
    {
        return CompleteAsync(messages, cancellationToken, options, null);
    }

    /// <summary>
    /// Stream the reasoning in two pieces, the way the provider does, so the loop's handling of
    /// a half-written thought can be tested. Off by default: most tests are about what the reply
    /// said, not about when the player heard it.
    /// </summary>
    public bool StreamsReasoning { get; set; }

    /// <summary>Make the first call hang until it is cancelled, and remember that it was.</summary>
    public bool Hangs { get; set; }

    /// <summary>
    /// Fail every call up to and including this one, so a test can watch an outage from the inside:
    /// the pause, the lines said while it lasts, and the catch-up when it is over (04_companion.md §10 (`C56`–`C60`)).
    /// Zero is a model that always answers.
    /// </summary>
    public int FailsUntilCall { get; set; }

    /// <summary>Raised the moment that hanging call is in flight.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Did the call in flight get cancelled rather than answered?</summary>
    public bool Cancelled { get; private set; }

    public async Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken,
        CallOptions? options,
        Func<string, Task>? onReasoning)
    {
        lock (_requests)
        {
            _requests.Add(messages);
            _options.Add(options);
        }

        if (FailsUntilCall > 0 && Requests.Count <= FailsUntilCall)
        {
            throw new LlmRetryableException($"the model is unreachable (call {Requests.Count})");
        }

        var reply = _replies.Count > 0
            ? _replies.Dequeue()
            : new ChatResult(string.Empty, Usage.Empty, "scripted", FinishReason.Stop);

        // The first call waits to be cancelled rather than answering, so a test can prove that
        // something done to the companion mid-thought ends the thought instead of queueing behind it.
        if (Hangs && Requests.Count == 1)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        if (StreamsReasoning && onReasoning is not null && !string.IsNullOrEmpty(reply.Reasoning))
        {
            var half = reply.Reasoning.Length / 2;
            await onReasoning(reply.Reasoning[..half]).ConfigureAwait(false);
            await onReasoning(reply.Reasoning[half..]).ConfigureAwait(false);
        }

        return reply;
    }

    /// <summary>Everything the model was told last time, flattened — for asserting on the prompt.</summary>
    public string LastPrompt =>
        Requests.Count == 0
            ? string.Empty
            : string.Join("\n", Requests[^1].Select(message => message.Content));
}

/// <summary>A writer the test can read while the loop is still writing to it.</summary>
internal sealed class CapturingWriter : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _text.Append(value).Append('\n');
        }
    }
}

/// <summary>
/// A seat the test can talk to while the companion works: a line handed over arrives at once, and a
/// read with nothing waiting blocks.
/// <para>
/// A fixed inbox cannot show what happens to work that is already in flight — every line is
/// available at once and the loop reads them in order — and "already in flight" is precisely the
/// case the companion gets wrong. This is the same shape as the live pipe, where the core's next
/// line arrives while the previous one is still being answered.
/// </para>
/// </summary>
internal sealed class OpenSeat : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    /// <summary>Hand the companion a line.</summary>
    public void Say(string line) => _lines.Writer.TryWrite(line);

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}

/// <summary>
/// One running companion: a scripted model, a seat fed from a string, and everything it says
/// captured. This is the seam the loop never had.
/// <para>
/// Most of the companion's behavioural rules — move only when asked, do not talk over your own
/// question, type it when the player hands you the keyboard — live in the loop rather than in
/// any parser, so the only honest way to test them is to run the loop with a model in it. The
/// alternative, which is what was happening, is to play for twenty minutes, read the log
/// afterwards, and decide it looked about right.
/// </para>
/// </summary>
internal sealed class LoopHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CapturingWriter _out = new();
    private readonly Task _running;
    private readonly string _state;
    private readonly AgentLoop _loop;

    private LoopHarness(ILlmClient? llm, TextReader inbox, AgentConfig config, string? gamesDirectory = null, Action<string>? prepare = null)
    {
        _state = Path.Combine(
            Path.GetTempPath(),
            "infocompanion-loop-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_state);
        prepare?.Invoke(_state);

        var log = AgentLog.Start(Path.Combine(_state, "logs"), keep: 1);
        var seat = new SeatConnection(inbox, _out);
        var loop = new AgentLoop(
            seat,
            config,
            log,
            _state,
            "You are a companion.",
            llm,
            gamesDirectory: gamesDirectory);
        _loop = loop;
        _running = Task.Run(() => loop.RunAsync(_cts.Token));
    }

    /// <summary>
    /// The voice-and-vote machine as the *running loop* has left it, for the facts that are about
    /// wiring rather than about the state machine: what the exchange's quiet clock counts from.
    /// </summary>
    public ArbitrationState Arbitration => _loop.Arbitration;

    /// <summary>Start a companion and feed it these envelopes, in order.</summary>
    public static LoopHarness Start(ILlmClient? llm, params string[] inbox) =>
        new(llm, new StringReader(string.Join('\n', inbox)), new AgentConfig());

    /// <summary>
    /// Start a companion on a seat the test keeps hold of, so it can be given a line *while* it is
    /// working rather than only before it starts.
    /// </summary>
    public static LoopHarness StartOn(ILlmClient? llm, OpenSeat seat) =>
        new(llm, seat, new AgentConfig());

    /// <summary>
    /// The same, with settings of its own. A tick fires every five seconds, so the only way to
    /// watch what a tick does — or refuses to do — is to make one due almost at once, which is how
    /// the hold in E5 is tested rather than argued.
    /// </summary>
    public static LoopHarness StartOn(ILlmClient? llm, OpenSeat seat, AgentConfig config) =>
        new(llm, seat, config);

    /// <summary>
    /// Start a companion that can see a games directory, so a game's own sidecar is found.
    /// </summary>
    public static LoopHarness StartWithGames(
        ILlmClient? llm,
        string gamesDirectory,
        params string[] inbox) =>
        new(llm, new StringReader(string.Join('\n', inbox)), new AgentConfig(), gamesDirectory);

    /// <summary>
    /// Start with the state directory prepared first — so a test can seed a record the
    /// companion will search, before any call can look for it.
    /// </summary>
    public static LoopHarness StartPrepared(ILlmClient? llm, Action<string> prepare, params string[] inbox) =>
        new(llm, new StringReader(string.Join('\n', inbox)), new AgentConfig(), null, prepare);

    /// <summary>
    /// A seat the test keeps hold of, settings of its own, and a state directory prepared first —
    /// what an outage needs, because it is timed and the catch-up reads the record (04_companion.md §10 (`C56`–`C60`)).
    /// </summary>
    public static LoopHarness StartOnPrepared(
        ILlmClient? llm,
        OpenSeat seat,
        AgentConfig config,
        Action<string> prepare) =>
        new(llm, seat, config, null, prepare);

    public string Output => _out.Text;

    /// <summary>The state directory the loop is using, so a test can seed the record.</summary>
    public string StateDirectory => _state;

    /// <summary>The notebook the loop is tending, so a rewrite can be read back.</summary>
    public string NotebookText
    {
        get
        {
            var path = Path.Combine(_state, "notebook", "unknown.md");
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
    }

    /// <summary>
    /// Wait until the loop has said something matching, or give up and hand back what it did say
    /// — so a timed-out expectation fails as a readable assertion rather than a hang.
    /// </summary>
    public async Task<string> UntilAsync(Func<string, bool> until, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var text = _out.Text;
            if (until(text))
            {
                return text;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return _out.Text;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The loop, with a model in it (10_delivery.md §5 (`D14`)), and the tool surface (04_companion.md §2 (`C6`–`C13`)).
/// <para>
/// The tools are the part of the contract that used to be tag markup, so what these tests are
/// really guarding is that the two channels stay separate: a call is not speech, speech is not a
/// call, and neither of them is parsed for the other.
/// </para>
/// </summary>
public class AgentLoopTests
{
    // Three dollars, because the envelope itself ends in two closing braces: with two the
    // trailing `}}` reads as an escape and the literal will not compile.
    private static string Handover(string line, ulong seq = 1) =>
        $$$"""{"v":1,"type":"handover","seq":{{{seq}}},"ts":"now","payload":{"line":"{{{line}}}"}}""";

    /// <summary>A framed turn, as the core sends it.</summary>
    private static string Turn(ulong n, string input) =>
        $$$"""{"v":1,"type":"turn_complete","seq":{{{n + 100}}},"ts":"now","payload":{"input":"{{{input}}}","output":"You are in a room.","elapsed_ms":4,"turn":{{{n}}},"awaiting_command":true,"machinery":false}}""";

    /// <summary>
    /// A framed turn carrying the interpreter's own status line, spliced in as raw JSON so that one
    /// fixture covers both shapes it arrives in: the numbers, for a story whose interpreter keeps
    /// the bar, and the absences a story that keeps its own bar sends instead (02_game_boundary.md §4.3
    /// (<c>G23</c>), §7 (<c>G38</c>)).
    /// </summary>
    private static string StatusTurn(ulong n, string status) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{"
        + "\"input\":\"look\",\"output\":\"You are in a room.\",\"elapsed_ms\":4,\"turn\":" + n
        + ",\"awaiting_command\":true,\"machinery\":false,"
        + status
        + "}}";

    /// <summary>
    /// A turn in a named room, with the interpreter's own numbers — enough to fill the block that
    /// says where a reply stands (the room, its name, the clock, the score).
    /// </summary>
    private static string NamedStatusTurn(ulong n, string name, string status) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{"
        + "\"input\":\"look\",\"output\":" + JsonSerializer.Serialize(name + "\nYou are here.")
        + ",\"elapsed_ms\":4,\"turn\":" + n
        + ",\"awaiting_command\":true,\"machinery\":false,"
        + status
        + "}}";

    /// <summary>Something the player said.</summary>
    private static string PlayerLine(string text, ulong seq = 900) =>
        $$$"""{"v":1,"type":"chat","seq":{{{seq}}},"ts":"now","payload":{"speaker":"player","text":"{{{text}}}"}}""";

    /// <summary>
    /// The story's parser words, which is also where the core states the game's Z-machine version
    /// — the fact that says whether the interpreter keeps the status line at all
    /// (02_game_boundary.md §4.3 (<c>G23</c>)).
    /// </summary>
    private static string Vocabulary(int zVersion, ulong seq = 953) =>
        "{\"v\":1,\"type\":\"vocabulary\",\"seq\":" + seq + ",\"ts\":\"now\",\"payload\":{"
        + "\"game_stem\":\"wishbringer\",\"version\":" + zVersion
        + ",\"resolution\":6,\"words\":[\"look\",\"take\",\"north\"]}}";

    /// <summary>The player sitting down.</summary>
    private static string PlayerArrives(ulong seq = 950) =>
        $$$"""{"v":1,"type":"system","seq":{{{seq}}},"ts":"now","payload":{"event":"player_present","detail":"they sat down"}}""";

    /// <summary>A system line, as the core sends one — `/thinkoff` and the risk level arrive here.</summary>
    private static string System(string @event, string detail, ulong seq = 951) =>
        $$$"""{"v":1,"type":"system","seq":{{{seq}}},"ts":"now","payload":{"event":"{{{@event}}}","detail":"{{{detail}}}"}}""";

    /// <summary>
    /// The handshake, with the attempt facts the core now sends (02_game_boundary.md §6 (`G27`–`G34`)). `resumed` is what
    /// makes it a boot that came back to a position rather than one that started the story.
    /// </summary>
    private static string Welcome(bool resumed, ulong seq = 1) =>
        $$$"""{"v":1,"type":"welcome","seq":{{{seq}}},"ts":"now","payload":{"game_title":"Wishbringer","game_stem":"wishbringer","turn_count":0,"transcript_tail":[],"agent_name":"Quill","run":3,"generation":2,"resumed":{{{resumed.ToString().ToLowerInvariant()}}},"resumed_from":"wishbringer.sav"}}""";

    // -- the boot, and what it says about itself (02_game_boundary.md §6 (`G27`–`G34`) and 05_memory.md §5 (`M23`–`M26`)) ----------------------

    [Fact]
    public async Task A_resumed_boot_says_once_that_the_notes_may_be_older_than_the_screen()
    {
        // The fifteen minutes that opened the 2026-09-17 evening: a companion reading a note
        // written on the wrong side of a reload and asserting it over a screen that contradicted
        // it. Said once, at the first reply — a standing instruction to check the screen is
        // boilerplate by the third call, which is how the last standing constraint came to be
        // obeyed two turns late.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: true), PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("Evening"));

        var constraint = llm.Requests[0][^1].Content;
        Assert.Contains("come back to a position", constraint);
        Assert.Contains("attempt 3", constraint);
        Assert.Equal(1, constraint.Split("come back to a position").Length - 1);
    }

    [Fact]
    public async Task A_fresh_boot_says_nothing_about_coming_back()
    {
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("Evening"));

        Assert.DoesNotContain("come back to a position", llm.Requests[0][^1].Content);
    }

    // -- the game asking, on its own screen (issue #19; found still broken 2026-09-19) --------------

    /// <summary>
    /// A turn the story *drew* rather than printed: no output at all, the question living only in
    /// the screen the interpreter handed over.
    /// <para>
    /// Built by concatenation rather than as a raw literal on purpose. The screen is real text with
    /// real newlines in it, and every hand-rolled version of this envelope ends up one brace short
    /// of a JSON string that parses.
    /// </para>
    /// </summary>
    private static string ScreenTurn(ulong n, string input, string screen) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{"
        + "\"input\":" + JsonSerializer.Serialize(input)
        + ",\"output\":\"\",\"elapsed_ms\":0,\"turn\":" + n
        + ",\"awaiting_command\":false,\"machinery\":false"
        + ",\"screen\":" + JsonSerializer.Serialize(screen)
        + "}}";

    /// <summary>A turn that came back to nothing at all: no output and no screen.</summary>
    private static string BlankTurn(ulong n) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{"
        + "\"input\":\"\",\"output\":\"\",\"elapsed_ms\":0,\"turn\":" + n
        + ",\"awaiting_command\":false,\"machinery\":false}}";

    /// <summary>Everything the companion was shown across every call it made.</summary>
    private static string ShownToTheModel(ScriptedLlm llm) =>
        string.Join("\n", llm.Requests.Select(request => request[^1].Content));

    [Fact]
    public async Task A_question_drawn_on_the_screen_reaches_the_companion_and_is_answered()
    {
        // border_zone's chapter menu, 2026-09-19: five turns running with output_bytes 0 and the
        // menu on the interpreter's screen the whole time. The companion was told "the game showed:
        // (no output)", reasoned out loud that the game was printing nothing at all, and tried
        // `xyzzy` at it. The screen was never in the payload this type deserialised.
        var llm = new ScriptedLlm().Types("That's the chapter menu — pressing 1.", "1");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            ScreenTurn(
                4,
                "",
                "The chapters are:\n   Chapter 1: The Train\n   Chapter 2: The Border\n"
                + "Which chapter would you like to play: 1, 2, 3, or (R)estore?"));

        await harness.UntilAsync(text => text.Contains("pressing 1"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("Which chapter would you like to play", shown);
        Assert.Contains("Chapter 1: The Train", shown);
        Assert.DoesNotContain("(no output)", shown);

        // And it was treated as the game *asking*, not as an ordinary turn to remark on.
        var log = ReadAgentLog(harness);
        Assert.Contains("\"kind\":\"game_prompt\"", log);
        Assert.Contains("\"screen_bytes\":", log);
    }

    [Fact]
    public async Task A_form_drawn_on_the_screen_reaches_the_companion()
    {
        // bureaucracy's licence form, 2026-09-19. The output there was a run of [LOW-PITCHED BEEP]
        // and the form was in none of it: the companion typed a name into a field it could not see
        // and then reported, honestly, that it had "never actually seen the form drawn".
        var llm = new ScriptedLlm().Reply("That's the licence form, and I can read it.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            ScreenTurn(3, "", "SOFTWARE LICENCE APPLICATION\nLast name:\nFirst name:"));

        await harness.UntilAsync(text => text.Contains("licence form"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("SOFTWARE LICENCE APPLICATION", shown);
        Assert.Contains("Last name:", shown);
    }

    [Fact]
    public async Task A_turn_that_showed_nothing_at_all_is_still_not_a_question()
    {
        // The rule the fix replaces, and the reason it has to survive: a switch leaves a turn framed
        // by quiescence with no input, no output and no screen, and asking the companion to answer
        // *that* is what produced a guess at a prompt it could not see. Silence stays silence —
        // the change is only that "showing nothing" now means the screen was empty too.
        var llm = new ScriptedLlm().Reply("Nothing to say.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), BlankTurn(4));
        await harness.UntilAsync(_ => false, timeoutMs: 300);

        Assert.DoesNotContain("\"kind\":\"game_prompt\"", ReadAgentLog(harness));
    }


    // -- the delegated errand, and the halt (04_companion.md §6 (`C42`–`C47`)) ----------------------------------------

    /// <summary>What the player's box begins with, which is the only bit of a half-written line the companion is given.</summary>
    private static string Composing(bool held) =>
        "{\"v\":1,\"type\":\"player_composing\",\"seq\":960,\"ts\":\"now\",\"payload\":{\"starts_with_space\":"
        + (held ? "true" : "false") + "}}";

    [Fact]
    public async Task An_errand_the_player_set_is_not_rationed_in_steps()
    {
        // The 2026-09-19 evening, where identical ground took 9 errands to walk the first time and
        // 21 the second: every eighth step was a hard stop, and six of the player's lines that
        // evening were literally "keep going". A step limit cannot tell the player's own
        // instruction from the companion's own idea, and it is only the second that needs one.
        var llm = new ScriptedLlm().Types("On it.", "south");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            PlayerLine("scout the open exits and report back"));

        await harness.UntilAsync(text => text.Contains("\"line\":\"south\""));

        var log = ReadAgentLog(harness);
        Assert.Contains("delegated by the player", log);
        Assert.Contains("\"budget\":10000", log);
    }

    [Fact]
    public async Task A_step_is_asked_for_a_call_where_a_reply_in_front_of_the_player_is_not()
    {
        // The claim is the request, not the wording. Measured across six sittings on 2026-10-01:
        // 150 steps of an errand, 26 of them prose with `finish_reason: stop` (17.3%), and every one
        // repaired — the harness asked again and that reply always carried the move, so the price was
        // a whole prompt each time and a player watching a companion announce a move and sit there.
        // The step's brief has said "words on their own are not a step" since 2026-09-21 and the rate
        // did not move, which is what makes this the request's business (`C23.8`).
        // The rule is drawn exactly at the step: a reply in front of the player may be speech alone.
        // Two replies, because the errand has two passes: taking the job, and the step the turn
        // after it asks for.
        var llm = new ScriptedLlm()
            .Types("On it.", "south")
            .Types("Carrying on.", "south");

        var inbox = new[]
        {
            PlayerLine("scout the open exits and report back"),
            Turn(1, "south"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);

        await harness.UntilAsync(text => text.Contains("Carrying on."));

        var requests = llm.Requests;
        var options = llm.Options;
        Assert.NotEmpty(requests);
        Assert.Equal(requests.Count, options.Count);

        var steps = 0;
        for (var index = 0; index < requests.Count; index++)
        {
            var prompt = string.Join("\n", requests[index].Select(message => message.Content));
            if (prompt.Contains("Take the next step", StringComparison.Ordinal))
            {
                steps++;
            }

            // The rule, as it has to be written after the provider had its say: a call may only be
            // *required* where thinking is explicitly off, because this backend answers the
            // combination with `HTTP 400: Thinking mode does not support this tool_choice` — fatal,
            // not a quiet fall back to prose, and measured live: the agent paused model calls for two
            // minutes and the companion went silent mid-evening. So what is asserted here is the
            // implication rather than the equality: nothing but a step asks for a call, and a step
            // only does when its own thinking is off.
            var asked = options[index]?.RequireATool ?? false;
            Assert.False(
                asked && !prompt.Contains("Take the next step", StringComparison.Ordinal),
                $"request {index} asks for a call but is not a step: the requirement is the step's alone");
            Assert.False(
                asked && options[index]?.Thinking != false,
                $"request {index} requires a call with thinking on, which the provider refuses");
        }

        // And in this harness every pass thinks, so no step could be forced — the guard is what is
        // being tested here, and the forced request itself is tested in `LlmClientTests`.
        Assert.True(steps > 0, "the errand never took a step, so this test proved nothing");
    }

    [Fact]
    public async Task A_line_held_with_a_space_pauses_the_errand_and_letting_go_resumes_it()
    {
        // A hold is the player's hand held up, not a cancellation. It used to end the errand where
        // it stood, on the argument that a hold is what the player has instead of a step budget —
        // which read a hand held up as giving up, and killed work the player had asked for over a
        // character they typed into a box and then deleted. Measured on 2026-09-22: two of that
        // evening's five errands died that way, one of them after a single step, and nothing was
        // said to the player about it. What a hold does now is stop the *steps*: the work waits
        // where it stands, the companion may still talk, and the errand picks up on the step it was
        // on by itself once the space comes off the front of the line.
        // The hand goes up *first* here, before the line that asks for the work, and that is the
        // awkward case rather than the easy one: the line arrives while the player is holding, so
        // reading it, taking it in hand and saying something about it all have to happen without a
        // step — and there is no dropped call for the repair to come looking for, which is what
        // makes the reply queue this test's own. Exactly one reply is spent while the hand is up,
        // and the one after it belongs to the tick that carries the errand on.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Scouting around, then.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "scout the open exits and report back")]);
        llm.Types("On again, then.", "south");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(Composing(startsWithSpace: true));
        seat.Say(PlayerLine("scout the open exits and report back"));

        await harness.UntilAsync(_ => ReadAgentLog(harness).Contains("work in hand"), timeoutMs: 20_000);
        await harness.UntilAsync(_ => ReadAgentLog(harness).Contains("player_held"), timeoutMs: 20_000);
        await Task.Delay(250);

        var log = ReadAgentLog(harness);
        Assert.Contains("player_held", log);
        // Taken in hand while the hand was up: the space stops the acting, not the reading.
        Assert.Contains("work in hand", log);
        // And it is told why it is not moving, in the player's own terms rather than as a policy:
        // the pass that read the line carried the hold, and the hold's words are the ones it read.
        Assert.Contains(
            "wait while I write this",
            string.Join("\n", llm.Requests[^1].Select(message => message.Content)));
        // Paused, not finished: the errand is in hand and nothing says otherwise.
        Assert.DoesNotContain("\"kind\":\"mission_end\"", log);
        var actsWithTheHandUp = harness.Output.Split("game_input").Length - 1;

        // A tick comes and goes with the line held, and a paused errand takes no step on it. What
        // takes the step is the tick's own `mission-step` pass, so the absence of that pass is the
        // whole claim — the errand is paused, and neither stalled nor over. `mission_step` itself is
        // written when a *turn* lands and no test seat invents a turn, so the pass is the thing to
        // count. Counted rather than asserted absent, because the claim is about the held window
        // and not about the order the script happened to be read in.
        var stepsWithTheHandUp = log.Split("\"purpose\":\"mission-step\"").Length - 1;
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.True(harness.Arbitration.IsHeld, "nothing released the hold");
        // The whole of the acting is what the hold stops, so this is every line and every key:
        // with the hand up, not one command reached the game, whoever was mid thought (04_companion.md §6 (`C42`–`C47`)).
        // Asserted with the two streams in the message rather than with a substring assert, because
        // the interesting half of this failure is *which* pass typed and what it thought it was
        // doing, and a window on the output cannot say that.
        Assert.True(
            harness.Output.Split("game_input").Length - 1 == actsWithTheHandUp,
            "a command reached the game with the line held\n"
            + "log:\n" + ReadAgentLog(harness) + "\noutput:\n" + harness.Output);
        Assert.Equal(
            stepsWithTheHandUp,
            ReadAgentLog(harness).Split("\"purpose\":\"mission-step\"").Length - 1);

        // And the space coming off is the whole of the release: the same errand, the next step on
        // the tick that follows, nothing re-declared and nothing asked of the player.
        seat.Say(Composing(startsWithSpace: false));
        await harness.UntilAsync(text => text.Contains("\"line\":\"south\""), timeoutMs: 20_000);

        var after = ReadAgentLog(harness);
        Assert.Contains("\"purpose\":\"mission-step\"", after);
        Assert.Equal(1, after.Split("\"kind\":\"mission_start\"").Length - 1);
        Assert.DoesNotContain("\"kind\":\"mission_end\"", after);

        // And one more hand up, this time over work that is already running. The pause is the
        // same pause, and what the log says about it names the errand that is standing still
        // rather than one that was shot for a character the player typed and then deleted.
        seat.Say(Composing(startsWithSpace: true));
        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("the errand waits where it stands"), timeoutMs: 10_000);
        Assert.Contains("the errand waits where it stands", ReadAgentLog(harness));
    }

    [Fact]
    public async Task A_player_line_arriving_mid_errand_does_not_call_the_errand_off()
    {
        // `C47.4`, measured on the same evening as the rest of this section. A remark typed while an
        // errand is running is answered — the player is owed an answer — and the errand stands where it
        // was: not restarted, which would read to the player as a new job replacing the old one, and not
        // ended, which is how two of that evening's errands died. What ends it is the player's own words
        // for it (`C48`) or the world changing under it (`C42.1`).
        //
        // The pass is the evidence for "answered": a line that reached the model cost one, and the
        // remark is the second. The reply's own words are deliberately not asserted — a tick can take a
        // queued reply, and a test that pinned which one would be pinning the script's order rather than
        // the behaviour.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Scouting around, then.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "scout the open exits and report back")]);
        llm.Reply("Fair enough.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("scout the open exits and report back"));

        await harness.UntilAsync(_ => ReadAgentLog(harness).Contains("work in hand"), timeoutMs: 20_000);
        var before = ReadAgentLog(harness);
        Assert.Equal(1, before.Split("\"kind\":\"mission_start\"").Length - 1);

        // The remark, mid-errand.
        seat.Say(PlayerLine("nice one"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 20_000);
        await Task.Delay(500);

        var after = ReadAgentLog(harness);
        Assert.True(llm.Requests.Count >= 2, "the remark never reached a pass, so nothing answered it");
        Assert.Equal(
            1,
            after.Split("\"kind\":\"mission_start\"").Length - 1);
        Assert.DoesNotContain("\"kind\":\"mission_end\"", after);
    }

    /// <summary>The system event the core sends when a restore has forked the world (`S69`).</summary>
    private static string Restored() =>
        """{"v":1,"type":"system","seq":902,"ts":"now","payload":{"event":"restore","detail":"wishbringer restored from forktest — the world forked (fork 2)"}}""";

    [Fact]
    public async Task A_restore_rebuilds_the_world_document_under_the_new_generation()
    {
        // `S69` forks the world on a restore, and the document has to be rebuilt underneath it: every
        // place and move carries the generation it was seen in, and a document left on the old one is a
        // map of a timeline the run has already disproved (`M13.2`) — which is the whole reason position
        // is stamped at all.
        //
        // Measured in the UAT of 2026-09-29: the core forked correctly and said so ("the world forked
        // (fork 2)"), the run book moved to generation 2, the record dropped 124 turns — and the
        // document still read `generation: 1, fork_turn: null`, because the fork was recorded and
        // nothing rebuilt the document. The failure is invisible from the game: the screen goes back
        // and the companion's map does not.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        await harness.UntilAsync(
            _ => Agent.Notebook.WorldState.Read(harness.StateDirectory, "wishbringer") is not null,
            timeoutMs: 20_000);

        // The run book as the core would have written it on the fork.
        var runs = Path.Combine(harness.StateDirectory, "runs");
        Directory.CreateDirectory(runs);
        File.WriteAllText(
            Path.Combine(runs, "wishbringer.json"),
            """{"count":1,"current":{"number":1,"started":"2026-09-29T21:28:23Z","generation":2}}""");

        seat.Say(Restored());

        await harness.UntilAsync(
            _ => (Agent.Notebook.WorldState.Read(harness.StateDirectory, "wishbringer")
                    ?.Header.Generation ?? 0) == 2,
            timeoutMs: 20_000);

        var world = Agent.Notebook.WorldState.Read(harness.StateDirectory, "wishbringer");
        Assert.True(
            world?.Header.Generation == 2,
            "the document was not rebuilt after the fork\nlog:\n" + ReadAgentLog(harness));
    }

    [Fact]
    public async Task A_held_line_is_answered_and_the_move_inside_it_is_not()
    {
        // B1, decided 2026-09-21: the space locks the *acting*, not the talking. Before this the
        // hold was silence outright — no pass started at all — so a player who began a line with a
        // space and pressed return got nothing, and could not tell "listening, but not moving" from
        // "stuck" or "gone". Measured over the evening the complaint came from: 37 holds, 36
        // releases, and 15 of 44 errands ended by one.
        var llm = new ScriptedLlm().Types("Going north, then.", "north");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            Composing(held: true),
            PlayerLine("what do you make of the case?"));

        await harness.UntilAsync(text => text.Contains("\"type\":\"chat\""), timeoutMs: 20_000);
        await Task.Delay(250);

        var output = harness.Output;
        // It answered.
        Assert.Contains("\"type\":\"chat\"", output);
        // And it did not move, though the reply asked it to.
        Assert.DoesNotContain("game_input", output);
        Assert.Contains("tried to move when the turn was not its to move in", ReadAgentLog(harness));
        // The reason it was given is the player's own space, not the generic "this turn is theirs".
        Assert.Contains("wait while I write this", string.Join("\n", llm.Requests[^1].Select(m => m.Content)));
    }

    [Fact]
    public async Task The_hold_does_not_stop_a_player_line_being_answered_at_all()
    {
        // The other half of the same fix, and the one that was literally the complaint: with the
        // hold silencing everything, this pass never happened.
        var llm = new ScriptedLlm().Reply("The case is shut, and the curtain wants a coin.");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            Composing(held: true),
            PlayerLine("what do you make of the case?"));

        var output = await harness.UntilAsync(text => text.Contains("\"type\":\"chat\""), timeoutMs: 20_000);

        Assert.Single(llm.Requests);
        Assert.Contains("curtain wants a coin", output);
    }

    [Fact]
    public async Task The_idle_nudge_may_speak_while_the_line_is_held_and_may_not_move()
    {
        // B1's other half: the hold is not silence. This is the pass a player sees as "it is
        // listening, but it is not going to move until I say so" — and before the fix there was no
        // pass at all, so a held line and a stuck companion looked exactly the same.
        var config = new AgentConfig { IdleNudgeSeconds = 1 };
        var llm = new ScriptedLlm().Types("Still here. Taking my time.", "north");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            config,
            state =>
            {
                // A plan in the notebook, because a companion with no lead does not nudge (§10.6).
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "wishbringer.md"),
                    "# wishbringer\n\n## Current goals and plan\n\n- find the wishbringer\n\n## Notes\n\n- the lamp is in the kitchen\n");
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(Composing(startsWithSpace: true));
        await harness.UntilAsync(_ => harness.Arbitration.IsHeld);

        await harness.UntilAsync(text => text.Contains("Still here"), timeoutMs: 20_000);
        await Task.Delay(250);

        var output = harness.Output;
        Assert.Contains("Still here", output);
        Assert.DoesNotContain("game_input", output);
        Assert.Contains("wait while I write this", string.Join("\n", llm.Requests[^1].Select(m => m.Content)));
    }

    [Fact]
    public async Task A_switch_ends_the_errand_that_belonged_to_the_game_it_left()
    {
        // Measured in the 2026-09-20 UAT, from the transcript: an errand to walk across wishbringer
        // was still in hand three turns into bureaucracy. `/switch` clears the transcript, retargets
        // the notebook and reloads the brief, and nothing ended the mission — so the companion went
        // on stepping a goal that named a story that was no longer on the screen, and the errand's
        // brief kept saying so.
        var llm = new ScriptedLlm().Types("Scouting, then.", "south");
        llm.Types("Bureaucracy, then.", "north");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("scout the open exits and report back"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"south\""));

        var askedBefore = llm.Requests.Count;
        seat.Say(System("game_changed", "bureaucracy"));
        seat.Say(PlayerLine("what now?"));

        // Wait for the pass *after* the switch, not merely for the errand to end: the errand is
        // ended while the game is being changed, so the last request is still the old game's until
        // the new one has been asked. Asserting on the newest request without this waits on nothing.
        var output = await harness.UntilAsync(
            _ => llm.Requests.Count > askedBefore,
            timeoutMs: 20000);

        Assert.True(
            ReadAgentLog(harness).Contains("this errand belonged to wishbringer"),
            output);
        Assert.Contains("\"kind\":\"mission_end\"", ReadAgentLog(harness));

        // And the game the player moved to is played as itself: the brief it was asked with has the
        // new story's transcript in it, and the old errand's goal nowhere.
        var after = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.DoesNotContain("scout the open exits", after);
    }


    // -- the world forking under its feet (02_game_boundary.md §6 (`G28`) and 03_session.md §10 (`S69`)) ---------------------------------------

    [Fact]
    public async Task A_restore_tells_the_companion_once_that_the_world_forked()
    {
        // `/restore` puts the same attempt at the same game back at an earlier save. Everything
        // after that save did not happen, and the companion has to be told — it is the one thing
        // about a restore that cannot be read off the screen, because the screen looks ordinary.
        var llm = new ScriptedLlm().Reply("Right — the fork, then. Picking it up from the screen.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("restore", "wishbringer restored from incell — the world forked (fork 2)"),
            PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Picking it up from the screen"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("The world has just forked", shown);
        Assert.Contains("What does not hold is where you were", shown);
        Assert.Contains("the player has gone back to a save", shown);
    }

    [Fact]
    public async Task A_restore_the_player_caused_is_said_to_be_the_players()
    {
        // The criterion set on 2026-10-05: the companion may not name a save itself — `/restore
        // <name>` is typed by the player — as long as it understands that the player did it. A
        // restore with no line of ours in flight is the player's hands: the core's own `/restore`
        // (bare or named), or a forced `.restore`.
        var llm = new ScriptedLlm().Reply("Back at the save, then.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("restore", "wishbringer restored from 06-cinemadone — the world forked (fork 5)"),
            PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Back at the save, then."));

        var shown = ShownToTheModel(llm);
        Assert.Contains("the player has gone back to a save", shown);
        Assert.DoesNotContain("you went back to a save", shown);
    }

    [Fact]
    public async Task A_restore_the_companion_typed_is_attributed_to_the_companion()
    {
        // The other direction of the same fact, measured in the 2026-10-05 UAT: the companion
        // answered a death menu with `restore`, the note said "the player has gone back to a save",
        // and eight minutes later it told the player "it wasn't me" about its own restore. The
        // line still in flight is the tell: a `restore` of our own, else the player's.
        var llm = new ScriptedLlm()
            .Types("Going back to the save.", "restore")
            .Reply("Right — where were we?");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("get us out of this"));
        await harness.UntilAsync(text => text.Contains("Going back to the save."));

        // The world reloads while our `restore` is the line in flight.
        seat.Say(System("restore", "wishbringer was reloaded by a restore — the world forked (fork 2)"));
        await Task.Delay(250);

        seat.Say(PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Right — where were we?"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("you went back to a save", shown);
        Assert.DoesNotContain("the player has gone back to a save", shown);
    }

    [Fact]
    public async Task A_restore_hands_the_companion_which_save_came_back_and_where_it_was_taken()
    {
        // B7/S70. The core reads the record beside the save file and says which save it was and
        // where it was written, so the companion is handed the fact instead of being sent to the
        // screen for it on every single load — and the name is what the companion can take back to
        // the save's note for anything the line does not carry. What it still cannot know is what
        // it was carrying: no save records that.
        var llm = new ScriptedLlm().Reply("Back at the bridge, then.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("save_position", "the save 06-hat was taken at turn 210, in North of Bridge (score 44)"),
            System("restore", "wishbringer restored from 06-hat — the world forked (fork 4)"),
            PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Back at the bridge"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("The world has just forked", shown);
        Assert.Contains("the save 06-hat was taken at turn 210, in North of Bridge (score 44)", shown);
        Assert.Contains("What does not hold is what you were carrying", shown);
        Assert.DoesNotContain("What does not hold is where you were", shown);
    }

    [Fact]
    public async Task A_restore_with_no_position_still_says_it_does_not_know()
    {
        // The honest half is not thrown away: a save written before the record carried a position,
        // or one taken before the game printed anything, leaves the companion where it always was —
        // told to look, because nobody can tell it.
        var llm = new ScriptedLlm().Reply("Right, I'll look.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("restore", "wishbringer restored from incell — the world forked (fork 2)"),
            PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Right, I'll look"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("What does not hold is where you were", shown);
        Assert.DoesNotContain("is not a mystery", shown);
    }

    [Fact]
    public async Task A_position_does_not_leak_into_the_next_restore()
    {
        // The fact belongs to the restore it arrived with. A save taken somewhere else must not
        // inherit it, which is the same rule the fork note itself is held to.
        var llm = new ScriptedLlm()
            .Reply("Back at the bridge.", calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "look")])
            .Reply("Still here.", calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "look")]);
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("save_position", "the save 06-hat was taken at turn 210, in North of Bridge (score 44)"),
            System("restore", "wishbringer restored from 06-hat — the world forked (fork 4)"),
            PlayerLine("hello"),
            System("restore", "wishbringer restored from 02-boots — the world forked (fork 5)"),
            PlayerLine("and now?"));
        await harness.UntilAsync(text => text.Contains("Still here."));

        // The first restore's call was handed the save and its position; the second one's was not,
        // because this save's note says nothing about where it was taken.
        Assert.Contains("North of Bridge", ShownToTheModel(llm));
        Assert.Contains("06-hat", ShownToTheModel(llm));
        var second = llm.Requests[1][^1].Content;
        Assert.Contains("What does not hold is where you were", second);
        Assert.DoesNotContain("North of Bridge", second);
        Assert.DoesNotContain("06-hat", second);
    }

    [Fact]
    public async Task A_restore_of_the_same_game_does_not_leave_a_fork_note_for_the_next_one()
    {
        // A half-spent note is a note about the wrong game. The transcript and the turn mark are
        // reset on a switch for exactly this reason; the fork note has to go with them.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            System("restore", "wishbringer restored from incell — the world forked (fork 2)"),
            System("game_changed", "border_zone"),
            PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("Evening"));

        Assert.DoesNotContain("The world has just forked", ShownToTheModel(llm));
    }

    [Fact]
    public async Task A_restore_forgets_the_facts_of_the_world_it_replaced()
    {
        // Measured 2026-10-05, the closing minutes of the UAT: after every fork the companion was
        // still handed the replaced world's facts — the room the story had last named, the clock,
        // the last thing examined — and it told the player it was standing on the Pleasure Wharf
        // while it was actually back in the cell the boots were about to reach. The facts belong
        // to the branch that was just replaced: they go with the transcript, and the restored
        // world's first turn refills them.
        var llm = new ScriptedLlm().Reply("Right, I'll look.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            NamedStatusTurn(1, "Jail Cell", "\"location\":137,\"score\":41,\"moves\":221,\"hours\":20,\"minutes\":41"),
            System("restore", "wishbringer restored from incell — the world forked (fork 2)"),
            PlayerLine("where are we?"));
        await harness.UntilAsync(text => text.Contains("Right, I'll look"));

        var shown = ShownToTheModel(llm);
        Assert.Contains("What does not hold is where you were", shown);
        Assert.DoesNotContain("Jail Cell", shown);
        Assert.DoesNotContain("20:41", shown);
        Assert.Contains("The interpreter cannot say which room you are in", shown);
    }

    [Fact]
    public async Task A_restart_forgets_the_facts_of_the_finished_run()
    {
        // The same rule for `/restart`: the opening's world is not the finished run's, and the
        // room name, the clock and the score the last run printed are not the ones the companion
        // is standing in. The opening's first turn refills them.
        var llm = new ScriptedLlm().Reply("Taking stock.").Reply("Fresh start, then.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            NamedStatusTurn(1, "Jail Cell", "\"location\":137,\"score\":41,\"moves\":221,\"hours\":20,\"minutes\":41"),
            System("restart", "wishbringer started again from the top"),
            PlayerLine("off we go"));
        await harness.UntilAsync(text => text.Contains("Fresh start, then."));

        var shown = ShownToTheModel(llm);
        Assert.DoesNotContain("Jail Cell", shown);
        Assert.DoesNotContain("20:41", shown);
    }

    [Fact]
    public async Task The_transcript_attributes_the_companions_own_typed_lines()
    {
        // The fork clears the transcript, and the companion reads its own typed lines back out of
        // it. Rendered as "the game was asked", its own `restore` looked like somebody else's
        // command, and on 2026-10-05 it told the player "it wasn't me" about a restore it had
        // made itself. A line the companion typed is named as its own.
        var llm = new ScriptedLlm()
            .Types("Going north, then.", "north")
            .Reply("And the room agrees.");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("please go north"));
        await harness.UntilAsync(text => text.Contains("Going north, then."));

        // The turn the companion's own `north` produced comes back...
        seat.Say(Turn(1, "north"));
        await Task.Delay(250);

        // ...and the next pass reads it from the transcript.
        seat.Say(PlayerLine("and now?"));
        await harness.UntilAsync(text => text.Contains("And the room agrees."));

        var shown = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.Contains("you typed: north", shown);
        Assert.DoesNotContain("the game was asked: north", shown);
    }


// -- how these games behave (05_memory.md §4, `M15`) -------------------------------------------

    [Fact]
    public async Task The_companion_is_told_what_is_true_of_the_engines()
    {
        // Paid for on 2026-09-19: border_zone's chapter menu and bureaucracy's licence form were
        // both declared unplayable while the companion typed verbs at the first and an entire name
        // into the second. None of it is story-specific — a menu wants a key, a form is filled a
        // field at a time, and a game may be painted rather than printed — so it goes in front of
        // every call rather than being learned again every evening.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("Evening"));

        // The system message, because this is standing knowledge rather than a fact about this turn.
        var prompt = llm.Requests[0][0].Content;
        Assert.Contains("How these games behave", prompt);
        Assert.Contains("A menu is answered with a key, not a word", prompt);
        Assert.Contains("A form is filled in one field at a time", prompt);
        Assert.Contains("painted rather than printed", prompt);
    }


    /// <summary>The agent's own log, where the events the panes never see are written.</summary>
    private static string ReadAgentLog(LoopHarness harness)
    {
        var file = Directory.GetFiles(Path.Combine(harness.StateDirectory, "logs"), "agent-*.jsonl").First();
        return File.ReadAllText(file);
    }

    // -- quiet is a property of the exchange (04_companion.md §5 (`C29`–`C41`)) ---------------------------------------------

    [Fact]
    public async Task The_companions_own_words_and_question_move_the_quiet_clock()
    {
        // The state machine's own rule is unit-tested; this is the half that goes missing
        // silently — whether the *loop* tells it. A companion whose own line does not move the
        // clock reads its own speech as silence, which is issue #23 from the other end.
        var llm = new ScriptedLlm().Reply("Evening, Peter.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("Evening, Peter."));

        Assert.Equal("the companion's own words", harness.Arbitration.LastEventWas);
        Assert.True(
            harness.Arbitration.QuietFor < TimeSpan.FromSeconds(5),
            "its own words are not silence");
    }

    [Fact]
    public async Task A_question_leaves_the_clock_at_the_question()
    {
        var llm = new ScriptedLlm().Reply(
            "Evening. Where shall we start?",
            calls: [ScriptedLlm.Tool(CompanionTools.Ask, "text", "the trail, or the shore?")]);
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("hello"));
        await harness.UntilAsync(text => text.Contains("\"type\":\"ask\""));

        Assert.Equal("the companion's own question", harness.Arbitration.LastEventWas);
        Assert.True(harness.Arbitration.IsAwaitingAnswer);
        Assert.True(harness.Arbitration.QuietFor < TimeSpan.FromSeconds(5));
    }

    // -- a prompt that wants a key, not a command (02_game_boundary.md §4.2–§4.3 (`G20`–`G24`)) --------------------------------------

    [Fact]
    public async Task A_prompt_that_wants_a_key_is_answered_with_a_key_and_not_a_line()
    {
        // Measured, 2026-09-20: the brief for a prompt that is not a command prompt told the
        // companion to answer with `type` — *"exactly what you would type in reply"* — and `type` is
        // a line. On Bureaucracy's licence form, which is inside `read_char`, the first character of
        // that line answers the prompt and the rest are typed into the field: `Fillmore` became
        // `ILLMORE`. The brief now names the key, and the tool that sends one.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Pressing Return at that page.",
            calls: [ScriptedLlm.Tool(CompanionTools.PressKey, "key", "")]);
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            ScreenTurn(2, "", "Press any key to begin.\n"));

        await harness.UntilAsync(text => text.Contains("\"type\":\"game_input\""));

        var brief = llm.Requests[0][^1].Content;
        Assert.Contains("waiting for a **key**, not a command", brief);
        Assert.Contains("press_key", brief);
        Assert.Contains("A `type` line here is refused before it reaches the story", brief);
    }

    [Fact]
    public async Task A_line_the_harness_stopped_is_named_and_answered_again_with_a_key()
    {
        // The other half of the same rule: the core refuses the line rather than typing it into the
        // prompt, and a refusal nobody hears is a command the companion believes is still in flight.
        var llm = new ScriptedLlm();
        llm.Reply("Typing the name in full.", calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "Fillmore")]);
        llm.Reply(
            "Then I shall press it a key at a time.",
            calls: [ScriptedLlm.Tool(CompanionTools.PressKey, "key", "Fillmore")]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("get us past this form"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"Fillmore\""));

        seat.Say(
            System(
                "key_wanted",
                "nothing was sent: the game is waiting for a key rather than a command, and `Fillmore` "
                + "would have been typed into the prompt one character at a time."));

        var output = await harness.UntilAsync(text => text.Contains("\"key\":true"));

        Assert.Contains("\"key\":true", output);
        Assert.Contains("\"line\":\"Fillmore\"", output);

        // And it was told why, in the harness's own words, rather than left to guess.
        var second = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.Contains("nothing was sent", second);
        Assert.Contains("waiting for a single key rather than a command", second);

        // The refusal is in the record too, because the next UAT looks there.
        Assert.Contains("\"kind\":\"key_wanted\"", ReadAgentLog(harness));
    }

    // -- a story that has stopped moving (02_game_boundary.md §4.3 (`G24`)) ---------------------------------------------

    [Fact]
    public async Task A_stuck_session_asks_the_companion_to_reason_about_the_play()
    {
        // The harness can count that the play is repeating; it cannot do anything about it. The one
        // part of the system that can is the part allowed to think above the game, so the verdict
        // goes to the companion with the instruction that matters: this is about *how it is playing*,
        // and no amount of re-reading its notes about the story will explain it.
        var llm = new ScriptedLlm();
        llm.Reply("I've been going round in circles — let me look again.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(
            System(
                "stuck",
                "the harness reads this as stuck: counters+place+barren (3 of 4 signals). No further "
                + "checkpoints will be taken while this lasts."));

        await harness.UntilAsync(text => text.Contains("round in circles"));

        var asked = llm.Requests[^1][^1].Content;
        Assert.Contains("the harness reads this as stuck: counters+place+barren (3 of 4 signals)", asked);
        Assert.Contains("fact about how this is being played, not about the story", asked);
        Assert.Contains("Do not send the last command again", asked);
        Assert.Contains("meta_note", asked);
        Assert.Contains("\"kind\":\"stuck\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task A_stuck_verdict_stands_until_the_core_says_the_world_moved()
    {
        // Standing, not said once: the failure a one-shot note would produce is a companion that
        // forgot within a turn and went back to repeating itself. It is cleared by the core's own
        // fact (`unstuck`) rather than by the agent guessing that things look better.
        var llm = new ScriptedLlm();
        llm.Reply("Going round in circles.");
        llm.Reply("Trying the other door.");
        llm.Reply("On we go.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(System("stuck", "the harness reads this as stuck: place+repetition (2 of 4 signals)"));
        await harness.UntilAsync(text => text.Contains("round in circles"));

        // A turn that has not moved the world still carries the verdict.
        seat.Say(Turn(6, "north"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"north\""));
        var whileStuck = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.Contains("the harness reads this as stuck: place+repetition (2 of 4 signals)", whileStuck);

        // And one that has clears it: the world moved, so the note goes. A player's line rather than
        // a turn, because the companion has just spoken and the quiet clock rightly keeps it quiet
        // on the next turn — the point here is the *content* of the next call it does make.
        seat.Say(System("unstuck", "the world moved again: checkpoints are back on."));
        var askedBefore = llm.Requests.Count;
        seat.Say(PlayerLine("where are we?"));
        await harness.UntilAsync(_ => llm.Requests.Count > askedBefore, timeoutMs: 20000);
        var after = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.DoesNotContain("the harness reads this as stuck: place+repetition (2 of 4 signals)", after);
        Assert.Contains("\"kind\":\"unstuck\"", ReadAgentLog(harness));
    }

    // -- the player holding the companion with a leading space (03_session.md §6 (`S41`–`S43`)) ---------------------------

    [Fact]
    public async Task A_leading_space_holds_the_companion_and_taking_it_off_releases_it()
    {
        // The signal is one bit about a line that has not been sent. The loop's job is to hold
        // everything self-initiated while it is set — a tick is not something the harness can fire,
        // so what is asserted here is the state the loop keeps and the log it leaves.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            Composing(startsWithSpace: true));
        await harness.UntilAsync(_ => harness.Arbitration.IsHeld);

        Assert.True(harness.Arbitration.IsHeld);
        Assert.Contains("player_held", ReadAgentLog(harness));
        Assert.DoesNotContain("player_carried_on", ReadAgentLog(harness));
    }

    [Fact]
    public async Task While_the_player_holds_the_companion_housekeeping_waits()
    {
        // The whole of E5, watched: the tick is five seconds, so the settings make taking stock due
        // almost at once and the hold is set before the first one. Housekeeping is not talking — it
        // is a long model call writing notes for the companion's own benefit — so it waits for the
        // space to come off the front of the line (B1, 2026-09-21: the hold locks the acting and
        // the housekeeping, and not the speech).
        var config = new AgentConfig { ReconcileEveryTurns = 1, ReconcileMinQuietSeconds = 0 };
        var llm = new ScriptedLlm();
        llm.Reply("Taking stock.");
        llm.Reply("Still taking stock.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(Turn(1, "look"));
        seat.Say(Composing(startsWithSpace: true));
        await harness.UntilAsync(_ => harness.Arbitration.IsHeld);

        // A tick has come and gone by now, and taking stock was due on it.
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.True(harness.Arbitration.IsHeld, "nothing released the hold");
        Assert.DoesNotContain("\"purpose\":\"reconcile\"", ReadAgentLog(harness));

        // One backspace, and the companion carries on.
        seat.Say(Composing(startsWithSpace: false));
        await harness.UntilAsync(_ => ReadAgentLog(harness).Contains("\"purpose\":\"reconcile\""), timeoutMs: 12000);

        Assert.Contains("\"purpose\":\"reconcile\"", ReadAgentLog(harness));
        Assert.Contains("player_carried_on", ReadAgentLog(harness));
    }

    /// <summary>What the core sends when the line in the box begins with a space (03_session.md §6 (`S41`–`S43`)).</summary>
    private static string Composing(bool startsWithSpace, ulong seq = 952) =>
        $$$"""{"v":1,"type":"player_composing","seq":{{{seq}}},"ts":"now","payload":{"starts_with_space":{{{startsWithSpace.ToString().ToLowerInvariant()}}}}}""";
    [Fact]
    public async Task A_switch_calls_off_the_reply_in_flight_and_answers_in_the_new_game()
    {
        // Reported: "/switch called while Quill is thinking does not terminate its thought loop".
        // The loop is serial, so the game change was not even *seen* until the reply landed — the
        // companion finished a long turn about the game that had just been closed, and then typed
        // what that turn had decided into the new one. What is guarded here is the order of three
        // things: the call in flight is dropped, nothing it had decided reaches the game, and the
        // next pass is about the game the player is actually playing.
        var llm = new ScriptedLlm { Hangs = true };
        llm.Reply(
            "So the grate was never opened.",
            calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "open grate")]);  // dropped, never typed
        llm.Types("North it is — new story.", "north");                             // the new game's pass

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("so what about the grate?"));
        await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The player switches games *while* the reply is in flight.
        seat.Say(System("game_changed", "catapole"));
        await harness.UntilAsync(_ => llm.Cancelled, timeoutMs: 5000);

        seat.Say(PlayerLine("go north, then"));
        var said = await harness.UntilAsync(text => text.Contains("\"type\":\"game_input\""));
        var log = ReadAgentLog(harness);

        Assert.True(llm.Cancelled, "the call in flight should have been cancelled, not finished");

        // The thought was called off, and the game change was handled straight after it.
        Assert.Contains("reply_abandoned", log);
        Assert.Contains("\"to\":\"catapole\"", log);

        // Nothing the abandoned turn had decided was ever typed into a game.
        Assert.DoesNotContain("open grate", said);
        Assert.DoesNotContain("So the grate was never opened.", said);

        // And the pass that followed is a pass in the new game: the move the player asked for.
        Assert.Contains("north", said);

        // One call thrown away, one call for the new game. A third would mean something was re-asked
        // in the game nobody is playing any more.
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task The_core_repeating_the_current_game_does_not_call_off_a_reply()
    {
        // The core sends `game_changed` again whenever it learns the title from the game's own
        // opening text, with the stem unchanged. Nothing is in flight *about* that, so a reply that
        // is being written must survive it — otherwise a boot would throw away its first answer.
        var llm = new ScriptedLlm { Hangs = true };
        llm.Reply("Give me a moment.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));       // stem: wishbringer
        seat.Say(PlayerLine("hello"));
        await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        seat.Say(System("game_changed", "wishbringer"));
        await Task.Delay(200);

        Assert.False(llm.Cancelled, "the same game again is not a world change");
        Assert.DoesNotContain("reply_abandoned", ReadAgentLog(harness));
    }

    // -- the companion's lamp (03_session.md §7 (`S44`–`S45`)) --------------------------------------------------

    /// <summary>How many times a message type appears in what the companion has sent.</summary>
    private static int Count(string text, string type) =>
        text.Split($"\"type\":\"{type}\"").Length - 1;

    [Fact]
    public async Task The_lamp_is_lit_for_the_whole_of_a_turn_and_only_once()
    {
        // C2: the page lights a lamp from two facts rather than from bytes arriving, because a
        // byte-light says nothing in the seconds between handing the model the turn and hearing the
        // first word back — which are exactly the seconds in which a working companion and a hung
        // terminal look the same. Lit before the call leaves, out once the reply has been delivered,
        // and once each: the light is held by the core, so a second start would be a second light.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("hello"));
        var sent = await harness.UntilAsync(
            text => text.Contains("Evening.") && text.Contains("\"type\":\"turn_finished\""));

        var lit = sent.IndexOf("\"type\":\"turn_started\"", StringComparison.Ordinal);
        var spoke = sent.IndexOf("\"type\":\"chat\"", StringComparison.Ordinal);
        var out_ = sent.IndexOf("\"type\":\"turn_finished\"", StringComparison.Ordinal);

        Assert.True(lit >= 0, "the lamp was never lit");
        Assert.True(out_ >= 0, "the lamp was never put out");
        Assert.True(lit < spoke, "lit before the words arrive, or it says nothing about the wait");
        Assert.True(spoke < out_, "and still lit while the reply is delivered");
        Assert.Equal(1, Count(sent, "turn_started"));
        Assert.Equal(1, Count(sent, "turn_finished"));
    }

    [Fact]
    public async Task A_turn_with_two_model_calls_lights_the_lamp_once()
    {
        // One reply is several calls — a retrieval round here, the repair of a missed move
        // elsewhere — and a fact published per call would make the lamp strobe. Two calls, one
        // light, and the light is not interrupted between them.
        var llm = new ScriptedLlm();
        llm.Reply("hold on — let me look at the record", calls: [ScriptedLlm.Tool(CompanionTools.Recall, "query", "desk")]);
        llm.Reply("It has a drawer.");
        await using var harness = LoopHarness.Start(llm, Welcome(resumed: false), PlayerLine("what about the desk?"));
        var sent = await harness.UntilAsync(
            text => text.Contains("It has a drawer.") && text.Contains("\"type\":\"turn_finished\""));
        // Two calls at the least, and in fact three — the repair of a missed move joins in — which
        // is the whole point: the light does not flicker between them.
        Assert.True(llm.Requests.Count >= 2, $"the round really did happen ({llm.Requests.Count} calls)");

        Assert.Equal(1, Count(sent, "turn_started"));
        Assert.Equal(1, Count(sent, "turn_finished"));
        // The retrieval round is inside the light, not between two of them.
        Assert.True(
            sent.IndexOf("\"type\":\"turn_started\"", StringComparison.Ordinal)
            < sent.IndexOf("\"type\":\"turn_finished\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pass_that_makes_no_call_lights_nothing()
    {
        // A tick is an invitation to speak, not a turn: most of them decide to say nothing, and a
        // lamp lit for those would say "working" through an idle evening. Held, so that the tick is
        // certain to be one that takes no turn at all.
        var config = new AgentConfig { ReconcileEveryTurns = 1, ReconcileMinQuietSeconds = 0 };
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(Composing(startsWithSpace: true));
        await harness.UntilAsync(_ => harness.Arbitration.IsHeld);

        // A tick has come and gone by now, and it took no turn — E5's own test watches that.
        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.Empty(llm.Requests);
        Assert.DoesNotContain("\"type\":\"turn_started\"", harness.Output);
        Assert.DoesNotContain("\"type\":\"turn_finished\"", harness.Output);
    }

    [Fact]
    public async Task An_abandoned_reply_puts_the_lamp_out()
    {
        // The second half of the light is in a `finally`, and this is why: a pass called off because
        // the world changed underneath it — `/switch` while the companion is thinking — must not
        // leave a lamp on for the rest of the evening, saying "working" about nothing at all.
        var llm = new ScriptedLlm { Hangs = true };
        llm.Reply("About to be abandoned.", calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "open grate")]);
        llm.Reply("North it is — new story.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("so what about the grate?"));
        await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // In flight, and the light is on. Nothing has been put out yet.
        Assert.Equal(1, Count(harness.Output, "turn_started"));
        Assert.Equal(0, Count(harness.Output, "turn_finished"));

        seat.Say(System("game_changed", "catapole"));
        await harness.UntilAsync(_ => llm.Cancelled, timeoutMs: 5000);
        await harness.UntilAsync(text => text.Contains("\"type\":\"turn_finished\""), timeoutMs: 5000);

        Assert.Contains("reply_abandoned", ReadAgentLog(harness));
        Assert.Equal(1, Count(harness.Output, "turn_finished"));
    }

    // -- the model being down (04_companion.md §10 (`C56`–`C60`)) -------------------------------------------------

    [Fact]
    public async Task While_the_model_is_away_the_companion_says_so_and_stops_calling_it()
    {
        // The requester's own words: "quills screen displays random messages about being away".
        // Three failures make the pause real, and inside a pause every trigger refuses to call, so
        // the only thing the companion says is one honest line about it — the first one when the
        // pause begins, and one more on the slow rotation after it. What must not happen is
        // silence: from the keyboard, a companion that has stopped talking and a model that is
        // down look exactly alike.
        var config = new AgentConfig { AwayNoticeSeconds = 1 };
        var llm = new ScriptedLlm { FailsUntilCall = 99 };
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        // Three things said, three failures, and then the pause is real.
        seat.Say(PlayerLine("hello"));
        seat.Say(PlayerLine("are you there?"));
        seat.Say(PlayerLine("hello again"));

        var said = await harness.UntilAsync(text => text.Contains("cannot reach the model"), timeoutMs: 15000);
        Assert.Contains("cannot reach the model", said);
        var log = ReadAgentLog(harness);
        Assert.True(
            log.Contains("the model went away"),
            $"calls={llm.Requests.Count}\n{log}");

        // A tick comes round, and the away line comes with it.
        var away = await harness.UntilAsync(text => text.Contains("The game is all yours"), timeoutMs: 15000);
        Assert.Contains("The game is all yours", away);

        // And then nothing at all: inside the pause every trigger refuses to call, and the count
        // is the proof. A companion that kept calling a broken endpoint would spend the player's
        // money proving to nobody that it is still broken.
        var callsWhenPaused = llm.Requests.Count;
        await Task.Delay(TimeSpan.FromSeconds(11));
        Assert.Equal(callsWhenPaused, llm.Requests.Count);
        Assert.Contains("\"turns_missed\":0", ReadAgentLog(harness));
    }

    [Fact]
    public async Task The_window_the_model_missed_is_brought_up_to_date_when_it_comes_back()
    {
        // W2's other half. Two failures are an outage that never got as far as the pause — the
        // common case, a flap — and the game kept going through it: the core wrote the turns down
        // and the player said things. When the model answers again, the companion reads the window
        // back, in its own record's words, and writes its notes up to date before saying anything.
        var config = new AgentConfig { AwayNoticeSeconds = 1, CatchUpMaxTurns = 20 };
        var llm = new ScriptedLlm { FailsUntilCall = 2 };
        llm.Reply("Evening.", calls: [ScriptedLlm.Tool(CompanionTools.Ask, "text", "Shall I look?")]);
        llm.Reply(
            "Right — caught up.",
            calls: [ScriptedLlm.Tool(CompanionTools.WriteNotes, "markdown", "# wishbringer\n\n## Current goals and plan\n\n- the garage door is closed\n")]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            config,
            state =>
            {
                // What the core wrote down while the model was away: the turns the companion never
                // got to think about. Stamped with the attempt the welcome below declares, because
                // a record from an earlier attempt is a different world (02_game_boundary.md §6 (`G27`–`G34`)).
                var dir = Path.Combine(state, "observations");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(
                    Path.Combine(dir, "wishbringer.jsonl"),
                    [
                        """{"turn":3,"input":"look","output":"(garage)\nThe garage, like a car port.","fingerprint":"garage#aaaa","run":3,"generation":2}""",
                        """{"turn":4,"input":"north","output":"The garage door is closed.","fingerprint":"garage#aaaa","run":3,"generation":2}""",
                    ]);
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("hello"));               // call 1 fails: the outage begins here
        seat.Say(PlayerLine("the door is shut"));    // call 2 fails, and this line falls inside it
        seat.Say(PlayerLine("are you back?"));       // call 3 answers: the model is back

        await harness.UntilAsync(_ => ReadAgentLog(harness).Contains("model_back"), timeoutMs: 15000);
        Assert.True(
            ReadAgentLog(harness).Contains("model_back"),
            $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");

        // The catch-up is one pass of its own, and it carries the window: the turns the core wrote
        // down, and what the player said while it was away. Waited for by the event the pass writes
        // when it has *finished*, so a timeout is a missing pass rather than a slow one.
        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"kind\":\"catchup\""),
            timeoutMs: 20000);
        Assert.True(
            ReadAgentLog(harness).Contains("\"kind\":\"catchup\""),
            $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");

        var asked = llm.Requests
            .Select(messages => string.Join("\n", messages.Select(message => message.Content)))
            .FirstOrDefault(text => text.Contains("could not reach the model"));
        Assert.NotNull(asked);
        Assert.Contains("turn 3", asked);
        Assert.Contains("turn 4", asked);
        Assert.Contains("the door is shut", asked);
        Assert.Contains("Bring your notes up to date", asked);

        // And the notes really were written, which is the whole point of the pass.
        var notebook = Path.Combine(harness.StateDirectory, "notebook", "wishbringer.md");
        Assert.True(File.Exists(notebook), $"expected the notebook at {notebook}");
        Assert.Contains("the garage door is closed", File.ReadAllText(notebook));

        // Once, not once per tick: one finished catch-up in the record, counted by the event the
        // pass writes when it has actually read the window and brought the notes up to date.
        var catchups = ReadAgentLog(harness).Split("\"kind\":\"catchup\"").Length - 1;
        Assert.Equal(1, catchups);

        // And the recovery itself is one event per outage, not one per call: everything between the
        // first answer and the catch-up is the same recovery, and counting it three times would make
        // a report of the evening read three outages where there was one.
        var backs = ReadAgentLog(harness).Split("\"kind\":\"model_back\"").Length - 1;
        Assert.True(backs == 1, $"backs={backs}\n{ReadAgentLog(harness)}");
    }

    [Fact]
    public async Task A_long_silence_keeps_the_last_forty_lines_and_says_that_it_did()
    {
        // The other half of the window is what the player said, and it is capped too — by keeping the
        // *newest* lines, because the thread of an evening is at the end of it, and by saying which
        // part is missing, because a companion shown forty of forty-five with no sign of the rest
        // would answer the first thing they said and never mention the last.
        var config = new AgentConfig { AwayNoticeSeconds = 0, LlmFailurePauseSeconds = 30 };
        var llm = new ScriptedLlm { FailsUntilCall = 3 };
        llm.Reply("Right — caught up.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        // Three failures make the pause real, and inside a pause a line costs nothing: the rest of
        // these are recorded, not answered, which is what an outage looks like.
        for (var i = 1; i <= 45; i++)
        {
            seat.Say(PlayerLine($"line {i}"));
        }

        var log = await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("model call failed"),
            timeoutMs: 15000);
        Assert.True(ReadAgentLog(harness).Contains("model call failed"), log);

        // The pause is a real one (thirty seconds at the floor), so the way out of it is to wait for
        // it and then say one more thing the model can answer.
        await Task.Delay(TimeSpan.FromSeconds(31));
        seat.Say(PlayerLine("still there?"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"kind\":\"catchup\""),
            timeoutMs: 25000);
        Assert.True(
            ReadAgentLog(harness).Contains("\"kind\":\"catchup\""),
            $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");

        var asked = llm.Requests
            .Select(messages => string.Join("\n", messages.Select(message => message.Content)))
            .FirstOrDefault(text => text.Contains("could not reach the model"));
        Assert.True(asked is not null, $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");

        // Only the window itself is asserted on: the prompt also carries the transcript and the
        // floor, and a line that has fallen out of the window is still in the ordinary history —
        // which is the difference between "the window is capped" and "the companion forgot".
        var start = asked.IndexOf("What your friend said", StringComparison.Ordinal);
        var end = asked.IndexOf("Bring your notes", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, asked);
        var window = asked[start..end];
        var kept = window
            .Split('\n')
            .Where(line => line.StartsWith("> ", StringComparison.Ordinal))
            .ToArray();
        // Forty-five lines fell inside the outage — the first thing said was answered (it failed),
        // so the window starts at the second, and the last line is the one said after the pause — and
        // the window holds the *last* forty of them, saying which five it is not showing rather than
        // letting the evening read as if it started at line seven.
        var problems = new List<string>();
        if (!window.Contains("(only the last 40 of 45 things they said are here", StringComparison.Ordinal))
        {
            problems.Add("the count is not the honest one");
        }

        if (kept.Length != 40)
        {
            problems.Add($"{kept.Length} lines kept");
        }

        if (!kept.Contains("> line 7", StringComparer.Ordinal))
        {
            problems.Add("the oldest kept line is missing");
        }

        if (!kept.Contains("> still there?", StringComparer.Ordinal))
        {
            problems.Add("the newest line is missing");
        }

        if (kept.Contains("> line 6", StringComparer.Ordinal))
        {
            problems.Add("a line past the cap is still there");
        }

        Assert.True(
            problems.Count == 0,
            $"{string.Join("; ", problems)} | kept={kept.Length} "
                + $"first={kept.FirstOrDefault()} last={kept.LastOrDefault()}");
    }

    [Fact]
    public async Task A_flap_with_nothing_missed_is_not_worth_a_pass()
    {
        // The companion's own trying is not the game moving: two failed calls with no turn and
        // nothing said in between leave a window with nothing in it, and reading that window back
        // would be a model call to be told it is empty. Driven by the idle nudge, because that is
        // the one thing the companion does by itself — nothing else can fail twice in a row without
        // the game or the player having moved in between.
        var config = new AgentConfig { AwayNoticeSeconds = 0, IdleNudgeSeconds = 0 };
        var llm = new ScriptedLlm { FailsUntilCall = 2 };
        llm.Reply("Nothing for it yet.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            config,
            state =>
            {
                // A plan in the notebook, because a companion with no lead does not nudge (§10.6).
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "wishbringer.md"),
                    "# wishbringer\n\n## Current goals and plan\n\n- find the wishbringer, then the grue\n\n## Notes\n\n- the lamp is in the kitchen\n");
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());

        // Two ticks fail — nothing happens in between: no turn, and not a word said.
        var log = await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("model_back"),
            timeoutMs: 20000);
        var agentLog = ReadAgentLog(harness);
        Assert.True(agentLog.Contains("model_back"), $"calls={llm.Requests.Count}\n{agentLog}");
        Assert.Contains("\"turns_missed\":0", agentLog);
        Assert.Contains("\"player_lines\":0", agentLog);

        // And it does not spend a pass taking stock of an empty window.
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.DoesNotContain("\"purpose\":\"catchup\"", ReadAgentLog(harness));
    }

    // -- the write-up a returning player asked for (05_memory.md §6 (`M27`–`M36`)) ------------------------------

    [Fact]
    public async Task Asking_for_a_write_up_reads_everything_and_writes_one_page()
    {
        // The core tells the agent *what* was asked for and nothing else; the pass is the companion's
        // own, its own model call, and everything it is shown comes off the state volume — the same
        // files a person would read. What it must not do is move: a write-up is not a turn.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Written up.",
            calls:
            [
                ScriptedLlm.Tool(
                    CompanionTools.Report,
                    "markdown",
                    "## Where we are\n\nInside the well house.\n\n"
                    + "## What is left to do\n\n- The gully is the thread.\n\n"
                    + "## The map\n\n- End of road -> Well house (the kit is inside)\n"),
            ]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            new AgentConfig(),
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "wishbringer.md"),
                    "# wishbringer\n\n## Current goals and plan\n\n- the grate is the way down\n");
                Directory.CreateDirectory(Path.Combine(state, "observations"));
                File.WriteAllLines(
                    Path.Combine(state, "observations", "wishbringer.jsonl"),
                    [
                        """{"turn":1,"input":"look","output":"Well house\nYou are in a well house.","fingerprint":"Well house#aaaa","location":180}""",
                        """{"turn":2,"input":"go north","output":"Road\nThe mailbox is empty.","fingerprint":"Road#aaaa","location":181}""",
                    ]);
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(System("catchup", "wishbringer", seq: 960));

        var page = Path.Combine(harness.StateDirectory, "catchup", "wishbringer.html");
        var beside = Path.Combine(harness.StateDirectory, "catchup", "wishbringer.json");
        // Both files, not the page alone: they are written one after the other, and polling for the
        // first and then reading the second is a race the test lost the moment its timing changed.
        await harness.UntilAsync(_ => File.Exists(page) && File.Exists(beside), timeoutMs: 15000);
        Assert.True(File.Exists(page), $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");
        Assert.True(File.Exists(beside), $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");

        // The page is the harness's: the model's prose in tabs, and a map drawn from the record's own
        // locations (B4) rather than from the move list the model happened to write.
        var html = File.ReadAllText(page);
        Assert.Contains(">What is left to do</button>", html);
        Assert.Contains("<svg", html);
        Assert.Contains(">Well house<", html);
        Assert.Contains("the kit is inside", html);

        // The facts beside it are what the browser's list shows: which game, when, how many turns
        // the record held, and which model was paid.
        var facts = File.ReadAllText(beside);
        Assert.Contains("\"stem\": \"wishbringer\"", facts);
        Assert.Contains("\"turns\": 2", facts);
        // The model the endpoint reported, which for a scripted one is the script's own name.
        Assert.Contains("\"model\": \"scripted\"", facts);

        // Everything the harness holds was in the *write-up's* prompt — found by the block it
        // carries, because the welcome's own reply was a call too.
        var asked = llm.Requests
            .Select(messages => string.Join("\n", messages.Select(message => message.Content)))
            .FirstOrDefault(text => text.Contains("The notebook for wishbringer"));
        Assert.True(asked is not null, $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");
        Assert.Contains("the grate is the way down", asked);
        Assert.Contains("The turn record", asked);
        Assert.Contains("The mailbox is empty", asked);
        Assert.Contains("write up wishbringer as a document", asked);
        Assert.Contains("nobody is at the keyboard", asked);

        // And it never moved: a write-up is not a turn, whatever the reply contained.
        Assert.DoesNotContain("\"type\":\"game_input\"", harness.Output);
        Assert.Contains("\"purpose\":\"report\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task A_write_up_that_says_nothing_in_the_chat_is_not_announced_as_nothing()
    {
        // The page is the answer, and it arrives in the pane the player asked for it in — so the
        // plain line about nothing having come of it is false there (04_companion.md §3 (`C16.1`)).
        // Measured 2026-09-30: it arrived directly under the log's own row saying a 6,364-character
        // page had just been written. What makes it invisible to the guard is that everything the
        // model had to say went into the tool's argument, which is what the pass looks like from the
        // outside: no speech, no move.
        var llm = new ScriptedLlm();
        llm.Reply(
            calls:
            [
                ScriptedLlm.Tool(
                    CompanionTools.Report,
                    "markdown",
                    "## Where we are\n\nInside the well house.\n\n"
                    + "## What is left to do\n\n- The gully is the thread.\n\n"
                    + "## The map\n\n- End of road -> Well house (the kit is inside)\n"),
            ]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            new AgentConfig(),
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "wishbringer.md"),
                    "# wishbringer\n\n## Current goals and plan\n\n- the grate is the way down\n");
                Directory.CreateDirectory(Path.Combine(state, "observations"));
                File.WriteAllLines(
                    Path.Combine(state, "observations", "wishbringer.jsonl"),
                    [
                        """{"turn":1,"input":"look","output":"Well house\nYou are in a well house.","fingerprint":"Well house#aaaa","location":180}""",
                    ]);
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(System("catchup", "wishbringer", seq: 960));

        var page = Path.Combine(harness.StateDirectory, "catchup", "wishbringer.html");
        await harness.UntilAsync(_ => File.Exists(page), timeoutMs: 15000);
        await Task.Delay(300);

        // The page was written and it is the answer — so the harness must not tell the player that
        // nothing came of the pass that wrote it.
        Assert.Contains("the kit is inside", File.ReadAllText(page));
        Assert.DoesNotContain("Nothing came of that", harness.Output);
    }

    [Fact]
    public async Task A_write_up_survives_a_reply_that_only_gathers()
    {
        // Measured, 2026-09-20: the *second* write-up of an evening produced no page at all. The
        // reply carried a `saves()` call and no `report`, and a reply that reaches for anything
        // beyond `recall` and `read_notes` ends the pass — so the gathering was done, the notes
        // were corrected, and the page was never written. From the browser it read as "generate
        // does nothing when a report already exists", because a game being written up a second time
        // is exactly the one whose notes have gone stale enough to check first.
        var llm = new ScriptedLlm();
        llm.Reply(calls: [(CompanionTools.Saves, "{}")]);
        llm.Reply(
            calls:
            [
                ScriptedLlm.Tool(CompanionTools.Report, "markdown", "## Where we are\n\nIn the fog.\n"),
            ]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            new AgentConfig(),
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "wishbringer.md"),
                    "# wishbringer\n\n## Current goals and plan\n\n- get down the cliff\n");
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(System("catchup", "wishbringer", seq: 960));

        var page = Path.Combine(harness.StateDirectory, "catchup", "wishbringer.html");
        await harness.UntilAsync(_ => File.Exists(page), timeoutMs: 15000);

        Assert.True(File.Exists(page), $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");
        Assert.Equal(2, llm.Requests.Count);
        Assert.DoesNotContain("ended without a report", ReadAgentLog(harness));

        // `saves` is a *read*, so it no longer ends the pass: the round goes on, the save list is put
        // in front of the model, and the page is written in the same pass. This used to be a list of
        // two names (`recall`, `read_notes`) three frames away from the tool that disagreed with it,
        // and that disagreement was the whole of this bug.
        var second = string.Join(
            "\n",
            llm.Requests[1].Select(message => message.Content));
        Assert.Contains("saves", second, StringComparison.OrdinalIgnoreCase);

        // And the retry that used to repair this is not needed at all: one pass, one extra round.
        Assert.DoesNotContain("still has no write-up", second);
    }

    [Fact]
    public async Task A_write_up_that_never_writes_is_bounded_and_named()
    {
        // The other half: a pass that will not write must not be asked for ever, and must not
        // disappear. Three attempts, then a warning that says so (02_game_boundary.md §9 (`G46`–`G49`): failures are named).
        //
        // The *requests* are no longer the count to assert on: a read may loop for another round
        // (`LocalToolRounds`), so one attempt can be several calls. What is bounded — and what this
        // test is about — is the attempts, which the log names one by one.
        var llm = new ScriptedLlm();
        for (var i = 0; i < 20; i++)
        {
            llm.Reply(calls: [(CompanionTools.Saves, "{}")]);
        }

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(llm, seat, new AgentConfig(), _ => { });

        seat.Say(Welcome(resumed: false));
        seat.Say(System("catchup", "wishbringer", seq: 960));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("ended without a report"),
            timeoutMs: 20000);

        var log = ReadAgentLog(harness);
        Assert.Contains("ended without a report", log);
        Assert.Contains("\"attempts\":3", log);
        Assert.Equal(
            2,
            log.Split("the write-up pass is being asked again").Length - 1);
        Assert.False(File.Exists(Path.Combine(harness.StateDirectory, "catchup", "wishbringer.html")));

        // The first ask has to keep the checks available — the model reached for them, twice — and the
        // last offers nothing but the report, so a model that answers at all writes.
        Assert.Contains("saves", llm.Options[0]!.Tools!.Select(tool => tool.Name));
        Assert.Equal(new[] { "report" }, llm.Options[^1]!.Tools!.Select(tool => tool.Name));

        // And the failure is named where a person will look for it. The companion cannot report
        // this one — it has no way to know the harness gave up on its behalf — so the harness
        // writes it, into the file that exists for exactly this (02_game_boundary.md §9 (`G49`) and 05_memory.md §7 (`M39.2`)).
        // The note is written *after* the log line the wait above unblocked on, and `StreamWriter`
        // creates the file when it opens it and puts the lines in when it is flushed — so a read
        // here can catch a file that exists and is still empty. Reproduced on HEAD with no local
        // changes at all, 2026-09-22, by running two copies of this suite at once: the wait is for
        // the content, because the content is what the assertion is about.
        var notes = Path.Combine(harness.StateDirectory, "meta", "wishbringer.md");
        await harness.UntilAsync(
            _ => File.Exists(notes) && File.ReadAllText(notes).Contains("## harness —"),
            timeoutMs: 15_000);
        Assert.True(File.Exists(notes), ReadAgentLog(harness));
        var meta = File.ReadAllText(notes);
        Assert.Contains("## harness —", meta);
        Assert.Contains("ended without a report", meta);
    }

    [Fact]
    public async Task A_meta_note_is_kept_outside_the_world_and_never_read_back()
    {
        // The companion needs somewhere to put "the harness lost the world at the cliff" that is
        // neither the notebook nor the file describing the player (05_memory.md §7 (`M37`–`M39`)). Measured,
        // 2026-09-20: that exact kind of note was in the memories, where a model reading it in
        // another game next month finds a workaround for a harness defect presented as a fact
        // about the person it is playing with.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Noting that, in case it matters later.",
            calls:
            [
                (CompanionTools.MetaNote,
                    """{"note":"my note was ahead of a restore, not wrong","about":"harness"}"""),
                ScriptedLlm.Tool(CompanionTools.Type, "line", "north"),
            ]);
        llm.Types("On, then.", "wait");

        var inbox = new[] { PlayerLine("go on"), Turn(1, "north"), Turn(2, "wait") };
        await using var harness = LoopHarness.Start(llm, inbox);
        await harness.UntilAsync(_ => llm.Requests.Count >= 2);

        var path = Path.Combine(harness.StateDirectory, "meta", "unknown.md");
        Assert.True(File.Exists(path), ReadAgentLog(harness));
        var notes = File.ReadAllText(path);
        Assert.Contains("## harness —", notes);
        Assert.Contains("my note was ahead of a restore", notes);
        Assert.Contains("\"kind\":\"meta_note\"", ReadAgentLog(harness));

        // Kept out of the world, and not read back: the second request is a fresh turn with the
        // whole record in front of the model, and none of the note is in it. A note that could
        // steer a turn would be a second way into the game that nothing tests.
        var second = string.Join("\n", llm.Requests[1].Select(message => message.Content));
        Assert.DoesNotContain("my note was ahead of a restore", second);
    }

    // -- the goal belongs to whoever set it (04_companion.md §6 (`C42`–`C47`)) ------------------------------------

    [Fact]
    public async Task The_players_goal_is_not_replaced_by_the_companions_own_declaration()
    {
        // Measured on 2026-09-17: "keep going until you get to the fountain" was replaced by the
        // companion's own declaration five seconds later, with no step taken — and the walk to the
        // fountain became a trip back to the cell for a blanket. The goal is the player's; the
        // steps are the companion's.
        var llm = new ScriptedLlm().Types("Right, walking.", "north");
        llm.Reply(
            "I'll fetch the blanket first, if that's all right.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "go back to the cell for the blanket")]);

        await using var harness = LoopHarness.Start(
            llm,
            PlayerLine("keep going until you get to the fountain"),
            Turn(1, "north"));
        await harness.UntilAsync(text => text.Contains("fetch the blanket first"));

        var log = ReadAgentLog(harness);
        Assert.Contains("revised its plan inside", log);
        Assert.Contains("keep going until you get to the fountain", log);
        Assert.DoesNotContain("replaced by a new errand", log);
    }

    // -- the tool trace (03_session.md §4.3 (`S32`–`S34`)) --------------------------------------------------------
    [Fact]
    public async Task The_tool_trace_sends_one_line_per_call_when_it_is_on()
    {
        // Diagnosis, and the point of it is that a player can watch the companion reach. Off is
        // the default and off is free: the lines are not sent at all.
        var llm = new ScriptedLlm().Types("on it", "north");
        await using var harness = LoopHarness.Start(
            llm, System("debug", "on"), Handover("north"));
        await harness.UntilAsync(text => text.Contains("\"type\":\"game_input\""));

        Assert.Contains("\"type\":\"tool\"", harness.Output);
        Assert.Contains("\"name\":\"type\"", harness.Output);
        Assert.Contains("\"detail\":\"north\"", harness.Output);
    }

    [Fact]
    public async Task The_tool_trace_is_silent_until_it_is_asked_for()
    {
        var llm = new ScriptedLlm().Types("on it", "north");
        await using var harness = LoopHarness.Start(llm, Handover("north"));
        await harness.UntilAsync(text => text.Contains("\"type\":\"game_input\""));

        Assert.DoesNotContain("\"type\":\"tool\"", harness.Output);
    }

    // -- the money rule ---------------------------------------------------------------------

    [Fact]
    public async Task The_companion_stops_spending_once_its_allowance_is_spent()
    {
        // The money rule, on a server nobody switches off. It is not one runaway call: it is a
        // sequence — the companion reports, the report can carry another move, and nothing in
        // the loop ever counted. Measured live: four calls into a silence and then
        // `unattended_limit`, where the run before that fix spent about fifteen.
        //
        // The turn-completion passes are `allowAct: false` on purpose (that is the other half of
        // "it runs off and plays by itself"), so the chain cannot feed itself here — which is
        // exactly why this asserts on the *count* rather than on a chain that would never have
        // run anyway.
        var llm = new ScriptedLlm().Types("on it", "north");
        for (var i = 0; i < 12; i++)
        {
            llm.Reply("still here", calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "north")]);
        }

        var inbox = new List<string> { PlayerArrives(), Handover("north") };
        for (ulong turn = 1; turn <= 10; turn++)
        {
            inbox.Add(Turn(turn, "north"));
        }

        await using var harness = LoopHarness.Start(llm, inbox.ToArray());
        await harness.UntilAsync(text => text.Contains("game_input"));
        await Task.Delay(600);

        var limit = new AgentConfig().UnattendedCallLimit;
        Assert.True(
            llm.Requests.Count <= limit,
            $"the companion made {llm.Requests.Count} calls; the limit is {limit}");
        Assert.True(llm.Requests.Count >= 2, "the sequence did not even get started");
    }

    // There is deliberately no unit test for the *reset* half of the allowance, and the reason is
    // worth keeping: the counter can only grow through passes the companion starts itself, and the
    // two ways that happens here — the idle nudge and the report after its own move — need either
    // fifty seconds of quiet or a move it was allowed to make. Neither is reachable in this harness
    // at a sane speed, and a test that appeared to cover the reset while asserting nothing would be
    // worse than the gap. It is verified live instead: on 2026-09-14 a session with the player
    // connected and idle made four calls into a silence and then logged `unattended_limit`, where
    // the run before the fix spent about fifteen.
    //
    // The same goes for the two timing behaviours added on 2026-09-17 — the maintenance
    // allowance and the quiet gate in front of taking stock — and for a sharper reason: the
    // harness's inbox ends, the channel completes, and RunAsync returns before a single
    // five-second tick can fire. They are verified live (04_companion.md §5 (`C29`–`C41`)), where the ticker
    // is a real ticker.
    //
    // And the same for the split between work and the companion's own spending, added on
    // 2026-09-21. What that fix changes is *which calls the counter can see*, so the test would
    // have to make an errand of several steps and then wait out a silence before asserting — and
    // the wait is the part this harness cannot do. Verified live instead, and the log says it
    // plainly: `unattended_limit {"calls":29,"limit":4,"purpose":"goingincircles"}` on
    // 2026-09-21, where the counter could only reach 29 by counting the errand.

    // -- the handover, which is where a companion is allowed to say no ----------------------

    [Fact]
    public async Task A_handover_is_typed_with_the_line_the_player_asked_for()
    {
        var llm = new ScriptedLlm().Types("on it", "north");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("\"type\":\"game_input\"", output);
        Assert.Contains("\"line\":\"north\"", output);
    }

    // -- retrieval is hands, not hope (04_companion.md §3 (`C14`–`C25`)) ------------------------------------------

    [Fact]
    public async Task A_recall_call_is_answered_from_the_record_and_the_model_is_called_again()
    {
        var llm = new ScriptedLlm();
        llm.Reply("hold on — let me check what the game actually said", calls: [ScriptedLlm.Tool(CompanionTools.Recall, "query", "joystick")]);
        llm.Types("right — the stick did nothing, apparently", "look");

        await using var harness = LoopHarness.StartPrepared(
            llm,
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "observations"));
                File.WriteAllText(
                    Path.Combine(state, "observations", "unknown.jsonl"),
                    """{"turn":7,"input":"push joystick north","output":"Nothing exciting happens.","fingerprint":"Video Arcade#abcd"}"""
                    + "\n");
            },
            PlayerArrives(),
            PlayerLine("what did we try on the joystick?"));

        await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.True(llm.Requests.Count >= 2, "the retrieval round did not happen");
        var toolResult = llm.Requests.SelectMany(request => request).FirstOrDefault(message => message.Role == "tool");
        Assert.NotNull(toolResult);
        Assert.Contains("Nothing exciting happens", toolResult.Content);
    }

    [Fact]
    public async Task A_move_mixed_with_a_search_does_not_round_trip()
    {
        var llm = new ScriptedLlm().Reply(
            "checking, and going north",
            calls:
            [
                ScriptedLlm.Tool(CompanionTools.Recall, "query", "coin"),
                ScriptedLlm.Tool(CompanionTools.Type, "line", "north"),
            ]);

        await using var harness = LoopHarness.Start(llm, PlayerArrives(), PlayerLine("go north — and what about the coin?"));
        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("\"line\":\"north\"", output);
        Assert.DoesNotContain(llm.Requests.SelectMany(request => request), message => message.Role == "tool");
    }

    [Fact]
    public async Task An_update_notes_call_replaces_one_section_and_keeps_the_rest()
    {
        var llm = new ScriptedLlm().Reply(
            "noted — the coin's dead, then.",
            calls:
            [
                (CompanionTools.UpdateNotes, """{"section":"Open questions","markdown":"- the coin is no good for the arcade (the player says so)"}"""),
            ]);

        await using var harness = LoopHarness.Start(llm, PlayerArrives(), PlayerLine("the coin is no good for the arcade"));
        await harness.UntilAsync(text => text.Contains("noted"));
        await Task.Delay(300);

        var notebook = harness.NotebookText;
        Assert.Contains("the coin is no good for the arcade", notebook);
        Assert.Contains("## Open questions", notebook);
        // Everything else survives: a section edit does not rewrite the file.
        Assert.Contains("## Key facts", notebook);
        Assert.Contains("## Current goals and plan", notebook);
    }

    [Fact]
    public async Task A_handover_the_companion_refuses_types_nothing_at_all()
    {
        var llm = new ScriptedLlm().Reply("I would rather not — the grate is the only way in.");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        // The refusal is speech, so wait for the speech and then check that nothing followed it.
        await harness.UntilAsync(text => text.Contains("\"type\":\"chat\""));
        await Task.Delay(200);

        Assert.Contains("chat", harness.Output);
        Assert.DoesNotContain("game_input", harness.Output);
    }

    [Fact]
    public async Task The_handover_reaches_the_model_as_a_request_it_may_refuse()
    {
        // The whole reason this route exists rather than being /direct: passing the line through
        // the companion is slower, and the slowness buys one thing — a place for an objection to
        // arrive *before* the move instead of after it.
        var llm = new ScriptedLlm().Types("on it", "north");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        await harness.UntilAsync(_ => llm.Requests.Count > 0);

        Assert.Contains("asking you to type", llm.LastPrompt);
        Assert.Contains("north", llm.LastPrompt);
        Assert.Contains("refuse once", llm.LastPrompt);
    }

    [Fact]
    public async Task With_no_model_a_handover_is_still_typed()
    {
        // Typing is not a model's job, and leaving a player waiting on a brain that is not there
        // is worse than simply obeying.
        await using var harness = LoopHarness.Start(null, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("\"type\":\"game_input\"", output);
        Assert.Contains("\"line\":\"north\"", output);
    }

    [Fact]
    public async Task A_handover_with_no_line_is_ignored()
    {
        var llm = new ScriptedLlm().Reply("hmm");
        await using var harness = LoopHarness.Start(llm, Handover(""));

        await Task.Delay(250);

        Assert.Empty(llm.Requests);
        Assert.DoesNotContain("game_input", harness.Output);
    }

    [Fact]
    public async Task A_refusal_to_type_one_line_is_never_asked_again()
    {
        // Declining is the whole of what a refusal is, so the repair for a move that never
        // landed must keep its hands off this pass. The second reply is queued and types: if
        // the repair ever fires here it will be sent, and this test will say so.
        var llm = new ScriptedLlm()
            .Reply("I would rather not — the grate is the only way in.")
            .Types("oh, very well", "north");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        await harness.UntilAsync(text => text.Contains("\"type\":\"chat\""));
        await Task.Delay(300);

        Assert.Single(llm.Requests);
        Assert.DoesNotContain("game_input", harness.Output);
    }

    // -- the move that never lands ----------------------------------------------------------

    [Fact]
    public async Task A_reply_to_the_player_that_moves_nothing_is_asked_about_once()
    {
        // Measured live on 2026-09-14: "try the post office" produced "South out of the copse
        // first." with no call, then "Going." with no call, and the move only came when the
        // player typed a third time. The repair existed and skipped exactly this: it excluded
        // the passes the player caused, on the reasoning that a reply which moves nothing is a
        // legitimate answer to being spoken to. It is — but the model decides that, and it does
        // not decide it by speaking.
        var llm = new ScriptedLlm()
            .Reply("Right, back to the post office. South out of the copse first.")
            .Types("Fair — said I would go and did not. Going now.", "south");

        await using var harness = LoopHarness.Start(llm, PlayerLine("what about the post office?"));

        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("your last reply carried no move", llm.LastPrompt);
        Assert.Contains("make it now, and nothing else", llm.LastPrompt);
        Assert.Contains("\"line\":\"south\"", output);

        // And the miss is instrumented with what the reply carried, so the next evening's log
        // can say which failure it was — prose that forgot the call, a pass with nothing to do,
        // or a reply cut off before it reached the call (04_companion.md §12).
        await Task.Delay(250);
        var log = ReadAgentLog(harness);
        Assert.Contains("\"kind\":\"act_missed\"", log);
        Assert.Contains("\"speech_chars\":", log);
        Assert.Contains("\"thinking_chars\":", log);
        Assert.Contains("\"finish_reason\":\"stop\"", log);
        Assert.Contains("\"after_another_miss\":false", log);
    }

    [Fact]
    public async Task The_repairs_words_are_not_shown_when_it_decides_there_was_nothing_to_do()
    {
        // The repair is allowed to decide there was nothing to do. It is not allowed to make the
        // player read about it: the reply it was repairing has already been read, and a second
        // paragraph explaining the absence of a move is the harness talking about itself.
        var llm = new ScriptedLlm()
            .Reply("The stone is blank — no name, no dates.")
            .Reply("nothing to do");

        await using var harness = LoopHarness.Start(llm, PlayerLine("what about the stone?"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 2);
        await Task.Delay(300);

        Assert.Contains("The stone is blank", harness.Output);
        Assert.DoesNotContain("nothing to do", harness.Output);
        Assert.DoesNotContain("game_input", harness.Output);
    }

    [Fact]
    public async Task A_dropped_call_does_not_end_the_errand()
    {
        // Measured live on 2026-09-14: "keep going until you have given the dog the bone" started
        // an errand, its first step replied with prose and no call, and the errand was declared
        // over at step 0 — a second after it began, and before the repair had even been asked.
        // The move landed anyway, and the player spent the rest of the journey pushing it one
        // step at a time. A dropped call is not a report.
        var llm = new ScriptedLlm()
            .Reply("Errand taken. West to the cemetery, north to the grave.")
            .Types("My mistake — said west and did not type it. Going now.", "west")
            .Types("Gate again. North next.", "north");

        var inbox = new[]
        {
            PlayerLine("keep going until you reach the cemetery"),
            Turn(1, "west"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        await harness.UntilAsync(text => text.Contains("game_input"));
        await harness.UntilAsync(_ => llm.Requests.Count >= 3);
        await Task.Delay(200);

        // The turn that came back was read as a step of the errand, not as the report that ended
        // it: without the fix this pass is the talk-only report and the brief never appears.
        Assert.Contains("You are on an errand for the player", llm.LastPrompt);
    }

    [Fact]
    public async Task One_step_that_moves_nothing_leaves_the_errand_in_hand()
    {
        // Measured 2026-09-25, five times in one evening: the companion announced the move it was
        // about to make, typed nothing, the ask came back with nothing either, and the harness read
        // that *first* stutter as the report — the errand was over, the loop went quiet, and the
        // player was left to say "keep going" (once, to ask "why do stop after every meta_note()?").
        // A dropped call is a pause, the same reading as the hold: the tick takes the next step from
        // the step the errand stood on.
        var llm = new ScriptedLlm()
            .Types("Off the hill and into the cemetery.", "west")
            .Reply("That is the lot — nothing more to do out here.")
            .Reply("nothing to do")
            .Reply("Still here.")
            .Types("Right — north, then.", "north");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        // An open seat, not a fixed inbox: the step after the stall comes from the *tick*, and an
        // inbox that runs out of lines closes the seat and stops the loop with it.
        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("keep going until you reach the cemetery"));
        seat.Say(Turn(1, "west"));

        // The errand carried on by itself: the tick took the next step, and it reached the game.
        await harness.UntilAsync(text => text.Contains("\"line\":\"north\""), timeoutMs: 20_000);
        Assert.True(
            harness.Output.Contains("\"line\":\"north\""),
            $"the errand took no further step after the one that moved nothing\n"
            + $"calls: {llm.Requests.Count}\npane:\n{harness.Output}");

        // And the pass that moved was the errand's own step rather than a fresh conversation.
        Assert.Contains("You are on an errand for the player", llm.LastPrompt);
    }

    [Fact]
    public async Task An_errand_ends_after_two_consecutive_steps_that_move_nothing()
    {
        // The other half: the stutter is a pause, not a reprieve for ever. The second step in a row
        // that moves nothing, even after being asked, is the report — and the errand is over.
        var llm = new ScriptedLlm()
            .Types("Off the hill and into the cemetery.", "west")
            .Reply("That is the lot — nothing more to do out here.")
            .Reply("nothing to do")
            .Reply("Still here.")
            .Reply("nothing to do")
            .Types("Right — north, then.", "north");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);
        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("keep going until you reach the cemetery"));
        seat.Say(Turn(1, "west"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 5, timeoutMs: 20_000);

        // Two ticks and a change. An errand still in hand would have taken the step above and typed
        // it; the idle nudge and the taking-stock cadence both need fifty quiet seconds, so nothing
        // else is due this soon.
        await Task.Delay((ConfigDefaults.TickSeconds * 2 * 1000) + 1000);
        Assert.True(
            !harness.Output.Contains("\"line\":\"north\""),
            $"the errand stepped a third time rather than ending\ncalls: {llm.Requests.Count}\npane:\n{harness.Output}");
    }

    [Fact]
    public async Task The_same_line_is_held_when_a_turn_answered_somebody_elses_command()
    {
        // Measured in the 2026-09-21 UAT, nine times in one evening: the repair for a missed move
        // typed `examine case` at 22:13:04, a turn arrived a second later carrying the *player's*
        // own command (they had typed one themselves), the step pass read that as the answer to
        // what it had just said, and typed `examine case` again at 22:13:06. The game ran it twice.
        //
        // The companion cannot tell those turns apart — the turn it reads says what the game did,
        // and if that was somebody else's command, the natural conclusion is that its own move
        // never landed. The harness can tell them apart: the turn's own `Input` is what the core
        // wrote, so a line is answered when a turn arrives whose input is that line.
        var llm = new ScriptedLlm()
            .Reply("The case is shut, but the curtain makes me jump.")
            .Types("Said I would look. Looking now.", "examine case")
            .Types("And the woman — she's the one the letter was for.", "examine case")
            .Types("Right, the envelope then.", "give envelope to woman");

        var inbox = new[]
        {
            PlayerLine("keep going until you reach the case"),
            // The player typed a command of their own in the real session, the game executed it,
            // and *that* turn arrived while the repair's line was still unanswered. What matters
            // here is only the shape: a turn whose input is not the line we sent.
            Turn(1, "search examine and take everything"),
            // And this is the answer to the line the repair sent.
            Turn(2, "examine case"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        await harness.UntilAsync(text => text.Contains("\"line\":\"give envelope to woman\""));
        await Task.Delay(250);

        var output = harness.Output;
        var sent = output.Split("\"line\":\"examine case\"").Length - 1;
        Assert.True(sent == 1, $"`examine case` was sent {sent} time(s)");
        Assert.Contains("\"kind\":\"line_held\"", ReadAgentLog(harness));

        // And the held pass was not punished for it: no repair was asked for, because the move the
        // companion wanted was already in the game's hands.
        Assert.DoesNotContain("your last reply carried no move", llm.LastPrompt);
    }

    [Fact]
    public async Task Walking_into_the_same_direction_twice_is_not_held()
    {
        // The half that must not change: a duplicated direction is a walk, and a walk means each
        // step was answered before the next was taken. Holding it would stop the companion dead in
        // every corridor in the game.
        var llm = new ScriptedLlm()
            .Types("North, then north again.", "north")
            .Types("And on.", "north")
            .Types("That is the junction.", "west");

        var inbox = new[]
        {
            PlayerLine("keep going until you reach the junction"),
            Turn(1, "north"),
            Turn(2, "north"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        var output = await harness.UntilAsync(text => text.Contains("\"line\":\"west\""));

        Assert.Equal(2, output.Split("\"line\":\"north\"").Length - 1);
        Assert.DoesNotContain("line_held", ReadAgentLog(harness));
    }

    // -- a turn that never lands (02_game_boundary.md §4.3 (`G24`)) ------------------------------------

    [Fact]
    public async Task A_line_the_core_refused_lets_the_replies_held_behind_it_go()
    {
        // The session that went permanently silent, 2026-09-24: the companion typed a line the core's
        // repeat guard dropped, so no turn was ever coming back for it — and the player's next lines
        // are held on precisely the turn that line was going to produce, so they were held for ever.
        // No model call was made again, nothing on the screen said so, and the lines went nowhere.
        //
        // The core reported that refusal as `stuck`, a verdict about the story whose handler assumes a
        // turn can still land; it is named `input_refused` now, and this is the daemon's half of the
        // fix: the wait ends and the replies waiting behind it are answered.
        var llm = new ScriptedLlm()
            .Types("North, to the grate.", "north")                        // sent, and never answered
            .Reply("Then I have heard nothing back: the game never took that line.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("what do you make of the case?"));
        // The command is in the game's hands with no turn coming for it, which is the state the
        // player's next line has to be held in.
        await harness.UntilAsync(text => text.Contains("\"line\":\"north\""), timeoutMs: 20_000);

        seat.Say(PlayerLine("are you there?", seq: 901));
        // A grace rather than a wait with a duration behind it: the seat hands the loop one line at a
        // time, so all this has to allow for is the loop reading it. Held means no call, because
        // answering a line mid-turn is exactly what must not happen here.
        await Task.Delay(250);
        Assert.Single(llm.Requests);

        // And now the core says it would not write that line after all.
        seat.Say(System("input_refused", "that is the third time; nothing is moving", seq: 902));

        var output = await harness.UntilAsync(
            text => text.Contains("never took that line"), timeoutMs: 10_000);
        Assert.True(
            output.Contains("never took that line"),
            "the reply held behind a refused line never went out\noutput:\n" + output);

        // The companion was told *what* happened rather than only that the wait was over: a companion
        // that cannot tell a refusal from a turn reads the unchanged screen as the game's answer. It
        // is the second call, because exactly one has happened by the assertion above — the pass that
        // sent the line. (An errand is in hand here, so the reply below asks for the move it did not
        // carry; that is a third call and a different question.)
        var answering = string.Join("\n", llm.Requests[1].Select(message => message.Content));
        // The line that was waiting is the subject of the pass, so the answer is about *that* and not
        // about the game in general, and it carries the refusal: this pass could not have been one the
        // old `stuck` verdict asked for.
        Assert.Contains("are you there?", answering);
        Assert.Contains("refused before it reached the game", answering);
    }

    [Fact]
    public async Task A_turn_that_never_lands_lets_the_held_replies_go_once_the_deadline_passes()
    {
        // The net under the refusal above, and under every other way a line can be lost — including
        // the ones nobody has thought of yet. Same arrangement with nothing else added: a command in
        // the game's hands, a player line held behind it, and no turn and no refusal ever arriving,
        // so the deadline is the only thing left that can let the reply go.
        //
        // The deadline is read on the loop's tick rather than on a clock of its own, and the tick is
        // one every `ConfigDefaults.TickSeconds` — five seconds — so a reply can be a whole tick late.
        // The timeout says so rather than being a number chosen to look safe.
        var config = new AgentConfig { TurnFlightSeconds = 1 };
        var llm = new ScriptedLlm()
            .Types("North, to the grate.", "north")
            .Reply("No answer came back for that, so here is what I know.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("what do you make of the case?"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"north\""), timeoutMs: 20_000);

        seat.Say(PlayerLine("are you there?", seq: 901));
        await Task.Delay(250);
        Assert.Single(llm.Requests);

        var output = await harness.UntilAsync(
            text => text.Contains("here is what I know"),
            timeoutMs: ConfigDefaults.TickSeconds * 6 * 1000);
        Assert.True(
            output.Contains("here is what I know"),
            "the deadline did not let the held reply go\noutput:\n" + output);

        // And it was the deadline that did it rather than a turn: the second call (the first is the
        // pass that sent the line, and nothing else has happened since) was given the line that was
        // waiting and the fact that the command had produced no answer at all.
        var answering = string.Join("\n", llm.Requests[1].Select(message => message.Content));
        Assert.Contains("are you there?", answering);
        Assert.Contains("has produced no answer", answering);
    }

    [Fact]
    public async Task A_game_that_exits_lets_the_replies_held_behind_it_go()
    {
        // The third road to the same silence, and the one neither fix above covered: the hold is
        // keyed on `TurnInFlight`, which is `_awaitingTurn || PlayerDirectedFlight`, while
        // `game_exited` cleared `_awaitingTurn` alone. A `PlayerDirectedFlight` therefore outlived
        // the game it belonged to — and the player taking the keyboard sets that one flag by itself
        // (03_session.md §3.4 (`S20`): `player_direct`), with nothing of the companion's in flight
        // at all, so every line after the exit was held for a turn that no longer existed: no call,
        // nothing on the screen, and no way for the player to tell it from a hang.
        //
        // The deadline net from the test above is switched off here (`turn_flight_seconds` of zero
        // disables it), so that what this measures is the exit rather than the net. With the net
        // standing, the answer asserted below would still arrive on the old code, a whole
        // `turn_flight_seconds` later — and an assertion that passes two minutes late is not
        // asserting the thing under test.
        var config = new AgentConfig { TurnFlightSeconds = 0 };
        var llm = new ScriptedLlm()
            .Reply("Nothing came back for that: the game has stopped, and the line with it.")
            // One reply per thing this test is about: the answer to the line held behind the exit,
            // and the answer to the line spoken after it. Nothing is scripted for the repair that
            // the loop chases the second of those with (see the note above assertion 3): it is given
            // nothing to say, so it says nothing and reaches nobody.
            .Reply("I am here. The game is not.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(PlayerArrives());
        // The player typed a line straight into the game, so the companion is watching rather than
        // working: this is the flag the fix is really about, arranged without any command of the
        // companion's own in sight.
        seat.Say(System("player_direct", "the player took the keyboard", seq: 951));
        seat.Say(PlayerLine("what do you make of the case?", seq: 900));
        // Held means no call, as above, and the arrangement has to be sure of that before the exit
        // is sent — otherwise the answer this test asserts could be one the hold never stopped.
        await Task.Delay(250);
        Assert.Empty(llm.Requests);

        // The interpreter ends. Nothing is in flight, and nothing ever will be again.
        seat.Say(System("game_exited", "the interpreter ended", seq: 952));

        var output = await harness.UntilAsync(
            text => text.Contains("the game has stopped, and the line with it"),
            timeoutMs: 10_000);
        Assert.True(
            output.Contains("the game has stopped, and the line with it"),
            "the reply held behind a game that exited never went out\noutput:\n" + output);

        // And the companion was told *what* happened rather than only let go, because one that
        // cannot tell an exit from a turn reads the unchanged screen as the game's answer. This is
        // the first call of the run — the held line made none — and the line that was waiting is
        // its subject.
        var answering = string.Join("\n", llm.Requests[0].Select(message => message.Content));
        Assert.Contains("what do you make of the case?", answering);
        Assert.Contains("will not produce an answer", answering);

        // And the flag went with the game, so the next line is answered rather than held too. This
        // is the half that answering the held replies alone would have missed: draining the queue
        // empties it once, and a `PlayerDirectedFlight` still standing fills it again with the next
        // line, and the one after that.
        //
        // The pass that answers it is an ordinary one, because nothing in the loop carries the fact
        // that the game has gone: `allowAct` is decided by the hold alone there, so a reply that
        // moves nothing is chased by the repair for an announced move. That chase is a third call,
        // made after the words asserted below have gone out, and it is reported rather than fixed
        // here — the place it would be fixed is the act permission of a line's own pass
        // (`RespondOrWaitAsync`), not the exit path. What this test is about is that the line was
        // answered at all, and the pane says so before the chase begins.
        var before = llm.Requests.Count;
        seat.Say(PlayerLine("are you there?", seq: 901));

        output = await harness.UntilAsync(text => text.Contains("I am here."), timeoutMs: 10_000);
        Assert.True(
            output.Contains("I am here."),
            "a line spoken after the game exited was held behind it\noutput:\n" + output);
        Assert.True(
            llm.Requests.Count > before,
            "no call was made for the line spoken after the game exited");
        Assert.Contains(
            "are you there?",
            string.Join(
                "\n",
                llm.Requests
                    .Skip(before)
                    .SelectMany(request => request)
                    .Select(message => message.Content)));
    }

    [Fact]
    public async Task A_game_that_has_exited_is_not_asked_for_a_move()
    {
        // The other half of the exit, and the half the test above cannot see: the pass that answers
        // the replies held behind a game that has stopped is nobody's turn to move in any more. A
        // pass that may act with no game under it is a pass the repair for an announced move chases
        // (`NoteAMissedMoveAsync`), and what that repair asks is *"make the move now"* — put to a
        // live model with nowhere to put it, because the interpreter is gone. That is why
        // `AnswerTheHeldRepliesAsync` takes `actsAreUseful` instead of reading the hold alone, and
        // this is the assertion that it is wired rather than merely intended.
        //
        // Counted from the calls rather than read off the pane, because a repair that says nothing
        // is kept off the screen (`NothingCameOfTheRepair`) and would leave the pane looking right
        // while a needless call was made. With the exit pass act-free it is followed by exactly one
        // call — the answer itself — and the next `turn_started` belongs to whatever the player says
        // next.
        var config = new AgentConfig { TurnFlightSeconds = 0 };
        var llm = new ScriptedLlm()
            .Reply("Nothing came back for that: the game has stopped, and the line with it.")
            // A reply that fails the test if it is spent: a chase shows up as a second call.
            .Reply("if this is said, the exit was chased for a move");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(PlayerArrives());
        seat.Say(System("player_direct", "the player took the keyboard", seq: 951));
        seat.Say(PlayerLine("what do you make of the case?", seq: 900));
        await Task.Delay(250);
        Assert.Empty(llm.Requests);

        seat.Say(System("game_exited", "the interpreter ended", seq: 952));
        await harness.UntilAsync(
            text => text.Contains("the game has stopped, and the line with it"),
            timeoutMs: 10_000);
        // A grace rather than a wait: the repair, if one were coming, is the call the answer above
        // has already made possible, and the tick that could start a pass of its own is five
        // seconds away, so half a second is enough and short enough to prove nothing else fired.
        await Task.Delay(500);

        Assert.True(
            llm.Requests.Count == 1,
            $"the exit was followed by {llm.Requests.Count} calls rather than by the answer alone\n"
            + "pane:\n"
            + harness.Output);

        var exitPass = llm.Requests[0][^1].Content;
        // Told what happened (the reason the wait is over) and told not to move in the same breath:
        // the sentence the model reads and the gate the code enforces are one decision, which is the
        // rule the rest of this loop keeps its notes by.
        Assert.Contains("will not produce an answer", exitPass);
        Assert.Contains("nothing left to move in", exitPass);
        Assert.Contains("not to move in", exitPass);
        Assert.DoesNotContain("your last reply carried no move", exitPass);
    }

    [Fact]
    public async Task A_line_spoken_after_a_game_exit_is_not_asked_for_a_move()
    {
        // The half of the exit the two tests above cannot see. Both of them cover the *exit's own*
        // pass — the reply that was held behind a game that has stopped — and that pass was already
        // told it could not act, because its caller said so (`actsAreUseful: false`, since removed).
        // An ordinary line spoken afterwards is nobody's special case: it is a `player-line` pass
        // like any other, and that pass was authorised to act by the hold alone. So the answer was
        // chased by `NoteAMissedMoveAsync` with *"make the move now"* — a live model asked for a
        // command to type into a game that is gone (measured 2026-09-24).
        //
        // The sentence the model reads and the field the repair keys on are one decision
        // (`_allowAct`), so both ends of it are asserted here: the answer carries the no-act
        // constraint, and it is the only call the line produces. Counted from `ScriptedLlm.Requests`
        // rather than read off the pane, because a repair that says nothing is kept off the screen
        // (`NothingCameOfTheRepair`) and the pane would look right while a needless call was made.
        var llm = new ScriptedLlm()
            .Reply("Nothing to make of it: the game has stopped.")
            // A reply that fails the test by being spent: a chase shows up as a second call.
            .Reply("if this is said, the line after the exit was chased for a move");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, new AgentConfig { TurnFlightSeconds = 0 });

        seat.Say(PlayerArrives());
        // Nothing is waiting on the exit here, which is the point: the exit has no reply of its own
        // to answer, so every call this test sees belongs to the line that follows it.
        seat.Say(System("game_exited", "the interpreter ended", seq: 952));
        await Task.Delay(250);
        Assert.Empty(llm.Requests);

        seat.Say(PlayerLine("what do you make of the case?", seq: 901));

        var output = await harness.UntilAsync(
            text => text.Contains("Nothing to make of it"),
            timeoutMs: 10_000);
        Assert.True(
            output.Contains("Nothing to make of it"),
            "the line spoken after the game exited was never answered\noutput:\n" + output);

        // A grace rather than a wait: the repair, if one were coming, is the call the answer above
        // has already made possible, and the tick that could start a pass of its own is five seconds
        // away, so half a second is enough and short enough to prove nothing else fired.
        await Task.Delay(500);

        Assert.True(
            llm.Requests.Count == 1,
            $"the line after the exit was followed by {llm.Requests.Count} calls rather than by the "
            + "answer alone\npane:\n"
            + harness.Output);

        var answering = llm.Requests[0][^1].Content;
        // The generic no-act sentence and not the hold's: the reason this pass cannot move is the
        // world and not the player's hand, so the line the model reads has to be the one that says
        // the next move is the player's to hand over.
        Assert.Contains("This turn is yours to talk in, not to move in.", answering);
        Assert.Contains("the next move is your friend's to make, unless they hand it to you.", answering);
        Assert.DoesNotContain("your last reply carried no move", answering);
    }

    [Fact]
    public async Task A_game_that_exits_ends_the_errand()
    {
        // A plan is a plan *in* a world: `restart`, `reset` and `game_changed` all end the errand in
        // hand for that reason, and `game_exited` did not. So the errand outlived the game it was for
        // and the first tick after the exit stepped it again — measured 2026-09-24, as a
        // `mission-step` pass that put `{"type":"game_input","payload":{"line":"north"}}` on the pane
        // with no interpreter left to read it.
        //
        // The errand is *shown* to be in hand rather than assumed. Its line is one the tables read as
        // a delegation (`UtteranceClassifier.IsDelegation` starts the errand on the line itself, and
        // the same line is the one that produces "delegated by the player" in
        // `An_errand_the_player_set_is_not_rationed_in_steps`), and the turn that lands after it is
        // answered by a `mission-step` pass whose brief names the errand in the player's own words.
        // That second line is the errand working, and it is what makes the negative below honest: the
        // loop *is* stepping this errand before the exit, so a silent tick after it is the change
        // rather than a test that never arranged anything to stop.
        var config = new AgentConfig { TickSeconds = 1, TurnFlightSeconds = 0 };
        var llm = new ScriptedLlm()
            .Types("On it.", "south")
            .Types("Carrying on.", "north")
            // The reply a step would use if one were made after the exit, so the fault reaches the
            // pane as well as the call count if the errand survives the game it was for.
            .Types("Carrying on, then.", "north");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("scout the open exits and report back"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"south\""), timeoutMs: 10_000);

        seat.Say(Turn(1, "south"));
        await harness.UntilAsync(text => text.Contains("\"line\":\"north\""), timeoutMs: 10_000);
        Assert.Contains(
            "You are on an errand for the player: \"scout the open exits and report back\"",
            string.Join("\n", llm.Requests[1].Select(message => message.Content)));

        var actsBefore = harness.Output.Split("game_input").Length - 1;
        var callsBefore = llm.Requests.Count;

        seat.Say(System("game_exited", "the interpreter ended", seq: 952));

        // Two ticks, at a second each, plus slack — a test that asserts a negative has to have given
        // the loop the chance to do the wrong thing. Nothing else is due this soon: an errand in hand
        // outranks every speaking trigger in the tick, the idle nudge waits fifty seconds, and taking
        // stock waits for a cadence of turns that never land. So within this window a step is the one
        // thing the tick could do, and the errand is the one thing it could have done it with.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.True(
            harness.Output.Split("game_input").Length - 1 == actsBefore,
            "the errand typed into a game that had exited\npane:\n" + harness.Output);
        // The other way to see the same fault, and the one that catches a step whose act would be
        // refused on the world's account: the pass is still made, and this is what names it.
        Assert.DoesNotContain(
            llm.Requests.Skip(callsBefore).SelectMany(request => request),
            message => message.Content.Contains("You are on an errand for the player"));
        Assert.True(
            llm.Requests.Count == callsBefore,
            $"the errand was stepped after the game had exited: {llm.Requests.Count - callsBefore} call(s)\n"
            + "pane:\n"
            + harness.Output);
    }

    [Fact]
    public async Task An_instruction_that_takes_several_commands_keeps_the_floor()
    {
        // "Examine everything in the room" is five commands; a walk across the map is three.
        // One move per exchange is right about the *initiative* and wrong about the command, so
        // an instruction that earns a move becomes the work in hand — and the companion keeps
        // going until it reports (§3, §5.3).
        var llm = new ScriptedLlm()
            .Types("Right — south first, then the road.", "south")
            .Types("Gate east, then the hilltop.", "east")
            .Types("And the door.", "in")
            .Reply("In. Crisp is at the counter, shouting.")
            .Reply("nothing to do");

        var inbox = new[]
        {
            PlayerLine("try the post office then back here"),
            Turn(1, "south"),
            Turn(2, "east"),
            Turn(3, "in"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        var output = await harness.UntilAsync(_ => llm.Requests.Count >= 4);
        await Task.Delay(200);

        // Three moves, from one thing asked for.
        Assert.Contains("\"line\":\"south\"", output);
        Assert.Contains("\"line\":\"east\"", output);
        Assert.Contains("\"line\":\"in\"", output);

        // And the pass after the first one knew what the work was: the brief carries the
        // player's own words, not a summary of them.
        Assert.Contains(
            "You are on an errand for the player: \"try the post office then back here\"",
            llm.Requests[1][^1].Content);
    }

    [Fact]
    public async Task A_spent_budget_ends_the_errand_without_asking_for_another_move()
    {
        // The brief and the repair were contradicting each other: the brief said "the budget is
        // spent, stop and report", the repair said "you said you were going — make the call now".
        // The model obeyed the repair, and the act was then refused for exactly the reason the
        // brief gave. Measured live: "Walking it", a refused `west`, a retry refused by the
        // allowance, and two minutes of silence with the player waiting.
        var llm = new ScriptedLlm();
        for (var step = 0; step < 8; step++)
        {
            llm.Types($"step {step}", "north");
        }

        llm.Reply("Budget's spent on that run — here's where it got us.");

        var inbox = new List<string> { PlayerLine("keep going until you reach the lighthouse") };
        for (ulong turn = 1; turn <= 8; turn++)
        {
            inbox.Add(Turn(turn, "north"));
        }

        await using var harness = LoopHarness.Start(llm, inbox.ToArray());
        await harness.UntilAsync(_ => llm.Requests.Count >= 9);
        await Task.Delay(400);

        // The report is the end of it: nothing asked it for a move it could not make.
        Assert.DoesNotContain(
            llm.Requests,
            request => request[^1].Content.Contains("nothing was sent to the game"));
    }

    [Fact]
    public async Task A_move_the_policy_refuses_is_explained_rather_than_swallowed()
    {
        // The one thing a model cannot reason about is a world where its typing has no
        // consequences: it decides, it acts, and nothing answers. Refusals inside an errand were
        // always explained. Everywhere else they were logged and never mentioned — so the
        // companion went on believing it had acted, and never learned that the interpreter's
        // namespace is closed to it.
        var llm = new ScriptedLlm()
            .Reply(
                "Let me have the interpreter restart it.",
                calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", @"\restart")])
            .Reply("No interpreter tricks, then. Standing still.");

        await using var harness = LoopHarness.Start(llm, PlayerLine("i wonder if a restart would help"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"purpose\":\"introspect\""),
            timeoutMs: 15_000);
        await Task.Delay(200);

        // Looked for in the pass that was refused rather than in the last prompt: a dropped move
        // is now also handed over in the harness's own words, and that is a pass of its own
        // (05_memory.md §7 (`M37`–`M39`)), so the last prompt is the reading rather than the refusal.
        Assert.Contains(
            llm.Requests,
            request => request.Any(message => message.Content.Contains("your last move was not sent")));
        Assert.Contains("\"purpose\":\"introspect\"", ReadAgentLog(harness));
        Assert.DoesNotContain("game_input", harness.Output);
    }

    [Fact]
    public async Task A_named_purpose_runs_without_private_reasoning_and_an_unnamed_one_does_not()
    {
        // `thinking.off_for` is an instrument rather than a policy — the 2026-09-25 measurement is
        // 64 untyped moves in one evening, and the longest reasoning sat on exactly the turns that
        // missed. What it must not do is quietly silence the passes it does not name, and the
        // player's own reply is the one that matters most: thinking out loud is the companionship
        // (04_companion.md §4 (`C26`–`C28`)).
        var llm = new ScriptedLlm().Types("Off the hill and into the cemetery.", "west");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(
            llm,
            seat,
            new AgentConfig { Thinking = new ThinkingConfig { OffFor = ["mission-step"] } });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("keep going until you reach the cemetery"));
        seat.Say(Turn(1, "west"));

        await harness.UntilAsync(_ => llm.Options.Count >= 2, timeoutMs: 10_000);

        // The reply to the player first, with no opinion of ours, then the errand's step, silenced.
        Assert.Null(llm.Options[0]!.Thinking);
        Assert.False(llm.Options[1]!.Thinking);
    }

    [Fact]
    public async Task Work_the_player_asked_for_is_not_charged_to_the_allowance()
    {
        // The allowance bounds what the companion spends on itself at an empty desk. A journey
        // the player asked for is bounded by the errand's budget instead. Measured live: the
        // fifth call of one was refused with "the companion has spent its allowance for this
        // silence" and the walk stopped half done.
        var llm = new ScriptedLlm();
        for (var step = 0; step < 8; step++)
        {
            llm.Types($"step {step}", "north");
        }

        var inbox = new List<string> { PlayerLine("keep going until you reach the lighthouse") };
        for (ulong turn = 1; turn <= 7; turn++)
        {
            inbox.Add(Turn(turn, "north"));
        }

        await using var harness = LoopHarness.Start(llm, inbox.ToArray());
        await harness.UntilAsync(_ => llm.Requests.Count >= 6);
        await Task.Delay(300);

        // The limit is four calls and this is the sixth, because work is not counted against it.
        Assert.True(llm.Requests.Count >= 6, $"the walk stopped at {llm.Requests.Count} calls");
    }

    // -- the two channels are separate ------------------------------------------------------

    [Fact]
    public async Task The_tools_are_offered_on_a_call_that_can_act()
    {
        var llm = new ScriptedLlm().Types("on it", "north");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        await harness.UntilAsync(_ => llm.Options.Count > 0);

        Assert.NotNull(llm.Options[0]);
        Assert.NotNull(llm.Options[0]!.Tools);
        // A hand-over is a pass in front of the player with no errand in hand, so it gets everything
        // except the errand's own report — the surface is what the pass can legitimately do, and
        // there is nothing to call in when there is nothing in hand (04_companion.md §6 (`C42`–`C47`)) —
        // and except the whole-file writes, because it owes the player a line (`C70`).
        Assert.Equal(
            CompanionTools.WithoutTheWholeFileWrites(CompanionTools.InFrontOf(onAnErrand: false)).Count,
            llm.Options[0]!.Tools!.Count);
        Assert.Contains(llm.Options[0]!.Tools!, tool => tool.Name == CompanionTools.Type);
        Assert.DoesNotContain(llm.Options[0]!.Tools!, tool => tool.Name == CompanionTools.ReportBack);
    }

    [Fact]
    public async Task The_thinking_is_streamed_while_it_is_being_written()
    {
        // The screen used to be silent for the whole of a call — two seconds or more on 163 of
        // 248 calls in one evening, and twenty-two at the worst. The words now arrive as they
        // are written, and the finished thought that follows is the log's copy of them: the
        // pane closes the paragraph with it rather than printing it twice.
        var llm = new ScriptedLlm { StreamsReasoning = true };
        llm.Reply(
            "Locked gate, so east it is.",
            reasoning: "Locked. Climbing is out; the lane east is the one nobody has walked.");

        await using var harness = LoopHarness.Start(llm, PlayerLine("what now?"));

        var output = await harness.UntilAsync(text => text.Contains("\"type\":\"thought\""));
        await Task.Delay(200);

        var chunks = output.Split("\"type\":\"thought_delta\"").Length - 1;
        Assert.True(chunks >= 2, $"the thinking arrived in {chunks} chunk(s); expected it streamed");
        Assert.Contains("\"type\":\"thought\"", output);
    }

    [Fact]
    public void Every_tool_schema_is_json_the_api_can_accept()
    {
        // A malformed schema is a rejected request rather than a bad answer, which is the worst
        // possible moment to find out — and it is every call at once. `ToolDefinition.Of` is the only
        // way to build one, so this is really an assertion that the gate is where it should be and
        // that the definitions came through it.
        Assert.NotEmpty(CompanionTools.All);
        Assert.All(CompanionTools.All, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Name)));
        Assert.All(CompanionTools.All, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        Assert.All(CompanionTools.All, tool => JsonDocument.Parse(tool.Input.Json).Dispose());
        Assert.All(
            CompanionTools.All.Where(tool => tool.Output is not null),
            tool => JsonDocument.Parse(tool.Output!.Json).Dispose());
    }

    [Fact]
    public void The_name_is_a_stable_lower_case_word_and_no_tool_is_defined_twice()
    {
        // The names are what a call carries and what anything exposing this surface later would key
        // on, so they are pinned rather than left to taste: lower-case words with underscores.
        Assert.All(
            CompanionTools.All,
            tool => Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name));
        Assert.Equal(
            CompanionTools.All.Count,
            CompanionTools.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_argument_the_loop_reads_is_one_the_tool_declares()
    {
        // The drift this exists to catch is the quiet kind: a schema renamed, an argument read under
        // the old name, and a tool that silently stops receiving what it needs. The table is the
        // arguments the loop actually reads (`StringArgument`), by hand, because that is the only
        // honest way to know them.
        var read = new (string Tool, string[] Arguments)[]
        {
            (CompanionTools.Type, ["line"]),
            (CompanionTools.PressKey, ["key"]),
            (CompanionTools.Errand, ["goal"]),
            (CompanionTools.Ask, ["text"]),
            (CompanionTools.Recall, ["query"]),
            (CompanionTools.ReadNotes, ["section"]),
            (CompanionTools.UpdateNotes, ["section", "markdown"]),
            (CompanionTools.WriteNotes, ["markdown"]),
            (CompanionTools.WriteMemories, ["markdown"]),
            (CompanionTools.Report, ["markdown"]),
            (CompanionTools.MetaNote, ["note", "about"]),
        };

        foreach (var (name, arguments) in read)
        {
            var tool = Assert.Single(CompanionTools.All, candidate => candidate.Name == name);
            foreach (var argument in arguments)
            {
                Assert.Contains(argument, tool.Input.Properties);
            }
        }

        // And the tools that take nothing say so rather than guessing.
        foreach (var name in new[] { CompanionTools.Saves })
        {
            var tool = Assert.Single(CompanionTools.All, candidate => candidate.Name == name);
            Assert.Empty(tool.Input.Properties);
            Assert.Empty(tool.Input.Required);
        }
    }

    [Fact]
    public void The_round_rule_and_the_world_rule_follow_what_the_tools_declare()
    {
        // The two rules that have already gone wrong once, held to the declarations rather than to a
        // list of names beside them.
        //
        // 1. A retrieval round may only loop on a pure read. `saves` used to end a write-up pass,
        //    which is the whole of the 2026-09-20 report bug: the model checked its saves and the
        //    page was never written.
        Assert.True(CompanionTools.IsReadOnly(CompanionTools.Recall));
        Assert.True(CompanionTools.IsReadOnly(CompanionTools.ReadNotes));
        Assert.True(CompanionTools.IsReadOnly(CompanionTools.Saves));
        Assert.True(CompanionTools.IsReadOnly(CompanionTools.Map));
        Assert.False(CompanionTools.IsReadOnly(CompanionTools.WriteNotes));
        Assert.False(CompanionTools.IsReadOnly(CompanionTools.MetaNote));
        Assert.False(CompanionTools.IsReadOnly("a tool that does not exist"));
        Assert.All(
            CompanionTools.All,
            tool => Assert.Equal(tool.Annotations.ReadOnly, CompanionTools.IsReadOnly(tool.Name)));

        // 2. Nothing that moves the world may reach a pass that is not allowed to act.
        Assert.All(
            CompanionTools.Recording.Concat(CompanionTools.ForTheWriteUp).Concat(CompanionTools.ForTheLastWriteUp),
            tool => Assert.False(tool.Annotations.ActsOnTheGame, $"`{tool.Name}` can move the world"));
        Assert.All(CompanionTools.All.Where(tool => tool.Annotations.ActsOnTheGame),
            tool => Assert.Contains(tool.Name, new[] { CompanionTools.Type, CompanionTools.PressKey }));
        Assert.True(CompanionTools.All.Single(tool => tool.Name == CompanionTools.Type).Annotations.ActsOnTheGame);

        // 3. The last write-up attempt is the report and nothing else.
        Assert.Equal([CompanionTools.Report], CompanionTools.ForTheLastWriteUp.Select(tool => tool.Name));
    }

    /// <summary>
    /// The facts half of a tool result, checked against what the tool's output schema promises.
    /// <para>
    /// One assertion instead of two: the result must carry the block, and the block must carry every
    /// name the schema declares. A schema that promises a field the result does not have would be the
    /// same drift the input schemas are checked for, one level along.
    /// </para>
    /// </summary>
    private static JsonElement FactsBlock(string toolName, string result)
    {
        var tool = Assert.Single(CompanionTools.All, candidate => candidate.Name == toolName);
        Assert.NotNull(tool.Output);

        var block = result.Split("\n\n").Last();
        var parsed = JsonDocument.Parse(block);
        foreach (var declared in tool.Output!.Properties)
        {
            Assert.True(
                parsed.RootElement.TryGetProperty(declared, out _),
                $"`{toolName}` promises `{declared}` and the result does not carry it");
        }

        return parsed.RootElement.Clone();
    }

    /// <summary>
    /// The tool result the model was handed, by the id it answers.
    /// <para>
    /// Searched backwards through the calls rather than taken from the last one: a reply that only
    /// gathers leaves the harness asking for a move afterwards, so the most recent call is often the
    /// nudge rather than the round that carried the result.
    /// </para>
    /// </summary>
    private static string ToolResultFrom(ScriptedLlm llm)
    {
        foreach (var sent in Enumerable.Reverse(llm.Requests))
        {
            var result = sent.FirstOrDefault(message => message.ToolCallId is not null);
            if (result is not null)
            {
                return result.Content;
            }
        }

        var last = llm.Requests[^1];
        throw new InvalidOperationException(
            "no call carried a tool result. The last one held:\n"
            + string.Join(
                "\n---\n",
                last.Select(message => $"[{message.Role}] {message.Content[..Math.Min(240, message.Content.Length)]}")));
    }

    [Fact]
    public async Task The_saves_lookup_hands_back_facts_as_well_as_a_sentence()
    {
        // The companion quotes these numbers — "the save at turn 6, attempt 1, fork 2" — and reading
        // them out of prose is reading them slightly wrong eventually, with a confident answer about
        // the wrong branch as the cost (04_companion.md §2 (`C10`) and 05_memory.md §2 (`M11`)).
        var llm = new ScriptedLlm();
        llm.Reply(calls: [(CompanionTools.Saves, "{}")]);
        llm.Reply("Two saves, then.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            new AgentConfig(),
            state =>
            {
                var directory = Path.Combine(state, "saves", "wishbringer");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "wishbringer.sav"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(directory, "00-poodle.sav"), [1, 2, 3]);
                File.WriteAllText(
                    Path.Combine(directory, "00-poodle.json"),
                    """{"game":"wishbringer","saved_at":"2026-09-20T18:36:12Z","reason":"manual","turn":6,"run":1,"generation":2,"bytes":3}""");
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("where are we?"));
        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 20000);

        var result = ToolResultFrom(llm);
        Assert.Contains("Saves for wishbringer", result);

        var facts = FactsBlock(CompanionTools.Saves, result);
        Assert.Equal(2, facts.GetProperty("found").GetInt32());
        var poodle = facts.GetProperty("saves").EnumerateArray()
            .Single(entry => entry.GetProperty("Name").GetString() == "00-poodle");
        Assert.Equal(6, poodle.GetProperty("Turn").GetInt32());
        Assert.Equal(2, poodle.GetProperty("Fork").GetInt32());
        Assert.Equal("manual", poodle.GetProperty("Reason").GetString());
        Assert.False(poodle.GetProperty("IsTheRestartTarget").GetBoolean());

        // And the file with no note beside it says so as a null rather than as turn zero, which is a
        // different and wrong claim about when it was taken.
        var plain = facts.GetProperty("saves").EnumerateArray()
            .Single(entry => entry.GetProperty("Name").GetString() == "wishbringer");
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("Turn").ValueKind);
        Assert.True(plain.GetProperty("IsTheRestartTarget").GetBoolean());
    }

    [Fact]
    public async Task The_recall_lookup_hands_back_facts_with_the_attempt_and_the_fork()
    {
        // The citation failure this removes is the measured one: a turn quoted from the wrong attempt,
        // or from a branch the world was later reloaded away from.
        var llm = new ScriptedLlm();
        llm.Reply(calls: [ScriptedLlm.Tool(CompanionTools.Recall, "query", "teapot")]);
        llm.Reply("Turn twelve, then.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(
            llm,
            seat,
            new AgentConfig(),
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "observations"));
                File.WriteAllLines(
                    Path.Combine(state, "observations", "wishbringer.jsonl"),
                    [
                        """{"turn":11,"input":"look","output":"A dusty shelf.","fingerprint":"Shelf#aaaa","run":1,"generation":1}""",
                        """{"turn":12,"input":"take teapot","output":"You take the teapot.","fingerprint":"Shelf#bbbb","run":1,"generation":2}""",
                    ]);
            });

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerLine("what about the teapot?"));
        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 20000);

        var result = ToolResultFrom(llm);
        Assert.Contains("searched for \"teapot\"", result);

        var facts = FactsBlock(CompanionTools.Recall, result);
        Assert.Equal("teapot", facts.GetProperty("query").GetString());
        Assert.Equal(1, facts.GetProperty("found").GetInt32());
        var match = facts.GetProperty("matches").EnumerateArray().Single();
        Assert.Equal(12, match.GetProperty("Turn").GetInt32());
        Assert.Equal(1, match.GetProperty("Run").GetInt32());
        Assert.Equal(2, match.GetProperty("Generation").GetInt32());
        Assert.Equal("take teapot", match.GetProperty("Input").GetString());
        Assert.Contains("You take the teapot.", match.GetProperty("Output").GetString());
        Assert.Equal("Shelf#bbbb", match.GetProperty("Fingerprint").GetString());
    }

    /// <summary>
    /// Wait until some pass has been given a tool result, so a test can read it — the retrieval
    /// round means the call that ends the pass is usually the one after the result.
    /// </summary>
    private static Task<string> UntilAToolResultAsync(LoopHarness harness, ScriptedLlm llm) =>
        harness.UntilAsync(
            _ => llm.Requests.Any(request => request.Any(message => message.ToolCallId is not null)),
            timeoutMs: 20000);

    [Fact]
    public async Task Every_reply_carries_the_interpreters_own_numbers()
    {
        // The numbers the interpreter draws into row one of the screen by cursor addressing
        // (02_game_boundary.md §4.3 (`G23`)): no pane and no transcript has ever held them, and until
        // 2026-09-29 the only way to have them was a tool — which was called zero times in the
        // sixty-nine turns of the evening that needed them. They ride the per-turn message now.
        var llm = new ScriptedLlm().Reply("Nineteen moves in, then.");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(Vocabulary(3));
        seat.Say(StatusTurn(7, "\"location\":180,\"score\":0,\"moves\":19"));
        seat.Say(PlayerLine("how far in are we?"));
        await harness.UntilAsync(text => text.Contains("Nineteen moves in"));

        var asked = llm.Requests[^1][^1].Content;

        Assert.Contains("room 180", asked);
        Assert.Contains("score 0", asked);
        Assert.Contains("19 moves", asked);
    }

    [Fact]
    public async Task The_header_says_why_there_is_nothing_to_read_rather_than_zero()
    {
        // A story that keeps its own status line has no counters for the interpreter to report
        // (02_game_boundary.md §7 (`G38`)), so such a turn carries no numbers at all. The one answer worse
        // than an absent number is a zero: it reads as a real score of nothing, in a sentence the
        // companion would then say out loud to somebody watching the screen disagree.
        var llm = new ScriptedLlm().Reply("Then the screen is all I have to go on.");
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        // Version 5, which is the version that draws its own bar — the fact the header uses to say
        // which of the two reasons an absent number has.
        seat.Say(Vocabulary(5));
        seat.Say(StatusTurn(
            7,
            "\"location\":null,\"score\":null,\"moves\":null,\"hours\":null,\"minutes\":null"));
        seat.Say(PlayerLine("how far in are we?"));
        await harness.UntilAsync(text => text.Contains("Then the screen"));

        var asked = llm.Requests[^1][^1].Content;

        Assert.Contains("cannot say which room you are in", asked);
        Assert.Contains("Z-code v5", asked);
        Assert.DoesNotContain("score 0", asked);
        Assert.DoesNotContain("moves 0", asked);
    }

    [Fact]
    public void The_maintenance_pass_can_record_but_cannot_move()
    {
        // Taking stock is where the notebook and the memories are normally written, so a pass that
        // is offered no tools at all is a pass whose brief tells it to do something it cannot do.
        // The split is by what a tool does rather than by which pass is calling.
        Assert.Contains(CompanionTools.Recording, tool => tool.Name == CompanionTools.WriteNotes);
        Assert.Contains(CompanionTools.Recording, tool => tool.Name == CompanionTools.WriteMemories);
        // The second lookup that reads rather than asks: the map costs the game nothing and a pass
        // that must not move anything can still want to know where it has already been.
        Assert.Contains(CompanionTools.Recording, tool => tool.Name == CompanionTools.Map);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.Type);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.PressKey);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.Ask);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.Errand);

        // ...and the acting set is the whole surface, so a normal turn can do everything. The five
        // that a normal pass has and a maintenance pass does not are `type`, `press_key`, `ask`,
        // `errand` and `report` — a page nobody asked for is money spent on the companion's own
        // account (05_memory.md §6 (`M27`–`M36`)) — and `report_back` and `cause` are the sixth and
        // seventh: the errand's own ending, which a maintenance pass has no errand to earn, and a
        // hypothesis about the game, which is `T27.4`'s whole point (taking stock may correct its
        // reading of a place; it may not invent a law of the game absent-mindedly).
        Assert.Equal(CompanionTools.All.Count, CompanionTools.Recording.Count + 7);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.Report);
        Assert.DoesNotContain(CompanionTools.Recording, tool => tool.Name == CompanionTools.ReportBack);
        Assert.Contains(CompanionTools.ForTheWriteUp, tool => tool.Name == CompanionTools.Report);
        Assert.Contains(CompanionTools.ForTheWriteUp, tool => tool.Name == CompanionTools.WriteNotes);
    }

    [Fact]
    public void The_errand_report_is_offered_only_while_there_is_an_errand()
    {
        // Nothing to call in when there is no errand in hand, so the tool is not in the model's
        // list — the same rule as the maintenance surface, said from the other side (04_companion.md §6 (`C42`–`C47`)).
        Assert.Contains(CompanionTools.All, tool => tool.Name == CompanionTools.ReportBack);
        Assert.Contains(
            CompanionTools.InFrontOf(onAnErrand: true), tool => tool.Name == CompanionTools.ReportBack);
        Assert.DoesNotContain(
            CompanionTools.InFrontOf(onAnErrand: false), tool => tool.Name == CompanionTools.ReportBack);
        Assert.Contains(
            CompanionTools.InFrontOf(onAnErrand: false), tool => tool.Name == CompanionTools.Type);
        Assert.DoesNotContain(
            CompanionTools.ForTheWriteUp, tool => tool.Name == CompanionTools.ReportBack);
    }

    [Fact]
    public void The_harness_note_is_offered_wherever_a_failure_can_be_seen()
    {
        // A12, and the reason the fix is the description rather than the surface: over one evening
        // the tool was offered on every pass and called **0 times in 349 turns**, while 163 of the
        // companion's own thinking lines were about the machinery. The surface was never the
        // problem, so this test exists to keep it that way — every pass that can *watch* the
        // harness fail can also say so.
        Assert.Contains(CompanionTools.All, tool => tool.Name == CompanionTools.MetaNote);
        Assert.Contains(CompanionTools.Recording, tool => tool.Name == CompanionTools.MetaNote);
        Assert.Contains(CompanionTools.ForTheWriteUp, tool => tool.Name == CompanionTools.MetaNote);
        Assert.Contains(
            CompanionTools.InFrontOf(onAnErrand: true), tool => tool.Name == CompanionTools.MetaNote);
        Assert.Contains(
            CompanionTools.InFrontOf(onAnErrand: false), tool => tool.Name == CompanionTools.MetaNote);
    }

    [Fact]
    public void The_harness_note_description_names_the_moments_it_is_for()
    {
        // 05_memory.md §7 (`M39.5`): there is deliberately no paragraph about this tool in `engine.txt`, so the
        // schema *is* the instruction, and the schema that shipped said only "use it when there is
        // something to say" — which the model, standing in the middle of a turn, never recognised
        // as its own situation. These fragments are the load-bearing half of the replacement: the
        // moments, in the harness's own words, and the one sentence that says a note may travel
        // *beside* the move. If they are reworded, this test should be reworded with them.
        var description = CompanionTools.All
            .Single(tool => tool.Name == CompanionTools.MetaNote)
            .Description;

        Assert.Contains("carried no move", description);
        Assert.Contains("a restore, a fork, a save older than your notes", description);
        Assert.Contains("same reply", description);
        Assert.Contains("never a turn of its own", description);
        // And the boundaries it must keep: it is not the notebook, and it is not speech.
        Assert.Contains("is the notebook", description);
        Assert.Contains("is speech", description);
    }

    [Fact]
    public void The_type_description_binds_saying_you_are_going_to_the_call()
    {
        // `C63`'s own reason, applied to the tool whose job the sitting's biggest number is about:
        // the surface is injected as schemas, so a tool's description *is* where its job is stated.
        // Measured 2026-09-30: 16 replies announced a move and carried no call, across 14 of 37
        // turns, for 21% of the sitting's model spend — and the sentence that was there read as
        // licence to narrate the intent, because it said what to do *when* calling and nothing about
        // the words that stand in place of a call. What it says now is the missing half.
        var description = CompanionTools.All
            .Single(tool => tool.Name == CompanionTools.Type)
            .Description;

        Assert.Contains("carries no call has done nothing", description);
        Assert.Contains("this call is in that same reply", description);
        // And the half that was already right is kept: the words go beside the call, not instead.
        Assert.Contains("Your friend is reading", description);
    }

    [Fact]
    public async Task An_errand_ends_on_the_report_call_rather_than_after_a_second_pass()
    {
        // The report used to be the *absence* of a call: the brief promised that a reply which
        // moved nothing would end the errand, the repair asked once what it meant, and only then
        // was it over — two model calls for every report the companion wanted to make, 23 of them
        // in the 2026-09-21 evening. `report_back` says the same thing in the reply that says it,
        // and the pass after it is the player's silence rather than another call.
        var llm = new ScriptedLlm()
            .Types("West, then the gate.", "west")
            .Reply(
                "In the cemetery, and the grave is open. That is as far as this goes tonight.",
                calls: [(CompanionTools.ReportBack, "{}")]);

        var inbox = new[]
        {
            PlayerLine("keep going until you reach the cemetery"),
            Turn(1, "west"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        await harness.UntilAsync(text => text.Contains("the grave is open"));
        await Task.Delay(300);

        // The step, and the report: no third call for a repair that has nothing to repair.
        Assert.Equal(2, llm.Requests.Count);

        // And the report's own words reached the player, because beside this call they are the
        // report rather than a machine thinking out loud.
        Assert.Contains("as far as this goes tonight", harness.Output);

        var log = ReadAgentLog(harness);
        Assert.Contains("\"kind\":\"report_back\"", log);
        Assert.Contains("the companion called the errand in", log);
        Assert.Contains("\"reason\":\"the companion called the errand in\"", log);
        Assert.DoesNotContain("\"kind\":\"act_missed\"", log);
    }

    [Fact]
    public async Task A_report_called_in_with_no_words_is_asked_for_once()
    {
        // `T13.1`: the words beside the call are the report and MUST stand on their own. Measured in
        // the 2026-09-30 sitting: six of the nine reports carried none, and four of those six ended
        // an errand that had in fact been done — so at the moment a job finished, the only thing the
        // player was shown was the harness's own line about nothing having come of it. Asked once, and
        // for the words only: the errand is over either way, so there is nothing to decide.
        var llm = new ScriptedLlm()
            .Types("West, then the gate.", "west")
            .Reply(calls: [(CompanionTools.ReportBack, "{}")])
            .Reply("In the cemetery, and the grave is open. That is as far as this goes tonight.")
            // The drop this records also earns the companion the harness's own account of it, which is
            // a pass of its own (05_memory.md §7 (`M37`–`M39`)). It answers with a note and no prose,
            // which is exactly what it did in the sitting that found this — and that pass is not
            // talking to the player, so its silence must not become a line at them.
            .Reply(calls: [ScriptedLlm.Tool(CompanionTools.MetaNote, "note", "I called that in with no words beside it")]);

        var inbox = new[]
        {
            PlayerLine("keep going until you reach the cemetery"),
            Turn(1, "west"),
        };

        await using var harness = LoopHarness.Start(llm, inbox);
        await harness.UntilAsync(text => text.Contains("as far as this goes tonight"));

        // The reading of the drop's own account wrote its note and said nothing out loud, which is the
        // whole of its business — waited for by content, because the harness holds the file open while
        // it writes.
        var meta = Path.Combine(harness.StateDirectory, "meta", "unknown.md");
        await harness.UntilAsync(
            _ => File.Exists(meta) && File.ReadAllText(meta).Contains("called that in with no words"),
            timeoutMs: 15_000);
        await Task.Delay(300);

        // Asked once, and in words that ask for the report rather than for a move. Counting the ask
        // rather than the calls, because the drop it records also earns the companion the harness's
        // own account of it — which is a pass of its own (05_memory.md §7 (`M37`–`M39`)).
        var asked = llm.Requests
            .Select(messages => string.Join("\n", messages.Select(message => message.Content)))
            .Where(text => text.Contains("Say the report now"))
            .ToList();
        Assert.Single(asked);
        Assert.Contains("There is no call to make", asked[0]);

        // And the player got the companion's report rather than being told nothing happened — from
        // either pass. The reading that came after it is not the player's business and has no line.
        Assert.Contains("as far as this goes tonight", harness.Output);
        Assert.DoesNotContain("Nothing came of that", harness.Output);
    }

    [Fact]
    public async Task A_player_line_answered_with_a_call_and_no_words_is_asked_for_the_words()
    {
        // 2026-10-01, the sitting after the reset — and the pair of rows that made it findable:
        // `called_but_did_not_move {purpose: player-line, tools: [errand], speech_chars: 0}` beside
        // `harness_drop {…called the errand in with no words beside the call…}`. The words beside a
        // call are the whole of it (`T13.1`), and a call that is not a move is not an answer to being
        // spoken to: the only thing that reached the player was the harness's line about nothing
        // having come of it, on a pass that was answering them.
        var llm = new ScriptedLlm()
            .Reply(calls: [(CompanionTools.Errand, """{"goal":"find the magick stone"}""")])
            .Reply("The magick stone — that's the job, and the alley behind the shop is where I'd start.");

        await using var harness = LoopHarness.Start(llm, PlayerLine("what are we after tonight?"));
        await harness.UntilAsync(text => text.Contains("the alley behind the shop"));

        // Asked once, and asked for the *words*: nothing in this reply said it was going to move and
        // forgot to, so the repair that asks for a move would be answering a different question.
        var asked = llm.Requests
            .Select(messages => string.Join("\n", messages.Select(message => message.Content)))
            .Where(text => text.Contains("carried no words at all"))
            .ToList();
        Assert.Single(asked);
        Assert.Contains("what you are doing, and why", asked[0]);

        var log = ReadAgentLog(harness);
        Assert.Contains("silent-call", log);
        Assert.Contains("the companion called something with nothing said beside it", log);
        Assert.DoesNotContain("\"kind\":\"called_but_did_not_move\"", log);
    }

    [Fact]
    public async Task A_step_that_only_wrote_notes_is_counted_as_that_and_still_asked_once()
    {
        // 27 of the 167 misses in the 2026-09-21 UAT were replies that *had* called tools — a
        // notebook rewrite, an errand declared in the same breath. They are misses all the same:
        // the screen did not change, and the repair recovered the move 13 times out of the 19 that
        // only recorded. So the repair stays and the *number* changes, because the evening's
        // headline is read by counting one event name.
        var llm = new ScriptedLlm()
            .Reply(
                "Noted that the gate is locked, then.",
                calls:
                [
                    (CompanionTools.UpdateNotes, """{"section":"Dead ends","markdown":"- the gate is locked"}"""),
                ])
            .Types("Said I would go in. Going.", "in");

        await using var harness = LoopHarness.Start(llm, PlayerLine("try the gate"));
        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        // Asked once, and the move landed.
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("the game has not been given anything since", llm.LastPrompt);
        // Including the outcome that used to be missing from the ask: a question put to the player is
        // a call (`ask`), not prose (`NothingCameOfTheRepair` has counted it all along). This is what
        // the 2026-10-01 replay cost: a 1,522-character answer ending "Say which and I'll do it" was
        // repaired as a dropped move because the brief named every outcome except that one.
        Assert.Contains("`ask` is the call for that", llm.LastPrompt);
        Assert.Contains("\"line\":\"in\"", output);

        await Task.Delay(250);
        var log = ReadAgentLog(harness);
        Assert.Contains("\"kind\":\"called_but_did_not_move\"", log);
        Assert.Contains("\"tools\":[\"update_notes\"]", log);
        Assert.DoesNotContain("\"kind\":\"act_missed\"", log);
    }

    // -- the read, and the model's correction (04_companion.md §6.2 (`C44`–`C45`)) ---------------------------------

    [Fact]
    public async Task An_errand_the_model_declares_starts_even_when_the_tables_read_the_line_as_talk()
    {
        // Measured live, 2026-09-16: the bone errand was spoken mid-paragraph — "ok - i
        // remember playing this a long time ago... go to the graveyard and show the envelope
        // to the gravedigger..." — and the tables read it as chat because the first word was
        // `ok`. The model understood it in the same breath and the harness overruled it; the
        // errand only started when the player typed "keep going". The tables are a fast path
        // now, and the declaration is the authority.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Alright, that's an errand — I'll work it.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "get the bone from the grave and give it to the poodle")]);
        llm.Types("West to the cemetery first, then.", "west");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            PlayerLine(
                "ok - i remember playing this a long time ago.  to pass the dog we need a bone.  "
                + "that comes from the graveyard so....go to the graveyard and show the envelope "
                + "to the gravedigger."));

        await harness.UntilAsync(text => text.Contains("\"line\":\"west\""));

        // It was told how the tables read the line, and given the call that corrects them.
        var prompt = string.Join("\n", llm.Requests[0].Select(message => message.Content));
        Assert.Contains("the harness read that as ordinary talk", prompt);
        Assert.Contains("`errand`", prompt);

        // And the declaration became the errand, by name, in the log the machines read.
        var log = ReadAgentLog(harness);
        Assert.Contains("errand_declared", log);
        Assert.Contains("declared by the companion", log);
    }

    [Fact]
    public async Task A_long_instruction_only_the_model_can_read_gets_the_player_s_budget()
    {
        // The line is the 2026-09-30 UAT's own, and it is the whole defect: every job the classifier
        // read as "too long for a command" arrived as a declaration, was charged the companion's own
        // eight steps, and ended at exactly 8/8 mid-plan — the last of them standing in Rotary South
        // with the fountain one step north, after which the player had to re-issue the rest.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Right — that's a job, and I'll take it.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "Get to the graveyard, find the bone, then head into town")]);
        llm.Types("Out of the building first.", "out");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            PlayerLine(
                "I have some recollection of this from years ago.  After this we need to get to town "
                + "but to do that we need a bone to get passed the dog. the bone is in the grave in the "
                + "graveyard....so go to the graveyard and get the bone (there is an umberella there "
                + "too i think which we will need later).  once you habone head to town (give the dog "
                + "the bone) and wait there"));

        await harness.UntilAsync(text => text.Contains("\"line\":\"out\""));

        var log = ReadAgentLog(harness);
        // The route is still recorded, because C42.3 asks for that, and it is not what set the bound.
        Assert.Contains("declared by the companion", log);
        Assert.Contains("\"budget\":10000", log);
        Assert.DoesNotContain("\"budget\":8", log);
    }

    [Fact]
    public async Task A_line_the_tables_recognise_still_starts_the_errand_without_a_call()
    {
        // The fast path stays: a hand-over the tables can read starts the errand with no model
        // call for the purpose at all, and the log says which way it arrived.
        var llm = new ScriptedLlm().Types("On it.", "south");

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            PlayerLine("scout the open exits and report back"));

        await harness.UntilAsync(text => text.Contains("\"line\":\"south\""));

        var log = ReadAgentLog(harness);
        Assert.Contains("delegated by the player", log);
        Assert.DoesNotContain("errand_declared", log);
    }

    // -- the standing danger level (04_companion.md §8 (`C50`–`C54`)) ---------------------------------------------

    [Fact]
    public async Task A_high_risk_act_is_proposed_at_the_default_level_and_the_proposal_names_its_class()
    {
        // `give` spends something the player owns and cannot be taken back: medium lets it
        // through only with their blessing. The class travels on the proposal because §3.10
        // caught the old word `medium` hardcoded there — a grade is a measurement now.
        var llm = new ScriptedLlm().Reply(
            "Handing it over, then.",
            calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "give bone to poodle")]);

        await using var harness = LoopHarness.Start(
            llm,
            PlayerArrives(),
            PlayerLine("go on then - give it the bone"));

        await harness.UntilAsync(text => text.Contains("\"type\":\"proposal\""));

        Assert.Contains("\"risk\":\"high\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task The_players_standing_level_lets_the_same_act_through()
    {
        // `/riskhigh`, in the language the core speaks: a system line, and both dials move —
        // the gate below, and the sentence in the constraint block of every reply after it.
        var llm = new ScriptedLlm().Reply(
            "Handing it over, then.",
            calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", "give bone to poodle")]);

        await using var harness = LoopHarness.Start(
            llm,
            System("risk_level", "high"),
            PlayerArrives(),
            PlayerLine("go on then - give it the bone"));

        var output = await harness.UntilAsync(text => text.Contains("\"line\":\"give bone to poodle\""));

        Assert.DoesNotContain("\"type\":\"proposal\"", output);
        var prompt = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.Contains("danger level to high", prompt);
        Assert.Contains("\"level\":\"high\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task Speech_goes_out_exactly_as_it_was_written()
    {
        // Blank lines used to be dropped and a second [say] was joined onto the first, both of
        // which were artefacts of the tag contract (04_companion.md §2.2 (`C7`)). Plain text has neither rule.
        var speech = "Right, so the grate.\n\nIt gives a little when I lean on it.\nI say we try it.";
        var llm = new ScriptedLlm().Reply(speech);
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("\"type\":\"chat\""));
        var sent = await harness.UntilAsync(text => text.Contains("lean on it"));

        Assert.Contains("It gives a little when I lean on it.", sent);
        Assert.Contains("chat", output);
    }

    [Fact]
    public async Task The_reasoning_is_published_as_thinking_out_loud()
    {
        // The reasoning arrives on its own channel from the provider, so nothing has to parse it
        // out of the reply — and nothing can swallow it, which is what used to happen.
        var llm = new ScriptedLlm().Reply(
            "Let's try the grate.",
            reasoning: "The grate is loose. That is usually the way in.");
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("\"type\":\"thought\""));

        Assert.Contains("\"type\":\"thought\"", output);
        Assert.Contains("The grate is loose", output);
    }

    // -- one turn, one move -----------------------------------------------------------------

    [Fact]
    public async Task A_second_command_in_the_same_reply_is_not_typed()
    {
        // Two moves in one reply is not two turns: the game would already be answering the first.
        var llm = new ScriptedLlm().Reply(
            "north then east",
            calls:
            [
                ScriptedLlm.Tool(CompanionTools.Type, "line", "north"),
                ScriptedLlm.Tool(CompanionTools.Type, "line", "east"),
            ]);
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("game_input"));
        await Task.Delay(150);

        Assert.Contains("\"line\":\"north\"", output);
        Assert.DoesNotContain("\"line\":\"east\"", harness.Output);
    }

    [Fact]
    public async Task A_keystroke_is_sent_as_an_empty_line()
    {
        // The case `type` cannot express. The game is waiting at a prompt that is not a command
        // prompt — a `<Pulse una Tecla>`, a menu, an `[Press any key to begin.]` — and an empty
        // command line is deliberately not a move. `press_key` with no argument *is* that empty
        // line, and dfrotz reports an empty line to the game as a single carriage return.
        var llm = new ScriptedLlm().Reply(
            "It's asking for a key, not a command.",
            calls: [(CompanionTools.PressKey, "{}")]);
        await using var harness = LoopHarness.Start(llm, Handover("look"));

        var output = await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("\"type\":\"game_input\"", output);
        Assert.Contains("\"line\":\"\"", output);
    }

    [Fact]
    public async Task A_named_key_is_sent_as_that_key_and_still_counts_as_a_move()
    {
        var llm = new ScriptedLlm()
            .Reply("Menu. First option.", calls: [ScriptedLlm.Tool(CompanionTools.PressKey, "key", "2")])
            .Types("and on.", "look");
        await using var harness = LoopHarness.Start(llm, Handover("look"));

        var output = await harness.UntilAsync(text => text.Contains("\"line\":\"2\""));

        Assert.Contains("game_input", output);
    }

    [Fact]
    public async Task There_is_no_undo_tool()
    {
        // Removed on purpose, and this is here so that putting one back has to be a deliberate
        // act. A person at a 1983 keyboard cannot rewind, and the frame is two friends sharing
        // one of those keyboards. See the comment on the core's verb list for the long version.
        Assert.DoesNotContain(CompanionTools.All, tool => tool.Name == "undo");
    }

    [Fact]
    public async Task An_unknown_tool_is_ignored_rather_than_fatal()
    {
        var llm = new ScriptedLlm().Reply(
            "sure",
            calls: [("read_the_screen", "{}")]);
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        var output = await harness.UntilAsync(text => text.Contains("chat"));

        Assert.DoesNotContain("game_input", output);
    }

    // -- memory ----------------------------------------------------------------------------

    [Fact]
    public async Task Write_notes_replaces_the_notebook()
    {
        var llm = new ScriptedLlm().Reply(
            "Noted.",
            calls:
            [
                ScriptedLlm.Tool(CompanionTools.WriteNotes, "markdown", "# advent\n\n- the grate is loose"),
            ]);
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        await harness.UntilAsync(text => text.Contains("chat"));
        await Task.Delay(200);

        Assert.Contains("the grate is loose", harness.NotebookText);
    }

    [Fact]
    public async Task A_truncated_rewrite_leaves_the_notebook_alone()
    {
        // A reply cut off by the ceiling cannot be trusted to *replace* anything: the argument
        // would be the first half of a notebook. The finish reason says so outright, where the
        // old contract had to infer it from an unclosed block (issue #18).
        var llm = new ScriptedLlm().Reply(
            "Noting what I have so far",
            finish: FinishReason.Length,
            calls:
            [
                ScriptedLlm.Tool(CompanionTools.WriteNotes, "markdown", "# advent\n\n- a half-written"),
            ]);
        await using var harness = LoopHarness.Start(llm, Handover("north"));

        await harness.UntilAsync(text => text.Contains("chat"));
        await Task.Delay(200);

        Assert.DoesNotContain("a half-written", harness.NotebookText);
    }

    [Fact]
    public async Task A_reply_cut_off_before_it_says_anything_is_asked_again()
    {
        // Measured on 2026-09-22: the player corrected the companion's account of a walk, the reply
        // hit the length limit before its first sentence, and nothing came back at all — no words, no
        // question, no move, and nothing on a screen that tells a working companion from a stopped
        // one. Half an answer is left alone, because the player can see it was cut off and ask for
        // the rest; nothing at all is asked for once more.
        var llm = new ScriptedLlm();
        llm.Reply(finish: FinishReason.Length);                                 // cut off, having said nothing
        llm.Reply("The cliff, then. We went up it, and the shoppe was shut.");  // the answer, second time

        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            PlayerArrives(),
            PlayerLine("that litteraly is no true - we went up the cliff"));

        await harness.UntilAsync(text => text.Contains("the shoppe was shut"), timeoutMs: 20_000);

        Assert.Contains("the shoppe was shut", harness.Output);
        Assert.Contains(
            "the reply was cut off before it said anything; asking again",
            ReadAgentLog(harness));
        // The retry is a pass of its own, named so the evening's log can count the calls a ceiling
        // cost rather than hiding them inside the pass that was cut off — and what it was told is
        // the ceiling's own terms, not the repair-for-a-dropped-call brief, which would have asked
        // it for a move at a moment when the player was waiting to be answered.
        Assert.Contains("\"purpose\":\"cut-off\"", ReadAgentLog(harness));
        Assert.True(llm.Requests.Count >= 2, $"calls={llm.Requests.Count}\n{ReadAgentLog(harness)}");
        Assert.Contains(
            "was cut off by the length limit",
            string.Join("\n", llm.Requests[1].Select(message => message.Content)));
        // And the cut-off is a drop the harness knows about, so it is handed over afterwards in
        // the harness's own words (05_memory.md §7 (`M37`–`M39`)).
        Assert.Contains("\"purpose\":\"introspect\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task A_call_row_carries_the_ceiling_that_was_sent_rather_than_the_one_wanted()
    {
        // `§25` item 3, and the difference matters in both directions: the number a review used to be
        // given was the one the pass *wanted* — on a warn line, in prose — which is neither what went
        // out (the window may have clamped it) nor what the retry was given.
        var config = new AgentConfig
        {
            ContextWindowTokens = 40000,
            MaxTokensByPurpose = new Dictionary<string, int> { ["player-line"] = 100_000 },
        };
        var llm = new ScriptedLlm();
        llm.Reply(finish: FinishReason.Length);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("that litteraly is no true - we went up the cliff"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"kind\":\"reply_truncated\""),
            timeoutMs: 20_000);

        var log = ReadAgentLog(harness);

        // The clip is an event of its own, so a bundle can be counted rather than argued over.
        Assert.Contains("\"kind\":\"reply_truncated\"", log);

        // And the ceiling on the row is the one that was sent. The entry asked for 100,000 and the
        // window brought it down, so a row carrying the wanted number would say so here.
        Assert.DoesNotContain("\"max_tokens\":100000", log);

        // The retry is on the row as a retry, so an escalation can be counted instead of inferred.
        Assert.Contains("\"escalated\":true", log);
        Assert.Contains("\"escalated\":false", log);
    }

    [Fact]
    public async Task The_sitting_is_summarised_when_the_player_leaves()
    {
        // Wiring rather than arithmetic: `AgentLogTests` owns what the summary says, and this owns the
        // one place that says when — the same reason the container needs to be able to read it.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(System("player_absent", "the player has gone; wait for them", seq: 952));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"kind\":\"sitting_end\""),
            timeoutMs: 20_000);

        var log = ReadAgentLog(harness);

        Assert.Contains("\"kind\":\"sitting_end\"", log);
        Assert.Contains("\"llm_calls\":", log);
    }

    [Fact]
    public async Task No_sitting_is_summarised_that_nobody_was_in()
    {
        // The core says "the player has gone; wait for them" from boot, before anybody has connected —
        // measured 2026-10-01, the first rows of a fresh log. A summary of nothing is a fact about a
        // container starting rather than about a sitting, and it leaves the row a reader wants ambiguous.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome(resumed: false));
        seat.Say(System("player_absent", "the player has gone; wait for them", seq: 952));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("the player has gone; the companion will wait"),
            timeoutMs: 20_000);

        Assert.DoesNotContain("sitting_end", ReadAgentLog(harness));
    }

    /// <summary>
    /// Whether any call the model was given carried this text, rather than only the last one.
    /// <para>
    /// The last one is the wrong question for a consult: a reply that says nothing is followed at once
    /// by the passes that answer that (`C16.1`'s line, and the introspection the drop earns), so the
    /// prompt a test wants to read can be three requests old by the time it looks.
    /// </para>
    /// </summary>
    private static bool WasTold(ScriptedLlm llm, string text) =>
        llm.Requests.Any(
            request => request.Any(
                message => message.Content is { } content
                    && content.Contains(text, StringComparison.Ordinal)));

    [Fact]
    public async Task A_stretch_with_nothing_new_in_it_is_put_to_the_companion_as_a_decision()
    {
        // `C39.4`, and the half of it that was missing (`§25` item 4): the fact rode in the constraint
        // of every call and nobody acted on it. This is the unprompted path, which is where the circle
        // consult already lives.
        var llm = new ScriptedLlm();
        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(llm, seat, new AgentConfig(), WriteAStagnantWorld);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());

        // A turn that finds nothing new: a look around the room the record already knows. The turn
        // itself is an observation, so the line matters — `examine desk` would put a thing in the
        // record, and a stretch that just found something is not a stretch.
        seat.Say(StatusTurn(24, "\"location\":54,\"score\":0,\"moves\":24"));

        // Waited on the prompts rather than on the log: the log is held open for writing, which reads
        // fine in the gate's container and not at all on Windows (the suite has forty-odd tests in the
        // same boat). The behavioural assertions come first for the same reason — a failure here should
        // be about the consult rather than about the file.
        await harness.UntilAsync(
            _ => WasTold(llm, "make the move now"),
            timeoutMs: 20_000);

        // The prompt carries the fact *and* the ask — a decision rather than a remark (04_companion.md
        // `C39.1`) — and says nothing about what the stretch means, because whether to stop is the
        // player's (`F20`).
        Assert.True(WasTold(llm, "Nothing new for"), "the fact did not reach the model");
        Assert.True(WasTold(llm, "make the move now"), "the ask did not reach the model");
        Assert.False(WasTold(llm, "you should stop"), "the harness decided something for the player");

        var log = ReadAgentLog(harness);

        // And a pass of its own, named for what it is, recorded with the stretch's own numbers.
        Assert.Contains("\"purpose\":\"goingnowhere\"", log);
        Assert.Contains("\"kind\":\"stagnation\"", log);
        Assert.Contains("\"turns\":", log);
    }

    [Fact]
    public async Task An_errand_over_a_stretch_with_nothing_new_in_it_is_asked_to_decide()
    {
        // The case the row is about. §23 item 1 gave the player's own errand no step bound, so a stretch
        // with nothing new in it is the only thing watching one — and the circle consult cannot reach
        // here at all, because the errand branch answers before the triggers are ever asked.
        var llm = new ScriptedLlm();
        llm.Reply(
            "Scouting around, then.",
            calls: [ScriptedLlm.Tool(CompanionTools.Errand, "goal", "scout the open exits and report back")]);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOnPrepared(llm, seat, new AgentConfig(), WriteAStagnantWorld);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("scout the open exits and report back"));

        // Waited on the pane rather than on the log: the declaration is only in hand once the pass that
        // read the line has answered, and its words are what a test can see from here.
        await harness.UntilAsync(text => text.Contains("Scouting around"), timeoutMs: 20_000);

        // The step's own turn, in a room the record already knows, so the observation it adds is not
        // news either (see the unprompted test above for why the line matters).
        seat.Say(StatusTurn(25, "\"location\":54,\"score\":0,\"moves\":25"));

        await harness.UntilAsync(
            _ => WasTold(llm, "make the move now"),
            timeoutMs: 20_000);

        Assert.True(WasTold(llm, "Nothing new for"), "the fact did not reach the step");
        Assert.True(WasTold(llm, "make the move now"), "the step was told rather than asked");

        var log = ReadAgentLog(harness);

        // The step is still a step and may still move — a consult that stopped the errand would be the
        // stop `C39.2` records the failure of, and ending it is the player's to say.
        Assert.Contains("\"purpose\":\"mission-step\"", log);
        Assert.Contains("\"kind\":\"stagnation\"", log);
        Assert.DoesNotContain("\"kind\":\"mission_end\"", log);
    }

    /// <summary>
    /// Twenty-four turns of which the first four are the only ones that find anything, where the loop
    /// will look for them: the fixture `StagnationTests` uses, written into the state a harness is about
    /// to start on.
    /// <para>
    /// Both files the document is built from are written, and then the document itself — because the
    /// loop *reads* it (`WorldState.Read`) rather than building it, and a world that exists only as
    /// observations is a world the loop sees as no world at all.
    /// </para>
    /// </summary>
    private static void WriteAStagnantWorld(string state)
    {
        Directory.CreateDirectory(Path.Combine(state, "observations"));
        Directory.CreateDirectory(Path.Combine(state, "runs"));

        var rows = new List<string>
        {
            """{"turn":1,"input":"look","output":"The game room, with a gun rack and a desk.","fingerprint":"Game room#aaaa","run":1,"generation":1,"location":96}""",
            """{"turn":2,"input":"east","output":"The office is small and tidy.","fingerprint":"Office#aaaa","run":1,"generation":1,"location":54}""",
            """{"turn":3,"input":"examine thing0","output":"You find nothing of interest.","fingerprint":"Office#aaaa","run":1,"generation":1,"location":54}""",
            """{"turn":4,"input":"examine thing1","output":"You find nothing of interest.","fingerprint":"Office#aaaa","run":1,"generation":1,"location":54}""",
            """{"turn":5,"input":"west","output":"The game room, with a gun rack and a desk.","fingerprint":"Game room#aaaa","run":1,"generation":1,"location":96}""",
            """{"turn":6,"input":"east","output":"The office is small and tidy.","fingerprint":"Office#aaaa","run":1,"generation":1,"location":54}""",
        };
        for (var i = 0; i < 17; i++)
        {
            rows.Add(
                $$"""{"turn":{{7 + i}},"input":"examine thing{{i % 2}}","output":"You find nothing of interest.","fingerprint":"Office#aaaa","run":1,"generation":1,"location":54}""");
        }

        File.WriteAllLines(Path.Combine(state, "observations", "wishbringer.jsonl"), rows);
        File.WriteAllText(
            Path.Combine(state, "runs", "wishbringer.json"),
            """{"current":{"number":1,"started":"2026-09-30T00:41:00Z","generation":1}}""");

        WorldState.Rebuild(state, "wishbringer");
    }

    [Fact]
    public async Task The_ask_for_a_reply_cut_off_before_it_spoke_happens_once()
    {
        // The bound, not the good path: cut-off replies one after another mean the ceiling landed
        // early rather than a model that cannot answer, so the ask is made once and no more — a
        // second ask of a second silent reply is a second wasted call, and the allowance is not
        // there to be spent proving the same thing twice.
        var llm = new ScriptedLlm();
        llm.Reply(finish: FinishReason.Length);
        llm.Reply(finish: FinishReason.Length);
        llm.Reply(finish: FinishReason.Length);

        await using var harness = LoopHarness.Start(
            llm,
            Welcome(resumed: false),
            PlayerArrives(),
            PlayerLine("that litteraly is no true - we went up the cliff"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("the reply was cut off before it said anything"),
            timeoutMs: 20_000);
        await Task.Delay(300);

        var log = ReadAgentLog(harness);
        Assert.Equal(1, log.Split("the reply was cut off before it said anything").Length - 1);
        // Three calls and no more: the pass that was cut off, the one ask for it, and the one
        // reading of the harness's account of it. A fourth would be the same thing asked twice.
        Assert.True(
            llm.Requests.Count == 3,
            $"calls={llm.Requests.Count}\n{log}");
        // (Half the line, because the log is JSON and the apostrophe in "harness's" reaches it
        // as \u0027, which a C# literal would turn back into an apostrophe.)
        Assert.Equal(1, log.Split("account of what went wrong is in front of").Length - 1);
    }

    [Fact]
    public async Task A_reply_cut_off_before_it_said_anything_is_asked_for_again_with_more_room()
    {
        // `C23`'s ask was built with the same ceiling the reply had just been cut off by, so a ceiling
        // that bit once bit again — and the price of finding that out is the whole prompt, re-sent.
        // Measured 2026-09-30: 14,429 prompt tokens per call against a few hundred of completion, so
        // the room is the cheap half of that trade and narrowing the ask is the expensive half.
        var config = new AgentConfig { MaxTokens = 16000 };
        var llm = new ScriptedLlm();
        llm.Reply(finish: FinishReason.Length);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("that litteraly is no true - we went up the cliff"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 20_000);

        // The pass that was cut off, and the ask that followed it. Two things are being pinned: the
        // ceiling came from the pass's *role* rather than from the global fallback (`C23.6` — the two
        // numbers differ, which is how the test can tell), and the retry asked for twice the room
        // (`C23.4`).
        var first = llm.Options[0]!.MaxTokens;
        var retry = llm.Options[1]!.MaxTokens;

        Assert.Equal(16384, first);
        Assert.Equal(32768, retry);
    }

    [Fact]
    public async Task A_ceiling_bigger_than_the_window_leaves_is_clamped_to_the_room()
    {
        // `C23.3`: prompt and completion have to fit together, and a request that overshoots the window
        // is refused rather than shortened — which arrives looking like a provider fault rather than
        // like a setting. So the ceiling is arithmetic and not a wish, and an entry naming six figures
        // is a floor to be brought down rather than a number to be sent.
        var config = new AgentConfig
        {
            ContextWindowTokens = 40000,
            MaxTokensByPurpose = new Dictionary<string, int> { ["player-line"] = 100_000 },
        };
        var llm = new ScriptedLlm();
        llm.Reply(finish: FinishReason.Length);

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat, config);

        seat.Say(Welcome(resumed: false));
        seat.Say(PlayerArrives());
        seat.Say(PlayerLine("that litteraly is no true - we went up the cliff"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 20_000);

        var first = llm.Options[0]!.MaxTokens;
        var retry = llm.Options[1]!.MaxTokens;

        // The window governed both attempts: 40,000 less the prompt less the margin, and nothing like
        // the 100,000 the entry names.
        Assert.True(first <= 40000 - 2048, $"the first attempt asked for {first}");
        Assert.True(retry <= 40000 - 2048, $"the retry asked for {retry}");

        // And the clamp is not collapse: the entry still bought more room than the role alone would
        // have, which is the difference between a ceiling being trimmed and a ceiling being ignored.
        Assert.True(
            first > config.RoleCeilingFor("player-line") * 2,
            $"the clamp threw the entry away rather than trimming it: {first}");
    }

    [Fact]
    public async Task An_update_notes_call_with_nothing_in_it_is_dropped()
    {
        // An empty body is a model slip, not an edit: `UpdateSection` writes the heading and a blank
        // line over whatever was there, and the loop logged `notebook_section_updated` regardless.
        // Measured on 2026-09-22: seven section edits in ten minutes, every one of them a success in
        // the log, and what had been lost was only visible by reading the file. An empty `type` is
        // refused on the same grounds.
        var llm = new ScriptedLlm();
        llm.Reply(calls: [(CompanionTools.UpdateNotes, """{"section":"Open questions","markdown":"   "}""")]);
        llm.Reply("Nothing to add, then.");

        await using var harness = LoopHarness.StartPrepared(
            llm,
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "unknown.md"),
                    "# notes\n\n## Open questions\n\n- is the coin any good for the arcade?\n");
            },
            PlayerArrives(),
            PlayerLine("the coin is no good for the arcade"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("nothing in it was dropped"),
            timeoutMs: 15_000);
        await Task.Delay(200);

        Assert.Contains("an update_notes call with nothing in it was dropped", ReadAgentLog(harness));
        // The section is exactly as it was, and nothing claims otherwise.
        Assert.Contains("is the coin any good for the arcade", harness.NotebookText);
        Assert.DoesNotContain("\"kind\":\"notebook_section_updated\"", ReadAgentLog(harness));
    }

    [Fact]
    public async Task An_update_notes_call_that_names_no_section_is_dropped()
    {
        // The heading match treats "" as a match, so this call would land on the *first* heading in
        // the file — the plan, usually — and write a body meant for somewhere else over it, under the
        // name of a section the call never named.
        var llm = new ScriptedLlm();
        llm.Reply(
            calls: [(CompanionTools.UpdateNotes, """{"section":"","markdown":"- something else entirely"}""")]);

        await using var harness = LoopHarness.StartPrepared(
            llm,
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "unknown.md"),
                    "# notes\n\n## Current goals and plan\n\n- get down the cliff\n");
            },
            PlayerArrives(),
            PlayerLine("where were we?"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("named no section was dropped"),
            timeoutMs: 15_000);

        Assert.Contains("an update_notes call that named no section was dropped", ReadAgentLog(harness));
        Assert.Contains("get down the cliff", harness.NotebookText);
        Assert.DoesNotContain("something else entirely", harness.NotebookText);
    }

    [Fact]
    public async Task A_rewrite_names_the_sections_it_left_out()
    {
        // A rewrite is a whole-file replacement, and that is how a section written ten minutes ago
        // disappears: the event said a rewrite had happened and no row anywhere said that part of
        // the past had gone. Measured on 2026-09-22: a route log written over ten minutes was gone by
        // the next rewrite, and finding that out took a reader and the file rather than the log.
        //
        // The row is evidence rather than a verdict, and its wording says so: a heading count cannot
        // tell a merge from a loss. Measured again on 2026-09-25, the row named two sections whose
        // subjects are both in the file afterwards under other headings — so it names the names to
        // go and look for, and nothing more. It must never become a gate: the rewrite is the only
        // pruning the notebook has, and a rewrite that may not drop a heading can only grow.
        var llm = new ScriptedLlm();
        llm.Reply(
            calls:
            [
                ScriptedLlm.Tool(
                    CompanionTools.WriteNotes,
                    "markdown",
                    "# notes\n\n## Key facts\n\n- the coin is a dud\n"),
            ]);

        await using var harness = LoopHarness.StartPrepared(
            llm,
            state =>
            {
                Directory.CreateDirectory(Path.Combine(state, "notebook"));
                File.WriteAllText(
                    Path.Combine(state, "notebook", "unknown.md"),
                    "# notes\n\n## Key facts\n\n- the arcade is shut\n\n## Route log — this evening's walk\n\n- east, then north\n");
            },
            PlayerArrives(),
            PlayerLine("the coin is a dud"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("has none by these names"),
            timeoutMs: 15_000);

        var log = ReadAgentLog(harness);
        Assert.Contains("the rewrite kept different sections, and has none by these names", log);
        // Named, because a name is what tells a reader which part of the past to go and look for.
        Assert.Contains("Route log", log);
        Assert.Contains("the coin is a dud", harness.NotebookText);
    }
    [Fact]
    public async Task A_dropped_move_is_handed_over_and_what_it_learns_is_written_down()
    {
        // The measurement this exists for: `meta_note` was offered on every pass and called 0 times
        // in 349 turns, while 163 of the companion's own thinking lines were about the machinery. The
        // surface was there and the moments were there; what it did not have was any way to know that
        // something had gone wrong on the harness's side of the screen (05_memory.md §7 (`M37`–`M39`)). So the
        // harness says so, and the pass that follows has one thing to do: write it down.
        var llm = new ScriptedLlm()
            .Reply(
                "Let me have the interpreter restart it.",
                calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", @"\restart")])
            .Reply("No interpreter tricks, then. Standing still.")
            .Reply(
                calls:
                [
                    ScriptedLlm.Tool(
                        CompanionTools.MetaNote,
                        "note",
                        "a line that starts with a backslash is refused by the harness, not by the game"),
                ]);

        await using var harness = LoopHarness.Start(llm, PlayerLine("i wonder if a restart would help"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"kind\":\"meta_note\""),
            timeoutMs: 15_000);

        // The note is in the file that is for exactly this: a fact about the machinery, out of the
        // world, and never read back into any prompt.
        var meta = Path.Combine(harness.StateDirectory, "meta", "unknown.md");
        Assert.True(File.Exists(meta), ReadAgentLog(harness));
        Assert.Contains("refused by the harness, not by the game", File.ReadAllText(meta));

        // And what the pass was offered is the whole of "a record and never a lever": the tools it
        // holds are the recording ones, and none of them acts on the game.
        var offered = llm.Options[^1]!.Tools!;
        Assert.Contains(offered, tool => tool.Name == CompanionTools.MetaNote);
        Assert.DoesNotContain(offered, tool => CompanionTools.ActsOnTheGame(tool.Name));

        // The report it read named the drop in the harness's own words rather than the game's.
        var log = ReadAgentLog(harness);
        Assert.Contains("\"kind\":\"harness_drop\"", log);
        Assert.Contains("the harness did not send it", string.Join(
            "\n", llm.Requests[^1].Select(message => message.Content)));
    }

    [Fact]
    public async Task The_harness_account_is_read_once_a_turn()
    {
        // One dropped reply is enough to spend a call on looking at it (decided 2026-09-22), which is
        // why the report is read on the first thing that drops rather than after a run of them. What
        // is bounded is the reading: a turn that dropped twice is still one turn, and the second
        // reading would be the companion talking to itself about the same evening on the player's
        // account.
        var llm = new ScriptedLlm()
            .Reply(
                "Let me have the interpreter restart it.",
                calls: [ScriptedLlm.Tool(CompanionTools.Type, "line", @"\restart")])
            .Reply(finish: FinishReason.Length)                 // the refusal's ask is cut off, and says nothing
            .Reply(finish: FinishReason.Length)                 // and the ask for that is cut off too
            .Reply(calls: [ScriptedLlm.Tool(CompanionTools.MetaNote, "note", "the ceiling is not the game")]);

        await using var harness = LoopHarness.Start(llm, PlayerLine("i wonder if a restart would help"));

        await harness.UntilAsync(
            _ => ReadAgentLog(harness).Contains("\"purpose\":\"introspect\""),
            timeoutMs: 15_000);
        await Task.Delay(400);

        var log = ReadAgentLog(harness);
        // Counted from the line that is written once per reading, not from the purpose: a purpose
        // appears on several rows of the same call, and a test that counts rows is a test of the
        // log's shape rather than of the loop's.
        Assert.Equal(1, log.Split("account of what went wrong is in front of").Length - 1);

        // Both facts are in the one report, because they are both true and both the harness's: the
        // move it would not send, and the reply the provider cut off.
        var read = string.Join("\n", llm.Requests[^1].Select(message => message.Content));
        Assert.Contains("did not send it", read);
        Assert.Contains("cut your reply off", read);
    }

    [Fact]
    public async Task An_answer_that_moves_nothing_is_not_a_drop()
    {
        // The other side of "a single dropped reply is enough": an answer to being spoken to has no
        // move in it and is not a failure — the companion is the one who gets to decide that it
        // answered, and it decides it by speaking. Reading the harness's account after every such
        // answer would be a call an answer, on the evening's most ordinary exchange.
        var llm = new ScriptedLlm()
            .Reply("The coin is a dud, then.")
            .Reply("Nothing to do.");

        await using var harness = LoopHarness.Start(llm, PlayerLine("what do you make of the coin?"));

        await harness.UntilAsync(_ => llm.Requests.Count >= 2, timeoutMs: 15_000);
        await Task.Delay(500);

        var log = ReadAgentLog(harness);
        Assert.DoesNotContain("\"kind\":\"harness_drop\"", log);
        Assert.DoesNotContain("\"purpose\":\"introspect\"", log);
    }
}
