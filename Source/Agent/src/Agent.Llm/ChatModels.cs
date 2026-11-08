using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Llm;

/// <summary>One message in the conversation sent to the model.</summary>
public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);

    public static ChatMessage User(string content) => new("user", content);

    public static ChatMessage Assistant(string content) => new("assistant", content);

    /// <summary>
    /// The calls an assistant message made, when the message is being replayed inside a
    /// round of local tool use (04_companion.md §3 (`C14`–`C25`)). Null on every message that is not such a
    /// replay.
    /// </summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// The reasoning that came with a replayed assistant message. The provider wants it
    /// echoed back alongside the calls it produced.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>For a tool result: which call it answers. Null on everything else.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>One tool result, answering one call by id.</summary>
    public static ChatMessage ToolResult(string callId, string content) =>
        new("tool", content) { ToolCallId = callId };
}

/// <summary>
/// Token accounting for one call.
/// <para>
/// The cache split is the figure that explains a bill. A provider that caches a prompt prefix
/// bills the part it recognised far below the part it did not, so two calls with identical
/// <see cref="PromptTokens"/> can differ in price by an order of magnitude — and until this was
/// read, the difference was invisible (10_delivery.md §8.4 (`D22`–`D24`)).
/// </para>
/// </summary>
public sealed record Usage(int PromptTokens, int CompletionTokens)
{
    public static Usage Empty { get; } = new(0, 0);

    /// <summary>
    /// Prompt tokens the provider recognised from its cache.
    /// <para>
    /// Zero when it does not report a split, which is **not** the same as "nothing was
    /// cached": see <see cref="ProviderProfile.ReportsCacheSplit"/>. A reader who needs the
    /// distinction has it; everything downstream can treat zero as "bill it at full rate".
    /// </para>
    /// </summary>
    public int CacheHitTokens { get; init; }

    /// <summary>Prompt tokens billed at the full rate. Derived, so only one field is set.</summary>
    public int CacheMissTokens => Math.Max(0, PromptTokens - CacheHitTokens);

    public int TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>Was any of the prompt served from the provider's cache?</summary>
    public bool HadCacheHit => CacheHitTokens > 0;
}

/// <summary>Why the model stopped generating.</summary>
public enum FinishReason
{
    /// <summary>Ran to a natural stop.</summary>
    Stop,

    /// <summary>Hit the max_tokens ceiling: the reply is incomplete.</summary>
    Length,

    /// <summary>Anything else the API reported.</summary>
    Other,

    /// <summary>The model stopped because it wants a tool run.</summary>
    ToolCalls,
}

/// <summary>The assistant's reply plus what it cost.</summary>
public sealed record ChatResult(
    string Content,
    Usage Usage,
    string Model,
    FinishReason FinishReason)
{
    /// <summary>
    /// The working, before the answer (04_companion.md §4 (`C26`–`C28`)).
    /// <para>
    /// The provider reports this separately from <see cref="Content" />, and that split is
    /// exactly what the spec asks for: the reasoning is what the companion is thinking out
    /// loud, and the content is what it says. It is published to the player and never parsed
    /// — which is the whole point, because the old contract made the model label its own
    /// thinking with a <c>[think]</c> tag and the harness then threw the label away.
    /// </para>
    /// </summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>
    /// What the companion asked the world to do (04_companion.md §2 (`C6`–`C13`)). Empty is the common case: a
    /// turn with nothing to do but talk.
    /// </summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>
    /// True when the reply was cut off by <c>max_tokens</c>.
    /// <para>
    /// This is now the authoritative signal that a reply is incomplete, and it replaces a
    /// guess: the old contract inferred truncation from an <em>unclosed</em> block, which is a
    /// property of the markup rather than of the reply. A tool call cut off mid-argument is
    /// detected here instead, and so is a notebook that stops mid-sentence.
    /// </para>
    /// </summary>
    public bool WasTruncated => FinishReason == FinishReason.Length;
}

