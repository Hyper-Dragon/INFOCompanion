using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Core.Protocol;
using Agent.Llm;

namespace Agent.Core.Logging;

/// <summary>
/// The agent's half of session capture (§13).
/// <para>
/// The core logs what everyone can see — turns, player lines, arbitration events.
/// Only the agent can log what the agent knows: its <c>[think]</c> lines,
/// classification decisions, and per-call token counts and cost. Both files land in
/// <c>state/logs/</c> and can be joined on <c>ts</c>.
/// </para>
/// <para>
/// Two sinks: JSONL for analysis, stderr for the container log. Never stdout — that
/// belongs to the protocol (§6.1).
/// </para>
/// </summary>
public sealed class AgentLog : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _file;
    private readonly string _sessionId;
    private readonly bool _verbose;

    private int _llmCalls;
    private int _truncatedCalls;
    private int _escalatedCalls;
    private int _stagnations;
    private long _promptTokens;
    private long _cacheHitTokens;
    private long _completionTokens;
    private double _cost;
    private long _events;
    private readonly Dictionary<string, int> _truncatedByPurpose = new(StringComparer.Ordinal);

    /// <summary>
    /// Every event, by kind, so a sitting can report a rate about itself rather than a prose line a
    /// reviewer has to grep for (`§30` item 3). Cheap: one increment per row, and the rows are
    /// written one at a time anyway.
    /// </summary>
    private readonly Dictionary<string, int> _byKind = new(StringComparer.Ordinal);

    private AgentLog(StreamWriter file, string sessionId, bool verbose)
    {
        _file = file;
        _sessionId = sessionId;
        _verbose = verbose;
    }

    public string SessionId => _sessionId;

    public string FilePath { get; private init; } = string.Empty;

    /// <summary>Open the session log, rotating old ones out (§5.4, §13).</summary>
    public static AgentLog Start(string logsDirectory, int keep, bool verbose = false)
    {
        Directory.CreateDirectory(logsDirectory);
        Rotate(logsDirectory, keep);

        var sessionId = "agent-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(logsDirectory, sessionId + ".jsonl");
        var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        var log = new AgentLog(writer, sessionId, verbose) { FilePath = path };
        log.Event("session_start", new { protocol = SeatProtocol.Version, pid = Environment.ProcessId });
        return log;
    }

    /// <summary>Append one machine-readable event.</summary>
    public void Event(string kind, object? fields = null)
    {
        var payload = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields, ProtocolJson.Options) as JsonObject ?? new JsonObject();
        payload["ts"] = Timestamps.Rfc3339Now();
        payload["kind"] = kind;
        payload["session"] = _sessionId;

        lock (_gate)
        {
            _events++;
            _byKind[kind] = _byKind.TryGetValue(kind, out var seen) ? seen + 1 : 1;
            _file.WriteLine(payload.ToJsonString());
        }
    }

    /// <summary>How many rows of one kind this session has written. Zero when there are none.</summary>
    public int CountOf(string kind)
    {
        lock (_gate)
        {
            return _byKind.TryGetValue(kind, out var seen) ? seen : 0;
        }
    }

    /// <summary>
    /// How often a pass announced something and did not do it, per turn that landed.
    /// <para>
    /// Null rather than zero when no turn landed: a rate over no turns is not a rate, and the
    /// difference matters — the reader is asking whether the sitting was bad, and zero over zero is
    /// not an answer to it.
    /// </para>
    /// </summary>
    private double? MissedPerTurn()
    {
        var turns = CountOf("turn");
        return turns == 0 ? null : Math.Round((double)CountOf("act_missed") / turns, 3);
    }

    public void Info(string message, object? fields = null) => Write("INFO", message, fields, toStderr: true);

    public void Warn(string message, object? fields = null) => Write("WARN", message, fields, toStderr: true);

    public void Error(string message, object? fields = null) => Write("ERROR", message, fields, toStderr: true);

    /// <summary>Log a Think/Say/Ask/Act line, which is where the persona lives.</summary>
    public void CompanionLine(string kind, string text)
    {
        Event("companion_" + kind, new { text });
        if (_verbose)
        {
            Console.Error.WriteLine($"[{kind}] {text}");
        }
    }

    /// <summary>
    /// Record one model call: tokens, cost, how long it took, and whether it was cut.
    /// <para>
    /// The ceiling goes on the row as well as the reply, because a clip is otherwise unreadable: the
    /// only other place it appeared was the number a pass *wanted*, which is neither what was sent nor
    /// what a retry was given, and a bundle with a clip in it could only be argued over rather than
    /// read. A call that came back cut also gets its own <c>reply_truncated</c> event — one row to
    /// count, rather than a warn in prose to search for — and is counted by purpose for the session's
    /// own totals (`§25` item 3).
    /// </para>
    /// </summary>
    /// <param name="maxTokens">The ceiling actually sent, after the clamp.</param>
    /// <param name="escalated">Whether this call was a retry given more room than the pass asked for.</param>
    public void RecordLlmCall(
        string purpose,
        ChatResult result,
        double cost,
        long latencyMs = 0,
        int maxTokens = 0,
        bool escalated = false)
    {
        lock (_gate)
        {
            _llmCalls++;
            _promptTokens += result.Usage.PromptTokens;
            _cacheHitTokens += result.Usage.CacheHitTokens;
            _completionTokens += result.Usage.CompletionTokens;
            _cost += cost;

            if (result.WasTruncated)
            {
                _truncatedCalls++;
                _truncatedByPurpose[purpose] = _truncatedByPurpose.GetValueOrDefault(purpose) + 1;
            }

            if (escalated)
            {
                _escalatedCalls++;
            }
        }

        Event("llm_call", new
        {
            purpose,
            model = result.Model,
            prompt_tokens = result.Usage.PromptTokens,
            cache_hit_tokens = result.Usage.CacheHitTokens,
            cache_miss_tokens = result.Usage.CacheMissTokens,
            completion_tokens = result.Usage.CompletionTokens,
            // How long the player waited, which is the half of "cost" that a person can feel.
            // Measured around the call rather than inferred from timestamps afterwards: the
            // pane's silence and this number are the same thing.
            latency_ms = latencyMs,
            cost_usd = Math.Round(cost, 6),
            cost_total_usd = Math.Round(Cost, 6),
            finish_reason = result.FinishReason.ToString().ToLowerInvariant(),
            truncated = result.WasTruncated,
            // What the pass was allowed, and whether it was allowed more because it had already been
            // cut off once (04_companion.md §3.2 (`C23.4`)). Zero means the caller did not say, which
            // is a call made outside a pass rather than a ceiling of nothing.
            max_tokens = maxTokens,
            escalated,
        });

        if (result.WasTruncated)
        {
            Event("reply_truncated", new
            {
                purpose,
                max_tokens = maxTokens,
                escalated,
                prompt_tokens = result.Usage.PromptTokens,
                completion_tokens = result.Usage.CompletionTokens,
            });
        }
    }

    /// <summary>How many calls came back cut off, and which passes they were.</summary>
    public (int Total, IReadOnlyDictionary<string, int> ByPurpose) Truncated
    {
        get
        {
            lock (_gate)
            {
                return (_truncatedCalls, new Dictionary<string, int>(_truncatedByPurpose, StringComparer.Ordinal));
            }
        }
    }

    /// <summary>How many calls were retries given more room after a clip.</summary>
    public int EscalatedCalls
    {
        get
        {
            lock (_gate)
            {
                return _escalatedCalls;
            }
        }
    }

    /// <summary>
    /// Record a stretch of play with nothing new in it, at the moment the harness **acted** on it:
    /// how long the window was, how many of those turns went somewhere, and which pass was asked.
    /// <para>
    /// This is not the sentence that rides in the constraint, which is true of every call for as long
    /// as the stretch lasts. What a review cannot get from that sentence is whether the harness
    /// *noticed* — whether the stretch was ever put to the companion as something to decide about —
    /// and a count of the times it was is the fact that says so (`§25` item 4).
    /// </para>
    /// </summary>
    public void RecordStagnation(string purpose, int turns, int moved)
    {
        lock (_gate)
        {
            _stagnations++;
        }

        Event("stagnation", new { purpose, turns, moved });
    }

    /// <summary>How many stretches with nothing new in them the harness put to the companion.</summary>
    public int Stagnations
    {
        get
        {
            lock (_gate)
            {
                return _stagnations;
            }
        }
    }

    public int LlmCalls
    {
        get
        {
            lock (_gate)
            {
                return _llmCalls;
            }
        }
    }

    public long PromptTokens
    {
        get
        {
            lock (_gate)
            {
                return _promptTokens;
            }
        }
    }

    public long CompletionTokens
    {
        get
        {
            lock (_gate)
            {
                return _completionTokens;
            }
        }
    }

    public double Cost
    {
        get
        {
            lock (_gate)
            {
                return _cost;
            }
        }
    }

    /// <summary>
    /// The sitting is over — the player has gone — so the numbers so far are written down.
    /// <para>
    /// This exists because the other place they are written, <see cref="DisposeAsync"/>, is a place a
    /// sitting does not reliably reach: the agent is one of four processes in a container, nothing gives
    /// it a graceful exit, and a killed process does not run `await using`. Measured 2026-10-01: after a
    /// full sitting, a player disconnecting, a `docker compose restart` **and** a graceful stop, no log
    /// carried a `session_end` at all — so every count in it was unreadable by the only reader it was
    /// written for. A fact about a sitting belongs where the sitting ends (`03_session.md` §9 (`S61`)).
    /// </para>
    /// </summary>
    public void SittingEnded() => WriteSummary("sitting_end", "the player has gone; the sitting's numbers");

    /// <summary>
    /// The totals, as an event and as a line, under the kind that says which ending this was.
    /// </summary>
    private void WriteSummary(string kind, string message)
    {
        Event(kind, new
        {
            llm_calls = _llmCalls,
            prompt_tokens = _promptTokens,
            completion_tokens = _completionTokens,
            cost_usd = Math.Round(_cost, 6),
            // Clipping, as a number rather than as prose to search for: how many calls came back cut,
            // which passes they were, and how many retries were given more room because of one
            // (`§25` item 3). A sitting's own report can then say the rate instead of arguing about it.
            truncated_calls = _truncatedCalls,
            truncated_by_purpose = _truncatedByPurpose,
            escalated_calls = _escalatedCalls,
            // And how many stretches with nothing new in them the companion was asked to decide about:
            // zero is a fact about the sitting rather than an absence of one (`C39.4`).
            stagnation_consulted = _stagnations,
            // What the repair loop cost, as numbers rather than as prose to search for (`§30` item 3).
            // `act_missed` is the headline the log has always been read by counting — a pass that
            // announced something and did not do it — and against the turns that landed it is the rate
            // six sittings were read for by hand: 0.125, 0.14, 0.20, 0.23, 0.43 and 0.24 to 0.54, with
            // nothing anywhere recording it. The wobble was the most visible thing in the transcript
            // and the least measurable, which is the whole reason this row exists.
            missed_acts = CountOf("act_missed"),
            landed_turns = CountOf("turn"),
            missed_per_turn = MissedPerTurn(),
            events = _events,
        });

        Info(
            message,
            new
            {
                llm_calls = _llmCalls,
                prompt_tokens = _promptTokens,
                completion_tokens = _completionTokens,
                cost_usd = Math.Round(_cost, 6),
                truncated_calls = _truncatedCalls,
                escalated_calls = _escalatedCalls,
                stagnation_consulted = _stagnations,
                missed_acts = CountOf("act_missed"),
                missed_per_turn = MissedPerTurn(),
            });
    }

    public async ValueTask DisposeAsync()
    {
        WriteSummary("session_end", "session closed");

        lock (_gate)
        {
            _file.Flush();
        }

        await _file.DisposeAsync().ConfigureAwait(false);
    }

    private void Write(string level, string message, object? fields, bool toStderr)
    {
        var detail = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields, ProtocolJson.Options) as JsonObject ?? new JsonObject();
        detail["level"] = level;
        detail["msg"] = message;

        Event(level.ToLowerInvariant(), new { msg = message, detail = fields });

        if (!toStderr)
        {
            return;
        }

        // One line per event, JSON tail so the container log stays greppable.
        var builder = new StringBuilder();
        builder.Append(Timestamps.Rfc3339Now()).Append(' ').Append(level.PadRight(5)).Append(' ').Append(message);
        var tail = fields is null ? null : JsonSerializer.Serialize(fields, ProtocolJson.Options);
        if (!string.IsNullOrEmpty(tail) && tail != "{}")
        {
            builder.Append(" | ").Append(tail);
        }

        Console.Error.WriteLine(builder.ToString());
    }

    /// <summary>Keep only the newest <paramref name="keep"/> agent logs (§13).</summary>
    private static void Rotate(string logsDirectory, int keep)
    {
        if (keep <= 0)
        {
            return;
        }

        try
        {
            var files = Directory
                .EnumerateFiles(logsDirectory, "agent-*.jsonl")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();

            if (files.Count <= keep - 1)
            {
                return;
            }

            foreach (var stale in files.Take(files.Count - (keep - 1)))
            {
                File.Delete(stale);
            }
        }
        catch (IOException)
        {
            // Rotation is housekeeping; never let it stop the companion.
        }
    }
}
