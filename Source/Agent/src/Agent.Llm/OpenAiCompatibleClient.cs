using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Llm;

/// <summary>
/// A thin client for any <b>OpenAI-compatible</b> chat endpoint (§9.3).
/// <para>
/// Deliberately small: an <see cref="HttpClient"/>, SSE parsing, retry/backoff and
/// readable errors. No agent framework, no SDK — the model is an HTTP endpoint and
/// pretending otherwise has cost us more than it ever saved.
/// </para>
/// <para>
/// What differs between backends — where the token counts live, and how a thinking preference is
/// asked for — is not decided here: it is described by a <see cref="ProviderProfile"/>
/// (10_delivery.md §8.1 (`D18`)), so this file has no favourite vendor in it.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmSettings _settings;
    private readonly Action<string>? _log;
    private readonly bool _ownsHttp;

    /// <summary>Whether the once-only warning about a `<c>thinking</c>` shape nobody knows has been made.</summary>
    private int _warnedAboutThinking;

    public OpenAiCompatibleClient(LlmSettings settings, string apiKey, Action<string>? log = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmFatalException(
                "no API key: set INFOCOMPANION_LLM_API_KEY. It is never read from the repo or the config file.");
        }

        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        _http.Timeout = Timeout.InfiniteTimeSpan;

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The endpoint actually used, for logs and diagnostics.</summary>
    public string Endpoint => BuildEndpoint(_settings.BaseUrl);

    /// <summary>Send the conversation and return the reply.</summary>
    /// <param name="options">
    /// Per-call overrides. Null means "whatever the settings say", which is the ordinary case —
    /// these exist for the passes that are not the companion talking to anyone (10_delivery.md §8.3 (`D21`) and 05_memory.md §2 (`M11`)).
    /// </param>
    public async Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken,
        CallOptions? options = null,
        Func<string, Task>? onReasoning = null)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await AttemptAsync(messages, cancellationToken, options, onReasoning).ConfigureAwait(false);
            }
            catch (LlmRetryableException ex) when (attempt < _settings.MaxRetries)
            {
                attempt++;
                var delay = ex.RetryAfter ?? Backoff(attempt, _settings.MaxBackoffSeconds);
                _log?.Invoke(
                    $"llm retry {attempt}/{_settings.MaxRetries} in {delay.TotalSeconds:F1}s: {ex.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ChatResult> AttemptAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken,
        CallOptions? options,
        Func<string, Task>? onReasoning)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.RequestTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Content = new StringContent(BuildBody(messages, options), Encoding.UTF8, "application/json");

        try
        {
            // A retry after a fault mid-stream repeats whatever the first attempt already sent.
            // That is a stutter in the pane and nothing worse: the finished thought is the
            // record, and the log keeps it once.
            return _settings.Stream
                ? await StreamAsync(request, timeout.Token, onReasoning).ConfigureAwait(false)
                : await OnceAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmRetryableException(
                $"the model did not answer within {_settings.RequestTimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmRetryableException($"could not reach the model endpoint: {ex.Message}", null, ex);
        }
    }

    /// <summary>Non-streaming call: one request, one body.</summary>
    private async Task<ChatResult> OnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var content = string.Empty;
        var reasoning = string.Empty;
        var toolCalls = new List<ToolCall>();
        var finish = FinishReason.Other;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var messageContent)
                    && messageContent.ValueKind == JsonValueKind.String)
                {
                    content = messageContent.GetString() ?? string.Empty;
                }

                if (message.TryGetProperty("reasoning_content", out var thinking)
                    && thinking.ValueKind == JsonValueKind.String)
                {
                    reasoning = thinking.GetString() ?? string.Empty;
                }

                toolCalls = ReadToolCalls(message);
            }

            finish = ParseFinish(choice);
        }

        var usage = root.TryGetProperty("usage", out var usageElement) ? ReadUsage(usageElement) : Usage.Empty;
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? _settings.Model
            : _settings.Model;

        return new ChatResult(content, usage, model, finish)
        {
            Reasoning = reasoning,
            ToolCalls = toolCalls,
        };
    }

    /// <summary>Streaming call over SSE, so the first token arrives promptly.</summary>
    private async Task<ChatResult> StreamAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<string, Task>? onReasoning = null)
    {
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var partial = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        var usage = Usage.Empty;
        var finish = FinishReason.Other;
        var model = _settings.Model;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            using var chunk = JsonDocument.Parse(payload);
            var root = chunk.RootElement;

            if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString() ?? model;
            }

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = ReadUsage(usageElement);
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            var candidate = ParseFinish(choice);
            if (candidate != FinishReason.Other || choice.TryGetProperty("finish_reason", out _))
            {
                finish = candidate;
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var deltaContent)
                    && deltaContent.ValueKind == JsonValueKind.String)
                {
                    content.Append(deltaContent.GetString());
                }

                if (delta.TryGetProperty("reasoning_content", out var deltaThinking)
                    && deltaThinking.ValueKind == JsonValueKind.String)
                {
                    var piece = deltaThinking.GetString() ?? string.Empty;
                    reasoning.Append(piece);

                    // Away it goes, there and then: this is the difference between a companion
                    // thinking in front of the player and a screen that has stopped answering.
                    if (onReasoning is not null && piece.Length > 0)
                    {
                        await onReasoning(piece).ConfigureAwait(false);
                    }
                }

                AccumulateTools(delta, partial);
            }
        }

        return new ChatResult(content.ToString(), usage, model, finish)
        {
            Reasoning = reasoning.ToString(),
            ToolCalls = Collected(partial),
        };
    }

    /// <summary>
    /// Fold one streamed chunk's tool-call fragments into the accumulator.
    /// <para>
    /// Tool calls arrive in pieces: a name in one chunk, the arguments in dribs over several,
    /// and more than one call interleaved — which is why they are keyed by the <c>index</c> the
    /// provider sends rather than appended in arrival order (10_delivery.md §8.1 (`D18`–`D19`) lists this as a named
    /// quirk of "OpenAI-compatible" backends).
    /// </para>
    /// </summary>
    private static void AccumulateTools(JsonElement delta, Dictionary<int, (string Id, string Name, StringBuilder Args)> partial)
    {
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var position) && position.ValueKind == JsonValueKind.Number
                ? position.GetInt32()
                : partial.Count;

            if (!partial.TryGetValue(index, out var accumulator))
            {
                accumulator = (string.Empty, string.Empty, new StringBuilder());
            }

            if (call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                accumulator.Id += idElement.GetString();
            }

            if (call.TryGetProperty("function", out var function))
            {
                if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    accumulator.Name += name.GetString();
                }

                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                {
                    accumulator.Args.Append(arguments.GetString());
                }
            }

            partial[index] = accumulator;
        }
    }

    /// <summary>The accumulated calls, in the order the model numbered them.</summary>
    private static List<ToolCall> Collected(IReadOnlyDictionary<int, (string Id, string Name, StringBuilder Args)> partial)
    {
        var calls = new List<ToolCall>();
        foreach (var index in partial.Keys.OrderBy(position => position))
        {
            var (id, name, arguments) = partial[index];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // A replayed call needs an id the tool result can answer (04_companion.md §3 (`C14`–`C25`)). The
            // provider sends real ones while streaming; the fallback keeps the wire valid
            // for anything that does not.
            calls.Add(new ToolCall(
                string.IsNullOrEmpty(id) ? $"call_{index}" : id,
                name,
                arguments.Length == 0 ? "{}" : arguments.ToString()));
        }

        return calls;
    }

    /// <summary>Read the calls out of a non-streamed message.</summary>
    private static List<ToolCall> ReadToolCalls(JsonElement message)
    {
        var calls = new List<ToolCall>();
        if (!message.TryGetProperty("tool_calls", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var call in element.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var function))
            {
                continue;
            }

            var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var arguments = function.TryGetProperty("arguments", out var argsElement) ? argsElement.GetString() : null;
            calls.Add(new ToolCall(id ?? string.Empty, name, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        return calls;
    }

    /// <summary>
    /// One message as the wire wants it. A replayed assistant turn carries the calls it made
    /// and the reasoning that came with them (04_companion.md §3 (`C14`–`C25`)); a tool result answers a call by
    /// id; everything else is role and content, as it always was.
    /// </summary>
    private static object WireMessage(ChatMessage message)
    {
        if (message.Role == "tool")
        {
            return new
            {
                role = "tool",
                tool_call_id = message.ToolCallId ?? string.Empty,
                content = message.Content,
            };
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            var replay = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = message.Content.Length == 0 ? null : message.Content,
                ["tool_calls"] = calls.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new { name = call.Name, arguments = call.ArgumentsJson },
                }).ToArray(),
            };

            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                replay["reasoning_content"] = message.Reasoning;
            }

            return replay;
        }

        return new { role = message.Role, content = message.Content };
    }

    /// <summary>Build the OpenAI-compatible request body (§9.3, §10.1).</summary>
    private string BuildBody(IReadOnlyList<ChatMessage> messages, CallOptions? options)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _settings.Model,
            ["messages"] = messages.Select(WireMessage).ToArray(),
            // The sampling parameters travel whether or not the backend is reasoning: it may accept
            // and silently ignore them while thinking is on (10_delivery.md §8.1 (`D19`)), and dropping them
            // would change every pass that is not reasoning instead.
            ["temperature"] = _settings.Temperature,
            ["max_tokens"] = options?.MaxTokens ?? _settings.MaxTokens,
            ["top_p"] = _settings.TopP,
            ["presence_penalty"] = _settings.PresencePenalty,
            ["frequency_penalty"] = _settings.FrequencyPenalty,
            ["stream"] = _settings.Stream,
        };

        // A thinking preference is only sent when the config has an opinion **and** the profile says
        // this backend takes it in that shape (10_delivery.md §8.1 (`D18`)): `thinking: {"type": …}` is one
        // dialect's field, and a field that one member of a dialect accepts is the commonest way a
        // request is refused somewhere else. A backend with no such shape is told once rather than
        // left quiet — a knob that does nothing and says nothing is the defect (`D11.1`).
        // A per-call override wins over the settings: this is how a maintenance pass runs without
        // private reasoning while the companion's own replies keep it.
        var thinking = options?.Thinking ?? _settings.Thinking;
        if (thinking is { } enabled && _settings.Provider.TakesThinkingObject)
        {
            payload["thinking"] = new { type = enabled ? "enabled" : "disabled" };
        }
        else if (thinking is not null && Interlocked.Exchange(ref _warnedAboutThinking, 1) == 0)
        {
            _log?.Invoke(
                $"provider '{_settings.Provider.Name}' has no known shape for `thinking`, so none was sent; `reasoning_effort` is the lever that travels");
        }

        var effort = options?.ReasoningEffort ?? _settings.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(effort))
        {
            payload["reasoning_effort"] = effort;
        }

        if (_settings.Stream)
        {
            // Asking for the token counts on a streamed call: some backends answer nothing about
            // usage unless asked, and asking is the dialect's own field rather than one vendor's.
            payload["stream_options"] = new { include_usage = true };
        }

        // The world is a tool surface (04_companion.md §2 (`C6`–`C13`)): everything the companion *does* is a call,
        // because a schema cannot be mis-bracketed and a delimiter contract is only as good as
        // the examples in context. Speech is not a tool — it is the content.
        if (options?.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = tools
                .Select(tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = JsonNode.Parse(tool.Input.Json),
                    },
                })
                .ToArray();

            // `auto` unless the pass has no prose-only outcome (see `CallOptions.RequireATool`):
            // the errand's step, where a reply that says it is going and carries no call is not a
            // step at all. The provider is asked with the dialect's own field rather than with
            // another sentence, which is the only lever the harness has left once the brief already
            // says it — and the one the live probe confirms this backend accepts.
            payload["tool_choice"] = options.RequireATool ? "required" : "auto";
        }

        return JsonSerializer.Serialize(payload);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = ExtractApiMessage(body);
        var message = $"the model returned HTTP {status}: {Truncate(detail, 400)}";

        switch (status)
        {
            case 400:
                throw new LlmFatalException($"{message} — the request was rejected; check agent-config.json.");
            case 401:
            case 403:
                throw new LlmFatalException($"{message} — the API key was rejected; check INFOCOMPANION_LLM_API_KEY.");
            case 402:
                throw new LlmFatalException($"{message} — the account is out of credit.");
            case 429:
                throw new LlmRetryableException($"{message} — rate limited.", RetryAfter(response));
            case >= 500:
                throw new LlmRetryableException($"{message} — the model is having trouble.", RetryAfter(response));
            default:
                throw new LlmFatalException(message);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return null;
    }

    /// <summary>
    /// 1s, 2s, 4s… with a little jitter so repeated failures do not sync up, capped at the
    /// configured ceiling: a provider that is down stays down, and an uncapped ladder would push
    /// the next attempt past the end of the evening.
    /// </summary>
    private static TimeSpan Backoff(int attempt, int maxSeconds)
    {
        var seconds = Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.25;
        return TimeSpan.FromSeconds(Math.Min(maxSeconds, seconds + jitter));
    }

    private static FinishReason ParseFinish(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return FinishReason.Other;
        }

        return element.GetString() switch
        {
            "stop" => FinishReason.Stop,
            "length" => FinishReason.Length,
            "tool_calls" => FinishReason.ToolCalls,
            _ => FinishReason.Other,
        };
    }

    private Usage ReadUsage(JsonElement usage)
    {
        var prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var promptTokens)
            ? promptTokens
            : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var completionTokens)
            ? completionTokens
            : 0;

        var cacheHit = 0;
        foreach (var field in _settings.Provider.CacheHitFields)
        {
            if (TryReadInt(usage, field, out var value))
            {
                cacheHit = value;
                break;
            }
        }

        // Clamped to the prompt: a provider reporting more cached tokens than prompt tokens
        // would otherwise drive CacheMissTokens negative and the cost strange.
        return new Usage(prompt, completion)
        {
            CacheHitTokens = Math.Clamp(cacheHit, 0, prompt),
        };
    }

    /// <summary>
    /// Read an integer at a dotted path inside an object, e.g.
    /// <c>prompt_tokens_details.cached_tokens</c>. Missing anywhere along the path means "not
    /// reported", not an error: the shape of `usage` is the provider's business.
    /// </summary>
    private static bool TryReadInt(JsonElement element, string path, out int value)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                value = 0;
                return false;
            }

            current = next;
        }

        if (current.ValueKind != JsonValueKind.Number || !current.TryGetInt32(out value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    private static string ExtractApiMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no detail given";
        }

        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through and use the raw body.
        }

        return body.Trim();
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // The version path is the config's business, not ours: some backends take the bare host and
        // others want `/v1`, and appending one on a guess is how a working endpoint stops working.
        return trimmed + "/chat/completions";
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