/// <summary>
/// One tool the companion may call (04_companion.md §2 (`C6`–`C13`)).
/// <para>
/// The parameters are JSON Schema 2020-12, written out by hand rather than generated. There are a
/// dozen tools and their schemas are a few lines each: a generator would be more machinery than the
/// thing it generates, and it would hide from a reader exactly what the model is told. What the type
/// buys over a bare string is that a schema which will not parse, or which requires an argument it
/// does not declare, fails at *startup* rather than at the provider — where the failure mode is every
/// call failing at once.
/// </para>
/// <para>
/// Built through <see cref="Of"/> rather than constructed, so that gate cannot be skipped by adding
/// a tool the quick way.
/// </para>
/// </summary>
public sealed class ToolDefinition
{
    private ToolDefinition(
        string name,
        string description,
        ToolSchema input,
        ToolAnnotations annotations,
        ToolSchema? output)
    {
        Name = name;
        Description = description;
        Input = input;
        Annotations = annotations;
        Output = output;
    }

    /// <summary>The stable name: what the model calls, and what any adapter would key on.</summary>
    public string Name { get; }

    /// <summary>What the tool is for, in the model's own reading.</summary>
    public string Description { get; }

    /// <summary>The arguments.</summary>
    public ToolSchema Input { get; }

    /// <summary>
    /// What calling it does to the world, in a form code can be held to.
    /// <para>
    /// Not sent to the provider: the wire shape is <c>name</c>, <c>description</c>,
    /// <c>parameters</c>, and an unknown field there is a rejected request. These are for the
    /// harness — the pass subsets and the retrieval round are decided by them — and for whatever
    /// exposes this surface as MCP later, which is where they would finally have a second reader.
    /// </para>
    /// </summary>
    public ToolAnnotations Annotations { get; }

    /// <summary>The result's shape, on the tools that hand back facts rather than prose.</summary>
    public ToolSchema? Output { get; }

    /// <summary>Build one, checking the schemas on the way in.</summary>
    public static ToolDefinition Of(
        string name,
        string description,
        string inputSchema,
        ToolAnnotations annotations,
        string? outputSchema = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("a tool needs a name", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException($"`{name}` has no description: the model would be guessing", nameof(description));
        }

        var input = ToolSchema.Of(name, inputSchema, "input");
        var output = outputSchema is null ? null : ToolSchema.Of(name, outputSchema, "output");
        return new ToolDefinition(name, description, input, annotations, output);
    }
}

/// <summary>
/// A tool's arguments, as a schema that has been read at least once.
/// <para>
/// Two questions are asked here and nowhere else, because both are the difference between a working
/// surface and a provider-side failure nobody can diagnose from the log: does it parse, and does
/// every argument it *requires* actually exist in what it *declares*.
/// </para>
/// </summary>
public sealed class ToolSchema
{
    private ToolSchema(string json, JsonObject root, IReadOnlyList<string> properties, IReadOnlyList<string> required)
    {
        Json = json;
        Root = root;
        Properties = properties;
        Required = required;
    }

    /// <summary>The schema exactly as written, for the wire.</summary>
    public string Json { get; }

    /// <summary>The parsed form, so a question about it is a read rather than a re-parse.</summary>
    public JsonObject Root { get; }

    /// <summary>Every argument the model may supply.</summary>
    public IReadOnlyList<string> Properties { get; }

    /// <summary>The arguments it must supply.</summary>
    public IReadOnlyList<string> Required { get; }

    /// <summary>Read one, or say which tool and which half of it went wrong.</summary>
    public static ToolSchema Of(string tool, string json, string which)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException($"`{tool}` has no {which} schema", nameof(json));
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("the schema is not an object");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"`{tool}`'s {which} schema will not parse: {ex.Message}", nameof(json));
        }

        if (root["type"]?.GetValue<string>() != "object")
        {
            throw new ArgumentException(
                $"`{tool}`'s {which} schema is not an object schema: the provider is told these are "
                + "arguments, and JSON Schema 2020-12 only means that when `type` says `object`",
                nameof(json));
        }

        var properties = Names(root["properties"] as JsonObject);
        var required = Names(root["required"] as JsonArray);

        // The check that earns the type. A `required` name with no property beside it is a schema the
        // model cannot satisfy: the provider reports it as a malformed request, which reads as "the
        // model is broken" from anywhere except here.
        foreach (var name in required)
        {
            if (!properties.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"`{tool}`'s {which} schema requires `{name}` and does not declare it", nameof(json));
            }
        }

        return new ToolSchema(json, root, properties, required);
    }

    private static IReadOnlyList<string> Names(JsonObject? source) =>
        source is null ? [] : [.. source.Select(pair => pair.Key)];

    private static IReadOnlyList<string> Names(JsonArray? source) =>
        source is null
            ? []
            : [.. source.Select(item => item?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0)];
}

