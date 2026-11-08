using System.Net;
using System.Text;
using System.Text.Json;
using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Llm;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The DeepSeek client (§9.3): streaming, retries, and error messages that a human can
/// act on. Everything here runs against a stub handler, so the suite stays offline.
/// </summary>
public class LlmClientTests
{
    private const string StreamBody =
        "data: {\"model\":\"deepseek-chat\",\"choices\":[{\"delta\":{\"content\":\"hello \"}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{\"content\":\"world\"}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n"
        + "data: [DONE]\n\n";

    private const string OnceBody =
        """
        {"model":"deepseek-chat","choices":[{"message":{"role":"assistant","content":"[say] hi\n[act] look"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":8}}
        """;

    [Fact]
    public async Task A_streamed_reply_is_assembled_with_its_usage()
    {
        var handler = new StubHandler(() => Sse(StreamBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings { Stream = true },
            "test-key",
            http: http);

        var result = await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Equal("hello world", result.Content);
        Assert.Equal(10, result.Usage.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task A_non_streamed_reply_is_parsed()
    {
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "test-key", http: http);

        var result = await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Contains("[act] look", result.Content);
        Assert.Equal(20, result.Usage.PromptTokens);
        Assert.Equal(8, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task A_call_option_overrides_thinking_for_that_call_only()
    {
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings
            {
                Stream = false,
                Model = "deepseek-flash",
                Thinking = true,
                Provider = ProviderProfile.For("deepseek"),
            },
            "test-key",
            http: http);

        await client.CompleteAsync(
            [ChatMessage.User("hi")],
            CancellationToken.None,
            new CallOptions { Thinking = false });

        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.Bodies[0]);
    }

    [Fact]
    public async Task With_no_override_the_settings_decide_thinking()
    {
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings
            {
                Stream = false,
                Model = "deepseek-flash",
                Thinking = true,
                Provider = ProviderProfile.For("deepseek"),
            },
            "test-key",
            http: http);

        await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", handler.Bodies[0]);
    }

    [Fact]
    public async Task With_no_opinion_the_thinking_key_is_left_out_entirely()
    {
        // The provider's default stands rather than us asserting one — on the backend this was
        // measured on that default is on — so sending "enabled" would be claiming to know a default
        // we do not control.
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings
            {
                Stream = false,
                Model = "deepseek-flash",
                Provider = ProviderProfile.For("deepseek"),
            },
            "test-key",
            http: http);

        await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.DoesNotContain("\"thinking\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_backend_with_another_dialect_is_sent_no_thinking_object()
    {
        // `thinking: {"type": …}` is one dialect's field, and a field one member of a dialect
        // accepts is the commonest way a request is refused somewhere else (D18). The same config
        // against another backend therefore sends the effort and nothing else.
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings
            {
                Stream = false,
                Model = "gpt-4o-mini",
                Thinking = true,
                ReasoningEffort = "low",
                Provider = ProviderProfile.For("openai"),
            },
            "test-key",
            http: http);

        await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.DoesNotContain("\"thinking\"", handler.Bodies[0]);
        Assert.Contains("\"reasoning_effort\":\"low\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_thinking_knob_that_cannot_travel_is_named_once()
    {
        // A knob that does nothing and says nothing is the defect the spec names (D11.1). Said
        // once, because the check is per call and one evening makes hundreds of them.
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        var said = new List<string>();
        using var client = new OpenAiCompatibleClient(
            new LlmSettings { Stream = false, Thinking = true },
            "test-key",
            message => said.Add(message),
            http);

        await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);
        await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.DoesNotContain("\"thinking\"", handler.Bodies[0]);
        Assert.Single(said);
        Assert.Contains("no known shape for `thinking`", said[0]);
    }

    [Fact]
    public async Task A_call_option_overrides_the_output_ceiling()
    {
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings { Stream = false, MaxTokens = 4000 },
            "test-key",
            http: http);

        await client.CompleteAsync(
            [ChatMessage.User("hi")],
            CancellationToken.None,
            new CallOptions { MaxTokens = 700 });

        Assert.Contains("\"max_tokens\":700", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_truncated_reply_is_reported()
    {
        const string body =
            """
            {"choices":[{"message":{"content":"[say] I was in the middle of"},"finish_reason":"length"}]}
            """;
        var handler = new StubHandler(() => Json(body));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "test-key", http: http);

        var result = await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.True(result.WasTruncated);
        Assert.Equal(FinishReason.Length, result.FinishReason);
    }

    [Fact]
    public async Task A_pass_that_has_no_prose_only_outcome_asks_for_a_call_rather_than_hoping()
    {
        // Measured 2026-10-01: 26 of 150 steps of a real errand came back as words with
        // `finish_reason: stop`, every one of them repaired at the price of a whole prompt, and the
        // player watching a companion announce a move and sit there. The brief has said "words on
        // their own are not a step" since 2026-09-21 and the rate stayed at a sixth, so the fix is
        // the request rather than another sentence (`C23.8`).
        var tools = CompanionTools.InFrontOf(onAnErrand: true);

        var required = new StubHandler(() => Json(OnceBody));
        using (var http = new HttpClient(required))
        using (var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "key", http: http))
        {
            await client.CompleteAsync(
                [ChatMessage.User("take the next step")],
                CancellationToken.None,
                new CallOptions { Tools = tools, RequireATool = true });
        }

        var asked = JsonDocument.Parse(required.Bodies[0]).RootElement;
        Assert.Equal("required", asked.GetProperty("tool_choice").GetString());

        // And the ordinary case is unchanged: a reply in front of the player may be speech alone.
        var offered = new StubHandler(() => Json(OnceBody));
        using (var http = new HttpClient(offered))
        using (var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "key", http: http))
        {
            await client.CompleteAsync(
                [ChatMessage.User("hello")],
                CancellationToken.None,
                new CallOptions { Tools = tools });
        }

        var free = JsonDocument.Parse(offered.Bodies[0]).RootElement;
        Assert.Equal("auto", free.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task The_request_carries_the_v1_generation_parameters()
    {
        var handler = new StubHandler(() => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmSettings
            {
                Stream = true,
                Model = "deepseek-chat",
                Temperature = 0.7,
                MaxTokens = 600,
                TopP = 0.9,
                PresencePenalty = 0.2,
                FrequencyPenalty = 0.2,
            },
            "test-key",
            http: http);

        await client.CompleteAsync([ChatMessage.System("persona"), ChatMessage.User("hi")], CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(600, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.9, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(0.2, root.GetProperty("presence_penalty").GetDouble(), 3);
        Assert.Equal(0.2, root.GetProperty("frequency_penalty").GetDouble(), 3);
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("persona", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_rejected_key_is_fatal_and_says_what_to_do()
    {
        var handler = new StubHandler(() => Json("""{"error":{"message":"Authentication Fails"}}""", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "bad-key", http: http);

        var error = await Assert.ThrowsAsync<LlmFatalException>(
            () => client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None));

        Assert.Contains("INFOCOMPANION_LLM_API_KEY", error.Message);
        Assert.Contains("Authentication Fails", error.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task An_empty_balance_is_fatal_and_readable()
    {
        var handler = new StubHandler(() => Json("""{"error":{"message":"Insufficient Balance"}}""", HttpStatusCode.PaymentRequired));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false }, "key", http: http);

        var error = await Assert.ThrowsAsync<LlmFatalException>(
            () => client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None));

        Assert.Contains("balance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rate_limiting_is_retried_and_then_succeeds()
    {
        var handler = new StubHandler(
            () => Json("""{"error":{"message":"rate limited"}}""", HttpStatusCode.TooManyRequests),
            () => Json(OnceBody));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false, MaxRetries = 3 }, "key", http: http);

        var result = await client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Contains("[act] look", result.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task A_server_error_is_retried_only_up_to_the_limit()
    {
        var handler = new StubHandler(() => Json("upstream is unwell", HttpStatusCode.BadGateway));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(new LlmSettings { Stream = false, MaxRetries = 1 }, "key", http: http);

        await Assert.ThrowsAsync<LlmRetryableException>(
            () => client.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None));

        Assert.Equal(2, handler.Bodies.Count); // the first try plus one retry
    }

    [Fact]
    public void A_missing_key_is_refused_clearly()
    {
        var error = Assert.Throws<LlmFatalException>(() => new OpenAiCompatibleClient(new LlmSettings(), "  "));
        Assert.Contains("INFOCOMPANION_LLM_API_KEY", error.Message);
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions", "https://api.deepseek.com/chat/completions")]
    public void The_endpoint_is_built_from_the_configured_base_url(string configured, string expected)
    {
        using var client = new OpenAiCompatibleClient(new LlmSettings { BaseUrl = configured }, "key");
        Assert.Equal(expected, client.Endpoint);
    }

    [Fact]
    public void Cost_follows_the_configured_pricing()
    {
        var pricing = new Pricing(1.0, 2.0);
        Assert.Equal(2.0, pricing.CostOf(new Usage(1000, 500)), 6);
        Assert.Equal(0.0, Pricing.Free.CostOf(new Usage(9999, 9999)), 6);
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public StubHandler(params Func<HttpResponseMessage>[] responses) => _responses = new Queue<Func<HttpResponseMessage>>(responses);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var factory = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return factory();
        }
    }
}

/// <summary>Loading the runtime configuration (§9.3).</summary>
public class AgentConfigTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "infocompanion-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The repository root, found by walking up from the test assembly to the one directory with
    /// `Assets/agent-config.json` in it.
    /// <para>
    /// Two things the gate must check live *outside* the project — the shipped configuration and the
    /// loop's own source — so the suite needs the tree rather than the project. It is found rather than
    /// passed so that both ways of running it work: `dotnet test` from a checkout, and the gate's
    /// container, which mounts the repository root for exactly this.
    /// </para>
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Assets", "agent-config.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"no repository above {AppContext.BaseDirectory}: the checks that read the shipped "
            + "configuration and the loop's source need the tree that holds `Assets/`, so run the suite "
            + "from a checkout or mount the repository root (Build/check.sh does)");
    }

    [Fact]
    public void Thinking_is_not_disabled_for_maintenance_unless_the_server_config_asks()
    {
        // Off by default. The switch is a cost decision, not a correctness one, so an
        // unconfigured deployment behaves exactly as it did before the switch existed.
        var config = new AgentConfig { Thinking = new ThinkingConfig { Type = "enabled" } };

        Assert.False(config.Thinking!.OffForMaintenance);
    }

    [Fact]
    public void The_switch_is_read_from_json_under_its_documented_name()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
            """{"thinking":{"type":"enabled","off_for_maintenance":true}}""")!;

        Assert.True(config.Thinking!.Enabled);
        Assert.True(config.Thinking.OffForMaintenance);
    }

    [Fact]
    public void No_pass_thinks_with_its_reasoning_off_unless_it_is_named()
    {
        // The experiment instrument, and the reason it ships empty: the 2026-09-25 evening is the
        // measurement, and until a purpose is named here a deployment behaves exactly as it did
        // before the key existed.
        Assert.Empty(new AgentConfig { Thinking = new ThinkingConfig() }.Thinking!.OffFor);
        Assert.Empty(new ThinkingConfig().OffFor);
    }

    [Fact]
    public void The_named_passes_are_read_from_json()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
            """{"thinking":{"off_for":["mission-step"]}}""")!;

