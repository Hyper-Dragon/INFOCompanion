using System.Text;
using System.Text.Json;
using Agent.Core.Protocol;

namespace Agent.Tests;

/// <summary>
/// The wire contract (§6). The core relies on our framing being exact: one JSON object
/// per line, increasing sequence numbers, and nothing but protocol on stdout.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void An_envelope_round_trips()
    {
        var original = new Envelope
        {
            V = SeatProtocol.Version,
            Type = MessageTypes.Chat,
            Seq = 7,
            Ts = Timestamps.Rfc3339Now(),
            Payload = JsonSerializer.SerializeToNode(new { text = "hello" }, ProtocolJson.Options),
        };

        var line = JsonSerializer.Serialize(original, ProtocolJson.Options);
        var parsed = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;

        Assert.Equal((ulong)SeatProtocol.Version, parsed.V);
        Assert.Equal(MessageTypes.Chat, parsed.Type);
        Assert.Equal(7UL, parsed.Seq);
        Assert.EndsWith("Z", parsed.Ts);
        Assert.Equal("hello", parsed.StringField("text"));
    }

    [Fact]
    public void A_welcome_is_understood_including_the_game_stem()
    {
        const string line =
            """
            {"v":1,"type":"welcome","seq":1,"ts":"2026-09-11T00:00:00Z","payload":{"game_title":"Welcome to Adventure!","game_stem":"advent","turn_count":12,"transcript_tail":["> look\nYou are in a forest."]}}
            """;

        var envelope = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;
        var welcome = envelope.PayloadAs<WelcomePayload>()!;

        Assert.Equal("Welcome to Adventure!", welcome.GameTitle);
        Assert.Equal("advent", welcome.GameStem);
        Assert.Equal(12, welcome.TurnCount);
        Assert.Single(welcome.TranscriptTail!);
    }

    /// <summary>
    /// A welcome exactly as the core frames it on a fresh start, copied from
    /// <c>Outbound::Welcome</c>'s payload (<c>Core/src/seat.rs</c>).
    /// <para>
    /// This is the shape that used to be refused outright. With no save record to read the core
    /// sends <c>resumed_at_turn: null</c>; the companion declared that field non-nullable,
    /// <see cref="Envelope.PayloadAs{T}"/> swallowed the exception, <c>HandleWelcomeAsync</c> read
    /// the null as "this payload is not for me" and returned early — so the notebook was never
    /// retargeted, the transcript tail was never seeded and the run number never arrived, on every
    /// fresh boot, with nothing louder than a warning. A golden payload is what catches it.
    /// </para>
    /// </summary>
    [Fact]
    public void A_fresh_start_welcome_is_understood()
    {
        const string line =
            """
            {"v":1,"type":"welcome","seq":1,"ts":"2026-09-19T04:03:29Z","payload":{"game_title":"Zork I","game_stem":"zork1","turn_count":0,"transcript_tail":[],"agent_name":"james","run":1,"generation":1,"resumed":false,"resumed_from":null,"resumed_at_turn":null,"resumed_saved_at":null}}
            """;

        var envelope = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;
        var welcome = envelope.PayloadAs<WelcomePayload>(out var problem);

        Assert.Null(problem);
        Assert.NotNull(welcome);
        Assert.Equal("zork1", welcome.GameStem);
        Assert.Equal(1, welcome.Run);
        Assert.False(welcome.Resumed);
        Assert.Null(welcome.ResumedAtTurn);
        Assert.Null(welcome.ResumedSavedAt);
    }

    /// <summary>
    /// The other shape that same field arrives in: a resumed boot sends a number, and it has to
    /// survive the round trip.
    /// </summary>
    [Fact]
    public void A_resumed_welcome_carries_the_turn_it_came_back_at()
    {
        const string line =
            """
            {"v":1,"type":"welcome","seq":1,"ts":"2026-09-19T04:03:29Z","payload":{"game_title":"Zork I","game_stem":"zork1","turn_count":12,"transcript_tail":[],"agent_name":"james","run":1,"generation":2,"resumed":true,"resumed_from":"zork1.sav","resumed_at_turn":42,"resumed_saved_at":"2026-09-18T21:00:00Z"}}
            """;

        var envelope = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;
        var welcome = envelope.PayloadAs<WelcomePayload>(out var problem)!;

        Assert.Null(problem);
        Assert.True(welcome.Resumed);
        Assert.Equal("zork1.sav", welcome.ResumedFrom);
        Assert.Equal(42L, welcome.ResumedAtTurn);
        Assert.Equal("2026-09-18T21:00:00Z", welcome.ResumedSavedAt);
    }

    /// <summary>
    /// The interpreter's own status line travels with the turn, and an absent number is *cannot
    /// say* (02_game_boundary.md §4.3 (<c>G23</c>)). Both shapes are here because the second is the
    /// one a reader is tempted to read as zero — and the story that keeps its own status line
    /// (02_game_boundary.md §7 (<c>G38</c>)) sends exactly that: nothing at all.
    /// </summary>
    [Fact]
    public void A_turn_carries_the_interpreters_status_line_and_its_absences()
    {
        const string counted =
            """
            {"v":1,"type":"turn_complete","seq":9,"ts":"now","payload":{"input":"look","output":"You are in a room.","elapsed_ms":4,"turn":7,"awaiting_command":true,"machinery":false,"location":180,"score":0,"moves":19}}
            """;

        var turn = JsonSerializer.Deserialize<Envelope>(counted, ProtocolJson.Options)!
            .PayloadAs<TurnCompletePayload>(out var problem);

        Assert.Null(problem);
        Assert.Equal(180, turn!.Location);
        Assert.Equal(0, turn.Score);
        Assert.Equal(19, turn.Moves);
        // This game keeps counters rather than a clock, and the clock's absence is not midnight.
        Assert.Null(turn.Hours);
        Assert.Null(turn.Minutes);

        const string quiet =
            """
            {"v":1,"type":"turn_complete","seq":10,"ts":"now","payload":{"input":"look","output":"You are in a room.","elapsed_ms":4,"turn":8,"awaiting_command":true,"machinery":false}}
            """;

        var bare = JsonSerializer.Deserialize<Envelope>(quiet, ProtocolJson.Options)!
            .PayloadAs<TurnCompletePayload>(out var bareProblem);

        Assert.Null(bareProblem);
        Assert.Null(bare!.Location);
        Assert.Null(bare.Score);
        Assert.Null(bare.Moves);
    }

    /// <summary>
    /// A payload that genuinely does not fit must say so, and say why. The property name and the
    /// JSON path are in the message, which is the difference between "not for me" and "we disagree
    /// about a type" — and without the reason there is no louder failure mode anywhere in the loop.
    /// </summary>
    [Fact]
    public void An_unreadable_payload_reports_the_reason()
    {
        const string line =
            """
            {"v":1,"type":"welcome","seq":1,"ts":"now","payload":{"game_stem":"zork1","turn_count":"twelve"}}
            """;

        var envelope = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;
        var welcome = envelope.PayloadAs<WelcomePayload>(out var problem);

        Assert.Null(welcome);
        Assert.NotNull(problem);
        Assert.Contains("WelcomePayload", problem);
        Assert.Contains("turn_count", problem);
    }

    [Fact]
    public void Unknown_fields_are_ignored_so_the_protocol_can_grow()
    {
        const string line =
            """
            {"v":1,"type":"turn_complete","seq":3,"ts":"now","payload":{"input":"look","output":"forest","elapsed_ms":12,"turn":1,"something_new":true}}
            """;

        var envelope = JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!;
        var turn = envelope.PayloadAs<TurnCompletePayload>()!;

        Assert.Equal("look", turn.Input);
        Assert.Equal("forest", turn.Output);
        Assert.Equal(12, turn.ElapsedMs);
        Assert.Equal(1, turn.Turn);
    }

    [Fact]
    public void Timestamps_are_rfc3339_utc()
    {
        var stamp = Timestamps.Rfc3339(new DateTimeOffset(2026, 9, 11, 13, 45, 1, TimeSpan.FromHours(2)));
        Assert.Equal("2026-09-11T11:45:01Z", stamp);
    }

    [Fact]
    public async Task Reading_skips_malformed_lines_and_yields_the_rest()
    {
        var diagnostics = new List<string>();
        var input = new StringReader(
            "not json\n"
            + "\n"
            + "{\"v\":1,\"type\":\"chat\",\"seq\":1,\"ts\":\"t\",\"payload\":{\"text\":\"hi\"}}\n");

        await using var seat = new SeatConnection(input, new StringWriter(), diagnostics.Add);
        var received = new List<Envelope>();
        await foreach (var envelope in seat.ReadAllAsync(CancellationToken.None))
        {
            received.Add(envelope);
        }

        Assert.Single(received);
        Assert.Equal("hi", received[0].StringField("text"));
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task End_of_input_ends_the_stream()
    {
        await using var seat = new SeatConnection(new StringReader(string.Empty), new StringWriter());
        var count = 0;
        await foreach (var _ in seat.ReadAllAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Writing_produces_one_json_object_per_line_with_increasing_sequence()
    {
        var output = new CapturingWriter();
        await using (var seat = new SeatConnection(new StringReader(string.Empty), output))
        {
            await seat.SendAsync(MessageTypes.Hello, new { protocol_version = SeatProtocol.Version, client_name = "test" });
            await seat.SendAsync(MessageTypes.Chat, new { text = "hello" });
            await seat.SendAsync(MessageTypes.GameInput, new { line = "look" });

            var lines = await WaitForLinesAsync(output, 3);
            var envelopes = lines.Select(line => JsonSerializer.Deserialize<Envelope>(line, ProtocolJson.Options)!).ToList();

            Assert.Equal([1UL, 2UL, 3UL], envelopes.Select(e => e.Seq).ToArray());
            Assert.Equal(MessageTypes.Hello, envelopes[0].Type);
            Assert.Equal(MessageTypes.Chat, envelopes[1].Type);
            Assert.Equal("look", envelopes[2].StringField("line"));
            Assert.All(envelopes, e => Assert.Equal((ulong)SeatProtocol.Version, e.V));
        }

        Assert.DoesNotContain("\r", output.Text);
    }

    private static async Task<List<string>> WaitForLinesAsync(CapturingWriter writer, int count)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var lines = writer.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= count)
            {
                return lines.ToList();
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"expected {count} lines, saw: {writer.Text}");
    }

    /// <summary>A thread-safe sink so the test can read while the writer task writes.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder _builder = new();

        public CapturingWriter() => NewLine = "\n"; // production frames with \n, never \r\n

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_builder)
                {
                    return _builder.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_builder)
            {
                _builder.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_builder)
            {
                _builder.Append(value);
            }
        }
    }
}