/// <summary>
/// What calling a tool does, as three MCP annotations and one of ours.
/// <para>
/// The first three are MCP's, with MCP's meanings, because the point of them is to already be right
/// when this surface is exposed through an adapter. The fourth is not: <see cref="ActsOnTheGame"/>
/// is the harness's own question — *may this call move the player?* — and it exists because that is
/// the invariant the pass subsets are built on, and because neither reading of MCP's vocabulary
/// gives the right answer. Writing a note is not read-only, and taking a step in a maze is not
/// "destructive" in the sense MCP means.
/// </para>
/// </summary>
/// <param name="ReadOnly">It changes nothing at all.</param>
/// <param name="Destructive">It can lose something that was there before.</param>
/// <param name="Idempotent">Calling it twice with the same arguments has the same effect as once.</param>
/// <param name="ActsOnTheGame">It changes the world the player is standing in.</param>
public readonly record struct ToolAnnotations(
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool ActsOnTheGame)
{
    /// <summary>Reading: a lookup, a save list, the notebook. The only kind a round may loop on.</summary>
    public static ToolAnnotations Reads => new(true, false, true, false);

    /// <summary>Writing the companion's own records by replacement, which can lose what was there.</summary>
    public static ToolAnnotations Writes => new(false, true, false, false);

    /// <summary>Writing by addition, where nothing that was there can be lost.</summary>
    public static ToolAnnotations Adds => new(false, false, false, false);

    /// <summary>A control operation: a plan, a question. It changes no record and no world.</summary>
    public static ToolAnnotations Controls => new(false, false, false, false);

    /// <summary>One step in the game, which is the only thing here that moves the world.</summary>
    public static ToolAnnotations Acts => new(false, true, false, true);
}