        Assert.Equal(["mission-step"], config.Thinking!.OffFor);
    }

    [Fact]
    public void The_completion_ceiling_follows_the_pass_role_and_the_window()
    {
        // This replaced a single global number plus three hand-written exceptions (04_companion.md §3.2
        // (`C23.6`)). A reply is bounded by what a turn sounds like and a write by the artifact it is
        // composing, so the two are bounded differently; and both are stated against the window, so the
        // one number a deployment changes when it changes model moves every ceiling with it.
        var config = new AgentConfig();

        Assert.Equal(16384, config.MaxTokensFor("player-line"));
        Assert.Equal(16384, config.MaxTokensFor("mission-step"));
        Assert.Equal(32768, config.MaxTokensFor("reconcile"));
        Assert.Equal(32768, config.MaxTokensFor("report"));

        // Twice the window, twice every ceiling. Nothing here has to be raised by hand when the context
        // grows, which is the whole of what the arrangement is for.
        var roomier = new AgentConfig { ContextWindowTokens = 131_072 };

        Assert.Equal(32768, roomier.MaxTokensFor("player-line"));
        Assert.Equal(65536, roomier.MaxTokensFor("reconcile"));
    }

    [Fact]
    public void A_pass_with_no_role_keeps_the_global_ceiling()
    {
        // The global number is a fallback now rather than the policy: it is what a pass the role table
        // does not know gets, and what every pass gets from a deployment that named no shares.
        var config = new AgentConfig { MaxTokens = 8000 };

        Assert.Equal(8000, config.MaxTokensFor("a-pass-nobody-classified"));
        Assert.Equal(16384, config.MaxTokensFor("mission-step"));

        var shardless = new AgentConfig { MaxTokens = 8000, MaxTokensShareByRole = new() };

        Assert.Equal(8000, shardless.MaxTokensFor("mission-step"));
        Assert.Equal(8000, shardless.MaxTokensFor("reconcile"));
    }

    [Fact]
    public void An_entry_in_the_table_overrides_the_role_and_may_not_be_thrift()
    {
        // Measured live on 2026-09-16: three replies hit the global ceiling and were cut off — one lost
        // a mission-step's act, one cut a notebook rewrite mid-sentence. The mechanism stays for that
        // case, which is a pass whose reply is shaped differently from its role's.
        var config = new AgentConfig
        {
            MaxTokensByPurpose = new Dictionary<string, int> { ["reconcile"] = 40000 },
        };

        Assert.Equal(40000, config.MaxTokensFor("reconcile"));

        // And the floor (`C23.7`): half of what the role is given, so an entry can name a shape and
        // cannot be a way of spending less. `reconcile` writes, so half of its 32,768.
        Assert.Equal(16384, config.FloorFor("reconcile"));
        Assert.Equal(8192, config.FloorFor("player-line"));

        // Measured against the role rather than against the entry, which is what makes an under-sized
        // entry visible: 4,000 for a writing pass is under a floor of 16,384, and the gate is what says
        // so when it is shipped.
        var thrifty = new AgentConfig
        {
            MaxTokensByPurpose = new Dictionary<string, int> { ["introspect"] = 4000 },
        };

        Assert.True(thrifty.MaxTokensFor("introspect") < thrifty.FloorFor("introspect"));
    }

    [Fact]
    public void The_retry_after_a_truncation_asks_for_twice_the_room()
    {
        // 04_companion.md §3.2 (`C23.4`): the ceiling that cut the first attempt is the one fact about
        // the reply the harness holds and the model cannot see, so it is evidence rather than a guess.
        var config = new AgentConfig();

        Assert.Equal(32768, config.MaxTokensFor("player-line", escalated: true));
        Assert.Equal(65536, config.MaxTokensFor("reconcile", escalated: true));

        // The window clamps the ask whatever it is: six figures of ceiling at a 20,000 window is the
        // arithmetic saying there is no such thing, and it settles to what the prompt leaves. A ceiling
        // that fits comes back untouched, because clamping is not thrift.
        Assert.Equal(32000, config.CeilingThatFits(32000, promptTokens: 10_000));
        Assert.Equal(65536 - 40000 - 2048, config.CeilingThatFits(32000, promptTokens: 40_000));
    }

    [Fact]
    public void The_shipped_ceilings_are_not_under_the_floor_for_their_role()
    {
        // `C23.7`, and the failure it exists for: `introspect` sat at 4,000 in the document while the
        // shipped file carried 12,000, against a prompt of 28,230 — and the 2026-09-29 bundle holds the
        // drop that says a reply was cut off at 4,000 before it had said anything. A number left behind
        // while the context grew is silent to everyone but the log, so the gate reads the shipped file.
        var (config, source, problem) = AgentConfig.Load(Path.Combine(RepositoryRoot(), "Assets"));

        Assert.Null(problem);
        Assert.NotNull(source);

        foreach (var (purpose, ceiling) in config.MaxTokensByPurpose)
        {
            // A misspelled purpose is the other half of the drift this is for: an entry keyed to a pass
            // nothing runs is a number nobody is held to, and it reads exactly like one that is.
            Assert.True(
                Passes.Knows(purpose),
                $"`max_tokens_by_purpose` names `{purpose}`, which is not a pass the loop runs");

            var floor = config.FloorFor(purpose);

            Assert.True(
                ceiling >= floor,
                $"`max_tokens_by_purpose` gives `{purpose}` {ceiling}, under the {floor} its role allows");
        }

        // The number a pass with no role falls back to is held to the same rule, because it is what an
        // unclassified pass gets — a pass added without a role is not a reason to clip it.
        Assert.True(
            config.MaxTokens >= config.FloorFor("player-line"),
            $"`max_tokens` is {config.MaxTokens}, under the {config.FloorFor("player-line")} a reply allows");

        // The shipped shares themselves, stated here because they *are* the policy: a quarter of a
        // 65,536 window for a pass that answers somebody, a half for a pass that composes something.
        Assert.Equal(16384, config.MaxTokensFor("player-line"));
        Assert.Equal(32768, config.MaxTokensFor("report"));
    }

    [Fact]
    public void Every_pass_the_loop_names_at_a_call_site_has_a_role()
    {
        // The direction that was missing, and a live sitting found it: five passes — `player-joined`,
        // `idle-nudge`, `refused-line`, `stuck` and `mission-report` — are passed by the loop and were
        // absent from the role table, so they sat on the global fallback rather than their role's share.
        // Nothing said so: the table's own test only asks whether the names *in* it are real, and the
        // trigger test only walks `SpeakReason`, which covers some of the unprompted passes and none of
        // these. Measured 2026-10-01, it was visible in the sitting itself: two calls at 16,000 where a
        // reply pass should have had 16,384, and the errand's own report at half of what a write gets.
        //
        // The call shape is what makes this checkable: a pass's purpose is the argument immediately
        // before its cancellation token, and it is always a literal of its own on its own line.
        var source = File.ReadAllLines(
            Path.Combine(RepositoryRoot(), "Source", "Agent", "src", "Agent.Core", "Loop", "AgentLoop.cs"));

        var named = new List<string>();
        for (var i = 0; i + 1 < source.Length; i++)
        {
            var line = source[i].Trim();
            if (line.Length < 4 || line[0] != '"' || !line.EndsWith("\",", StringComparison.Ordinal))
            {
                continue;
            }

            if (!source[i + 1].Trim().StartsWith("cancellationToken", StringComparison.Ordinal))
            {
                continue;
            }

            var purpose = line[1..^2];
            if (purpose.Length > 0 && purpose.All(character => char.IsLower(character) || character == '-'))
            {
                named.Add(purpose);
            }
        }

        // Guard against the check passing because the shape it reads changed: a purpose is never zero.
        Assert.NotEmpty(named);

        foreach (var purpose in named.Distinct())
        {
            Assert.True(
                Passes.Knows(purpose),
                $"`{purpose}` is a pass `AgentLoop` runs and has no role, so it gets the global ceiling");
        }
    }

    [Fact]
    public void Every_purpose_a_trigger_runs_has_a_role_of_its_own()
    {
        // The gap this closes was found by reading, not by a failure: the six unprompted passes are
        // named after their trigger (`SpeakReason`, lowercased by the loop at the call site), so
        // nothing in the table's own words — or in `AgentLoop`'s source, which never spells them —
        // said they existed. They were falling back to the global ceiling and a 2% difference happened
        // to hide it, which is the shape of every one of these: right by accident until it isn't.
        foreach (var reason in Enum.GetValues<SpeakReason>())
        {
            if (reason == SpeakReason.None)
            {
                // The one that never becomes a purpose: the loop returns before it names a pass.
                continue;
            }

            var purpose = reason.ToString().ToLowerInvariant();

            Assert.True(Passes.Knows(purpose), $"`{purpose}` is a pass the triggers run and has no role");
        }
    }

    [Fact]
    public void Every_role_in_the_table_names_a_pass_the_loop_actually_runs()
    {
        // The way this can fail is a typo: a purpose misspelled in the table is a purpose that silently
        // falls back to the tighter role, and nothing about a ceiling shows that. The loop's own source
        // is the list — for the passes it names in words, and for the unprompted ones the triggers name
        // after themselves, which the loop never spells out.
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "Source", "Agent", "src", "Agent.Core", "Loop", "AgentLoop.cs"));
        var fromTriggers = Enum.GetValues<SpeakReason>()
            .Select(reason => reason.ToString().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var purpose in Passes.All)
        {
            Assert.True(
                source.Contains($"\"{purpose}\"", StringComparison.Ordinal) || fromTriggers.Contains(purpose),
                $"`{purpose}` is in the role table and is neither a pass `AgentLoop` runs nor one a trigger names");
        }

        // A purpose nobody classified has no role rather than a guessed one, and gets the global number
        // — which is why the fallback is held to the floor above. The writes are named one by one
        // because they are the ones a wrong guess would be costly for.
        Assert.Null(Passes.RoleFor("a-pass-nobody-classified"));
        Assert.False(Passes.Knows("a-pass-nobody-classified"));
        Assert.Equal(PassRole.Write, Passes.RoleFor("report"));
        Assert.Equal(PassRole.Write, Passes.RoleFor("silent-report"));
        Assert.Equal(PassRole.Reply, Passes.RoleFor("silent-call"));
        Assert.Equal(PassRole.Reply, Passes.RoleFor("cut-off"));
    }

    [Fact]
    public void The_loops_cadences_are_read_under_their_documented_names()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>(
            """
            {"tick_seconds":2,"tail_turns_for_autosave":3,"vocabulary_tail":5,"max_refused_acts":3,
             "away_player_line_limit":7,"max_held_replies":2,"max_command_words":9,
             "short_phrase_words":2,"arbitration_history_length":5,"observation_quote_limit":80,
             "max_backoff_seconds":12,"seat_writer_wait_seconds":4}
            """)!;

        Assert.Equal(2, config.TickSeconds);
        Assert.Equal(3, config.TailTurnsForAutosave);
        Assert.Equal(5, config.VocabularyTail);
        Assert.Equal(3, config.MaxRefusedActs);
        Assert.Equal(7, config.AwayPlayerLineLimit);
        Assert.Equal(2, config.MaxHeldReplies);
        Assert.Equal(9, config.MaxCommandWords);
        Assert.Equal(2, config.ShortPhraseWords);
        Assert.Equal(5, config.ArbitrationHistoryLength);
        Assert.Equal(80, config.ObservationQuoteLimit);
        Assert.Equal(12, config.MaxBackoffSeconds);
        Assert.Equal(4, config.SeatWriterWaitSeconds);

        // The shipped values, which is what a config file that does not mention them gets.
        var defaults = new AgentConfig();
        Assert.Equal(5, defaults.TickSeconds);
        Assert.Equal(1, defaults.MaxRefusedActs);
        Assert.Equal(40, defaults.AwayPlayerLineLimit);
        Assert.Equal(4, defaults.MaxHeldReplies);
        Assert.Equal(6, defaults.MaxCommandWords);
        Assert.Equal(4, defaults.ShortPhraseWords);
        Assert.Equal(12, defaults.ArbitrationHistoryLength);
        Assert.Equal(300, defaults.ObservationQuoteLimit);
        Assert.Equal(30, defaults.MaxBackoffSeconds);
        Assert.Equal(2, defaults.SeatWriterWaitSeconds);
    }

    [Fact]
    public void A_number_two_classes_share_has_one_home()
    {
        // The mismatch this pins down: the notebook's constructor default used to be 2500 while
        // `notebook_token_budget` was 6000, so a store built without a budget disagreed with the
        // configured one and nothing in either file said so. Every pair below is one number with
        // one home, and the configuration type is the one that points at it.
        var config = new AgentConfig();

        Assert.Equal(NotebookStore.DefaultTokenBudget, config.NotebookTokenBudget);
        Assert.Equal(PersistentMemory.DefaultTokenBudget, config.MemoryTokenBudget);
        Assert.Equal(ObservationLog.DefaultLimit, config.ReconcileRecentTurns);
        Assert.Equal(ObservationLog.SearchLimit, config.RecallMaxHits);
        Assert.Equal(ObservationLog.SearchQuoteLimit, config.RecallQuoteChars);
        Assert.Equal(ObservationLog.QuoteLimit, config.ObservationQuoteLimit);
        Assert.Equal(GameBrief.DefaultMaxCharacters, config.GameBriefMaxChars);

        // And the loop's own, whose home is the defaults type beside the configuration.
        Assert.Equal(ConfigDefaults.MaxRefusedActs, config.MaxRefusedActs);
        Assert.Equal(ConfigDefaults.MaxHeldReplies, config.MaxHeldReplies);
        Assert.Equal(ConfigDefaults.MaxCommandWords, config.MaxCommandWords);
        Assert.Equal(ConfigDefaults.ShortPhraseWords, config.ShortPhraseWords);
        Assert.Equal(ConfigDefaults.ArbitrationHistoryLength, config.ArbitrationHistoryLength);
        Assert.Equal(ConfigDefaults.MaxBackoffSeconds, config.MaxBackoffSeconds);
        Assert.Equal(ConfigDefaults.SeatWriterWaitSeconds, config.SeatWriterWaitSeconds);
    }

    [Fact]
    public void The_allowances_and_the_clocks_are_read_under_their_documented_names()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
            """{"maintenance_call_limit":9,"working_indicator_seconds":11,"reconcile_min_quiet_seconds":90}""")!;

        Assert.Equal(9, config.MaintenanceCallLimit);
        Assert.Equal(11, config.WorkingIndicatorSeconds);
        Assert.Equal(90, config.ReconcileMinQuietSeconds);

        // Defaults: six maintenance calls into a silence, eight seconds before the panel says
        // anything, and taking stock waiting a minute for a quiet room (04_companion.md §5 (`C29`–`C41`)).
        var defaults = new AgentConfig();
        Assert.Equal(6, defaults.MaintenanceCallLimit);
        Assert.Equal(8, defaults.WorkingIndicatorSeconds);
        Assert.Equal(60, defaults.ReconcileMinQuietSeconds);
    }

    [Fact]
    public void The_defaults_are_the_documented_v1_settings()
    {
        var directory = TempDirectory();
        var (config, source, problem) = AgentConfig.Load(directory);

        Assert.Null(source);
        Assert.Null(problem);
        // The code's own defaults name nobody: the endpoint and the model are the deployment's
        // choice in `agent-config.json`, and the shipped file is what it gets (D11).
        Assert.Equal("https://api.openai.com/v1", config.BaseUrl);
        Assert.Equal("gpt-4o-mini", config.Model);
        Assert.Equal(0.7, config.Temperature, 3);
        Assert.Equal(600, config.MaxTokens);
        Assert.Equal(0.9, config.TopP, 3);
        Assert.Equal(0.2, config.PresencePenalty, 3);
        Assert.Equal(0.2, config.FrequencyPenalty, 3);
        Assert.Equal(60, config.RequestTimeoutSeconds);
        Assert.Equal(6000, config.NotebookTokenBudget);
        Assert.Equal(50, config.IdleNudgeSeconds);
    }

    [Fact]
    public void A_config_in_the_state_directory_is_used()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "agent-config.json"),
            """{"base_url":"https://example.test/v1","model":"deepseek-reasoner","max_tokens":1234}""");

        var (config, source, problem) = AgentConfig.Load(directory);

        Assert.Null(problem);
        Assert.NotNull(source);
        Assert.Equal("https://example.test/v1", config.BaseUrl);
        Assert.Equal("deepseek-reasoner", config.Model);
        Assert.Equal(1234, config.MaxTokens);
        // Unspecified fields keep their documented defaults.
        Assert.Equal(0.7, config.Temperature, 3);
    }

    [Fact]
    public void The_specifications_sample_config_still_loads()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "agent-config.json"),
            """
            {
              "base_url": "https://api.deepseek.com",
              "model": "deepseek-chat",
              "temperature": 0.7,
              "max_tokens": 600,
              "top_p": 0.9,
              "presence_penalty": 0.2,
              "frequency_penalty": 0.2,
              "request_timeout_seconds": 60,
              "pricing_per_1k_tokens": { "prompt": 0.14, "completion": 0.28 }
            }
            """);

        var (config, _, problem) = AgentConfig.Load(directory);

        Assert.Null(problem);
        Assert.Equal(0.14, config.PricingPer1kTokens.Prompt, 6);
        Assert.Equal(0.28, config.PricingPer1kTokens.Completion, 6);
        Assert.Equal(600, config.ToLlmSettings().MaxTokens);
    }

    [Fact]
    public void A_broken_config_falls_back_to_defaults_and_says_so()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "agent-config.json"), "{ this is not json");

        var (config, source, problem) = AgentConfig.Load(directory);

        Assert.Null(source);
        Assert.NotNull(problem);
        Assert.Equal("gpt-4o-mini", config.Model);
    }

    /// <summary>
    /// Pointing the companion at another provider is an environment variable, not an edit to a
    /// mounted file — the endpoint and the model both, and the file is left as it was found.
    /// </summary>
    [Fact]
    public void The_environment_names_the_endpoint_and_the_model_when_it_speaks()
    {
        var fromFile = new AgentConfig
        {
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-flash",
        };

        var pointed = fromFile.WithEndpoint("http://localhost:11434/v1", "qwen3:8b");

        Assert.Equal("http://localhost:11434/v1", pointed.BaseUrl);
        Assert.Equal("qwen3:8b", pointed.Model);
        Assert.Equal("https://api.deepseek.com", fromFile.BaseUrl);
        Assert.Equal("deepseek-flash", fromFile.Model);
    }

    /// <summary>
    /// Compose passes an unset variable through as an empty string, so blank has to mean "no
    /// opinion" rather than "call nothing".
    /// </summary>
    [Fact]
    public void A_blank_variable_leaves_the_file_alone()
    {
        var fromFile = new AgentConfig
        {
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-flash",
        };

        var unchanged = fromFile.WithEndpoint(null, "  ");

        Assert.Equal("https://api.deepseek.com", unchanged.BaseUrl);
        Assert.Equal("deepseek-flash", unchanged.Model);
    }
}
