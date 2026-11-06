using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Agent.Core.Protocol;

/// <summary>
/// The agent's end of the stdio seat protocol.
/// <para>
/// Two inviolable rules, both structural rather than conventional:
/// </para>
/// <list type="number">
/// <item><b>stdout carries protocol bytes only.</b> Nothing else may write to it;
/// every log line goes to stderr, and the core asserts the same on its side
/// (§6.1).</item>
/// <item><b>one writer.</b> Messages are queued and serialised by a single task, so
/// two protocol lines can never interleave however many callers there are
/// (§6.6).</item>
/// </list>
/// </summary>
public sealed class SeatConnection : IAsyncDisposable
{
    private readonly Channel<OutboundMessage> _outbound = Channel.CreateUnbounded<OutboundMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Action<string> _diagnostics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    /// <summary>
    /// How long the writer is waited for on the way out, from <c>seat_writer_wait_seconds</c>.
    /// </summary>
    private readonly TimeSpan _writerWait;

    private ulong _sequence;
    private long _sent;
    private long _received;

    public SeatConnection(
        TextReader input,
        TextWriter output,
        Action<string>? diagnostics = null,
        int writerWaitSeconds = Config.ConfigDefaults.SeatWriterWaitSeconds)
    {
        _input = input;
        _output = output;
        _diagnostics = diagnostics ?? (_ => { });
        _writerWait = TimeSpan.FromSeconds(writerWaitSeconds);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Envelopes written to the core.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Envelopes read from the core.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Queue one message. Safe to call from anywhere; the writer serialises.</summary>
    public ValueTask SendAsync(string type, object? payload = null)
    {
        JsonNode? node = payload is null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, ProtocolJson.Options);
        return _outbound.Writer.WriteAsync(new OutboundMessage(type, node));
    }

    /// <summary>Read envelopes until the core closes its end of the pipe.</summary>
    public async IAsyncEnumerable<Envelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (IOException ex)
            {
                _diagnostics($"stdin failed: {ex.Message}");
                yield break;
            }

            if (line is null)
            {
                yield break; // the core is gone: clean EOF, the agent should exit
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            Envelope? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<Envelope>(trimmed, ProtocolJson.Options);
            }
            catch (JsonException ex)
            {
                _diagnostics($"ignoring a malformed protocol line: {ex.Message}");
            }

            if (envelope is null)
            {
                continue;
            }

            Interlocked.Increment(ref _received);
            yield return envelope;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                _sequence++;
                var envelope = new Envelope
                {
                    V = SeatProtocol.Version,
                    Type = message.Type,
                    Seq = _sequence,
                    Ts = Timestamps.Rfc3339Now(),
                    Payload = message.Payload,
                };

                var line = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
                await _output.WriteLineAsync(line).ConfigureAwait(false);
                await _output.FlushAsync().ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (IOException ex)
        {
            _diagnostics($"stdout failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(_writerWait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: we are on the way out.
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private readonly record struct OutboundMessage(string Type, JsonNode? Payload);
}