/// <summary>One call the model made: which tool, and the arguments it wants.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson)
{
    /// <summary>
    /// Read one string argument, or null when it is absent, not a string, or the whole
    /// argument blob will not parse. Never throws: a malformed call is a refusal to report,
    /// not a crash in the middle of a turn.
    /// </summary>
    public string? StringArgument(string name)
    {
        if (string.IsNullOrWhiteSpace(ArgumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(ArgumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Price per 1000 tokens, as configured in <c>agent-config.json</c>.</summary>
public sealed record Pricing(double PromptPer1K, double CompletionPer1K, double CacheHitPer1K = -1)
{
    public static Pricing Free { get; } = new(0, 0);

    /// <summary>
    /// What a cached prompt token costs.
    /// <para>
    /// Defaults to the full prompt rate when the config does not say — so a backend with no
    /// cache, and a config nobody has filled in, both report the old, honest, higher figure
    /// rather than a flattering one. Cost accounting must never be optimistic by accident.
    /// </para>
    /// </summary>
    public double EffectiveCacheHitPer1K => CacheHitPer1K >= 0 ? CacheHitPer1K : PromptPer1K;

    /// <summary>
    /// The cost of one call, with the prompt billed by class. With no cache rate configured
    /// this is exactly the flat calculation it replaces.
    /// </summary>
    public double CostOf(Usage usage) =>
        (usage.CacheMissTokens / 1000.0 * PromptPer1K)
        + (usage.CacheHitTokens / 1000.0 * EffectiveCacheHitPer1K)
        + (usage.CompletionTokens / 1000.0 * CompletionPer1K);

    /// <summary>Published per-model rates, keyed by the name the endpoint is called with.</summary>
    public IReadOnlyDictionary<string, ModelPrices> Models { get; init; } =
        new Dictionary<string, ModelPrices>();

    /// <summary>When peak rates apply, in UTC. Empty means the provider has no peak rate.</summary>
    public IReadOnlyList<PeakWindow> PeakWindowsUtc { get; init; } = [];

    /// <summary>Whether those windows apply on weekdays only.</summary>
    public bool PeakWeekdaysOnly { get; init; } = true;

    /// <summary>
    /// Is this moment inside a published peak window? Some providers halve their rates outside them,
    /// so the same tokens cost different money at different times of the day — and a figure
    /// resolved once at startup would be wrong for whichever half the session did not start in.
    /// </summary>
    public bool IsPeak(DateTimeOffset at)
    {
        if (PeakWindowsUtc.Count == 0)
        {
            return false;
        }

        var utc = at.UtcDateTime;
        if (PeakWeekdaysOnly && utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        return PeakWindowsUtc.Any(window => window.Contains(at));
    }

    /// <summary>
    /// What one call cost, priced at the moment it was made.
    /// <para>
    /// The published table wins when the model is in it; otherwise the flat per-1K rates stand,
    /// which is what lets an existing config keep working unchanged.
    /// </para>
    /// </summary>
    public double CostOfAt(Usage usage, string? model, DateTimeOffset at)
    {
        if (model is null || !Models.TryGetValue(model, out var prices))
        {
            return CostOf(usage);
        }

        var rates = IsPeak(at) ? prices.Peak : prices.OffPeak;
        return ((usage.CacheHitTokens * rates.CacheHit)
                + (usage.CacheMissTokens * rates.CacheMiss)
                + (usage.CompletionTokens * rates.Output)) / 1_000_000.0;
    }
}

/// <summary>The three rates a provider publishes, per million tokens.</summary>
public sealed record TokenPrices(double CacheHit, double CacheMiss, double Output);

/// <summary>
/// One model's published rates. Peak is only set where the provider prices by time of day; when
/// it is absent the off-peak rate is used for both, so an incomplete table undercharges the
/// evening rather than pricing it at zero.
/// </summary>
public sealed record ModelPrices(TokenPrices OffPeak, TokenPrices Peak);

/// <summary>A window of the UTC day in which peak rates apply: `[start, end)` hours.</summary>
public sealed record PeakWindow(int StartHourUtc, int EndHourUtc)
{
    public bool Contains(DateTimeOffset at) =>
        at.UtcDateTime.Hour >= StartHourUtc && at.UtcDateTime.Hour < EndHourUtc;
}

/// <summary>
/// What differs between OpenAI-compatible backends (10_delivery.md §8.1 (`D18`–`D19`)).
/// <para>
/// "OpenAI-compatible" is a dialect rather than a standard. The difference that matters here
/// is in the *usage* object: whether a cache split is reported at all, and under what name.
/// DeepSeek reports a hit/miss pair; OpenAI nests a cached count inside a details object; many
/// local servers report nothing. An unrecognised provider still runs — it simply has no cache
/// figure to report, and the config's prompt rate stands.
/// </para>
/// </summary>
public sealed record ProviderProfile
{
    public string Name { get; init; } = "unknown";

    /// <summary>
    /// Paths to the cache-hit count inside `usage`, tried in order, with `.` separating nested
    /// objects. Empty means the provider does not report one.
    /// </summary>
    public IReadOnlyList<string> CacheHitFields { get; init; } = [];

    public bool ReportsCacheSplit => CacheHitFields.Count > 0;

    /// <summary>
    /// Whether private reasoning is asked for in this backend's **own shape** — a `thinking`
    /// object — rather than through the `reasoning_effort` parameter the dialect shares
    /// (10_delivery.md §8.1 (`D18`–`D19`)). A profile that has no such field is sent neither the object nor a guess at
    /// one: *"OpenAI-compatible"* is a dialect, and a field one member accepts is the commonest
    /// way a request is refused somewhere else.
    /// </summary>
    public bool TakesThinkingObject { get; init; }

    /// <summary>The known backends, by the name `agent-config.json` gives them.</summary>
    public static ProviderProfile For(string? provider) =>
        provider?.Trim().ToLowerInvariant() switch
        {
            "deepseek" => new ProviderProfile
            {
                Name = "deepseek",
                CacheHitFields = ["prompt_cache_hit_tokens"],
                TakesThinkingObject = true,
            },
            "openai" => new ProviderProfile
            {
                Name = "openai",
                CacheHitFields = ["prompt_tokens_details.cached_tokens", "cached_tokens"],
            },
            _ => new ProviderProfile(),
        };
}

/// <summary>
/// Per-call overrides for one model call.
/// <para>
/// These exist for the passes that are not the companion talking to anyone — taking stock,
/// compacting memory — where the settings that suit a reply in front of the player are the wrong
/// ones (10_delivery.md §8.3 (`D21`) and 05_memory.md §2 (`M11`)). Null everywhere means "whatever the settings say".
/// </para>
/// </summary>
public sealed record CallOptions
{
    /// <summary>Override thinking mode for this call only.</summary>
    public bool? Thinking { get; init; }

    /// <summary>Override the reasoning effort for this call only.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Override the output ceiling for this call only.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// The tools this call may use (04_companion.md §2 (`C6`–`C13`)). Null or empty means none are offered, which is
    /// what a maintenance pass wants: taking stock is not a turn in front of the player, and it
    /// has no business moving anything.
    /// </summary>
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    /// <summary>
    /// This reply MUST carry a call, and the provider is told so rather than asked.
    /// <para>
    /// For a pass with no prose-only outcome, where "the words say what I am doing and the call is
    /// missing" is not a reply at all: the errand's step (04_companion.md §6 (`C42`–`C47`) — *"words on their own are
    /// not a step"*). Measured 2026-10-01 across six sittings: 150 steps, 26 of them prose with
    /// `finish_reason: stop` (17.3%), every one repaired — the harness asked again and that reply
    /// always carried the move — so the cost was one whole prompt per miss, and what the player saw
    /// was a companion that announced a move and then sat there. The brief already says the call has
    /// to be in the reply; the surface is what makes it true (`C23.5`).
    /// </para>
    /// <para>
    /// Deliberately not the default, and deliberately not for every pass: a reply in front of the
    /// player may legitimately be speech alone (`C16`), and a step is the one pass where every
    /// outcome the brief names is a call — the move, `press_key`, or the report that ends the errand.
    /// </para>
    /// </summary>
    public bool RequireATool { get; init; }
}

/// <summary>Base class for LLM failures.</summary>
public class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The request could reasonably succeed if retried: rate limiting or a server-side
/// problem (§9.3).
/// </summary>
public sealed class LlmRetryableException : LlmException
{
    public LlmRetryableException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner) => RetryAfter = retryAfter;

    /// <summary>The server asked us to wait this long, if it said so.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Retrying cannot help: a rejected key, an empty balance, a malformed request.
/// These are surfaced loudly so the "session expired?" class of failure is
/// readable from the logs rather than looking like the companion sulking.
/// </summary>
public sealed class LlmFatalException : LlmException
{
    public LlmFatalException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Everything the client needs to make a call.</summary>
public sealed record LlmSettings
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>Which model plays. The code's default is a general chat model, and the shipped file picks the fast one.</summary>
    public string Model { get; init; } = "gpt-4o-mini";

    public double Temperature { get; init; } = 0.7;

    public int MaxTokens { get; init; } = 600;

    public double TopP { get; init; } = 0.9;

    public double PresencePenalty { get; init; } = 0.2;

    public double FrequencyPenalty { get; init; } = 0.2;

    public int RequestTimeoutSeconds { get; init; } = 60;

    /// <summary>Stream the reply over SSE rather than waiting for the whole body.</summary>
    public bool Stream { get; init; } = true;

    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The ceiling on retry backoff, in seconds.
    /// <para>
    /// Set from <c>max_backoff_seconds</c> in <c>agent-config.json</c>; the default here is a
    /// fallback for a client built without a configuration, which only the tests do.
    /// </para>
    /// </summary>
    public int MaxBackoffSeconds { get; init; } = 30;

    /// <summary>
    /// Whether the provider should reason privately before answering.
    /// <para>
    /// Null leaves the provider's own default alone — and on the backend these numbers come from,
    /// that default is **on**, at effort `high`, with the reasoning billed as <em>output</em>.
    /// Measured: turning it on took a call from ~40 completion tokens to ~3,000, which is most of
    /// the bill. It is also reasoning the player never sees, because what they read is the
    /// companion's own `[think]` line.
    /// </para>
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>
    /// How hard to think: `low`, `high` or `max`. Null leaves it to the provider. Only meaningful
    /// while <see cref="Thinking"/> is on, and a lower effort is the first lever to reach for when
    /// reasoning dominates the cost.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Which backend this is, so the cache split can be read out of the usage object. See
    /// <see cref="ProviderProfile.For"/> for the known names.
    /// </summary>
    public ProviderProfile Provider { get; init; } = new();

    public Pricing Pricing { get; init; } = Pricing.Free;
}
