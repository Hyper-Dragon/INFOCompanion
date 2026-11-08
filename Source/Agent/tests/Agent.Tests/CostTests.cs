using Agent.Core.Config;
using Agent.Llm;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// Cost accounting.
///
/// Small tests for a small thing, and they matter because a cost figure that is quietly
/// optimistic is worse than none at all: it makes every later decision about cadence, context
/// and summarisation wrong. `pricing_per_1k_tokens` shipped at zero for exactly that reason
/// (10_delivery.md §8.4 (`D22`–`D24`)).
/// </summary>
public sealed class CostTests
{
    private static Usage Call(int prompt, int completion, int cacheHit) =>
        new(prompt, completion) { CacheHitTokens = cacheHit };

    [Fact]
    public void The_miss_count_is_whatever_the_prompt_did_not_hit()
    {
        var usage = Call(prompt: 1000, completion: 10, cacheHit: 900);

        Assert.Equal(100, usage.CacheMissTokens);
        Assert.True(usage.HadCacheHit);
    }

    [Fact]
    public void A_provider_that_reports_nothing_is_billed_on_the_whole_prompt()
    {
        var usage = Call(prompt: 1000, completion: 10, cacheHit: 0);

        Assert.Equal(1000, usage.CacheMissTokens);
        Assert.False(usage.HadCacheHit);
    }

    [Fact]
    public void A_cache_hit_larger_than_the_prompt_is_clamped_rather_than_credited()
    {
        var usage = new Usage(100, 0) { CacheHitTokens = 400 };

        Assert.Equal(0, usage.CacheMissTokens);
    }

    [Fact]
    public void With_no_cache_rate_configured_the_cost_is_the_flat_calculation()
    {
        // The back-compatibility that matters: an agent-config.json carrying only `prompt` and
        // `completion` must report exactly what it reported before this existed.
        var pricing = new Pricing(PromptPer1K: 0.5, CompletionPer1K: 1.5);
        var usage = Call(prompt: 2000, completion: 100, cacheHit: 1500);

        Assert.Equal((0.5 * 2) + (1.5 * 0.1), pricing.CostOf(usage), precision: 9);
    }

    [Fact]
    public void A_configured_cache_rate_charges_the_hit_portion_less()
    {
        var pricing = new Pricing(PromptPer1K: 0.5, CompletionPer1K: 0, CacheHitPer1K: 0.05);
        var usage = Call(prompt: 2000, completion: 0, cacheHit: 1500);

        // 500 tokens at the full rate, 1500 at a tenth of it.
        Assert.Equal((0.5 * 0.5) + (0.05 * 1.5), pricing.CostOf(usage), precision: 9);
    }

    [Theory]
    [InlineData("deepseek", true)]
    [InlineData("OpenAI", true)]
    [InlineData("ollama", false)]
    [InlineData(null, false)]
    public void Only_a_provider_that_reports_a_split_is_expected_to_have_one(string? name, bool reports)
    {
        Assert.Equal(reports, ProviderProfile.For(name).ReportsCacheSplit);
    }

    [Fact]
    public void An_openai_cache_count_is_looked_for_inside_its_details_object()
    {
        var profile = ProviderProfile.For("openai");

        Assert.Equal(
            ["prompt_tokens_details.cached_tokens", "cached_tokens"],
            profile.CacheHitFields);
    }

    [Fact]
    public void The_config_sentinel_does_not_flatter_a_backend_with_no_pricing()
    {
        // Nothing filled in: the full prompt rate stands, so cost stays zero rather than
        // quietly pretending that cached tokens are free.
        var settings = new AgentConfig().ToLlmSettings();
        var usage = Call(prompt: 1000, completion: 0, cacheHit: 1000);

        Assert.Equal(settings.Pricing.PromptPer1K, settings.Pricing.EffectiveCacheHitPer1K);
        Assert.Equal(0, settings.Pricing.CostOf(usage), precision: 9);
    }

    [Fact]
    public void The_provider_named_in_the_config_selects_the_profile()
    {
        var settings = new AgentConfig { Provider = "openai" }.ToLlmSettings();

        Assert.Equal("openai", settings.Provider.Name);
        Assert.True(settings.Provider.ReportsCacheSplit);
    }

    // -- the published table --------------------------------------------------------------------

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_flash_call_is_priced_from_the_published_table()
    {
        var pricing = new AgentConfig().ToPricing();
        var usage = Call(prompt: 6730, completion: 89, cacheHit: 6016);

        // Off-peak flash: 0.003 / 0.15 / 0.60 per million tokens.
        var expected = ((6016 * 0.003) + (714 * 0.15) + (89 * 0.60)) / 1_000_000.0;

        Assert.Equal(
            expected,
            pricing.CostOfAt(usage, "deepseek-flash", Utc(2026, 9, 14, 14, 0)),
            precision: 12);
    }

    [Fact]
    public void The_same_tokens_cost_double_inside_a_peak_window()
    {
        var pricing = new AgentConfig().ToPricing();
        var usage = Call(prompt: 6730, completion: 89, cacheHit: 6016);

        var offPeak = pricing.CostOfAt(usage, "deepseek-flash", Utc(2026, 9, 14, 14, 0));
        var peak = pricing.CostOfAt(usage, "deepseek-flash", Utc(2026, 9, 14, 7, 0));

        Assert.Equal(offPeak * 2, peak, precision: 12);
    }

    [Fact]
    public void Peak_windows_do_not_apply_at_the_weekend()
    {
        var pricing = new AgentConfig().ToPricing();

        // Walk to a Saturday rather than assuming which weekday a fixed date falls on — the
        // window is nominally the same 06:00-10:00 as on a weekday.
        var saturday = Utc(2026, 9, 14, 7, 0);
        while (saturday.DayOfWeek != DayOfWeek.Saturday)
        {
            saturday = saturday.AddDays(1);
        }

        Assert.False(pricing.IsPeak(saturday));
        Assert.True(pricing.IsPeak(saturday.AddDays(-1)));
    }

    [Fact]
    public void The_shipped_table_covers_the_two_published_models()
    {
        var pricing = new AgentConfig().ToPricing();

        Assert.Contains("deepseek-flash", pricing.Models.Keys);
        Assert.Contains("deepseek-v4-pro", pricing.Models.Keys);
        Assert.Equal(2, pricing.PeakWindowsUtc.Count);
    }

    [Fact]
    public void A_model_the_table_does_not_know_falls_back_to_the_flat_rates()
    {
        var config = new AgentConfig
        {
            PricingPer1kTokens = new PricingConfig { Prompt = 0.5, Completion = 1.5 },
        };

        var usage = Call(prompt: 2000, completion: 100, cacheHit: 1500);

        // Exactly the old flat calculation, so an existing config keeps its old answer.
        Assert.Equal(
            (0.5 * 2) + (1.5 * 0.1),
            config.ToPricing().CostOfAt(usage, "some-other-model", DateTimeOffset.UtcNow),
            precision: 9);
    }

    [Fact]
    public void A_malformed_peak_window_is_dropped_rather_than_guessed_at()
    {
        var config = new AgentConfig
        {
            Pricing = new PricingTableConfig
            {
                PeakWindowsUtc = [[6, 10], [10, 6], [25, 30]],
            },
        };

        var pricing = config.ToPricing();

        // A peak window that is wrong puts a wrong number on every call inside it, so a bad one
        // is dropped rather than clamped.
        Assert.Single(pricing.PeakWindowsUtc);
        Assert.Equal(new PeakWindow(6, 10), pricing.PeakWindowsUtc[0]);
    }

    // -- thinking mode: the largest single lever -------------------------------------------------

    [Theory]
    [InlineData("enabled", true)]
    [InlineData("disabled", false)]
    [InlineData("DISABLED", false)]
    public void Thinking_mode_is_read_from_the_providers_own_shape(string type, bool expected)
    {
        var config = new AgentConfig { Thinking = new ThinkingConfig { Type = type } };

        Assert.Equal(expected, config.ToLlmSettings().Thinking);
    }

    [Fact]
    public void An_unrecognised_thinking_value_leaves_the_provider_default_alone()
    {
        // Better no opinion than a boolean guessed from a typo: the provider's default is on, and
        // silently disabling it would change how the companion reads.
        var config = new AgentConfig { Thinking = new ThinkingConfig { Type = "maybe" } };

        Assert.Null(config.ToLlmSettings().Thinking);
    }

    [Fact]
    public void With_no_thinking_setting_the_provider_default_stands()
    {
        Assert.Null(new AgentConfig().ToLlmSettings().Thinking);
    }

    [Fact]
    public void The_reasoning_effort_passes_through()
    {
        var config = new AgentConfig { ReasoningEffort = "low" };

        Assert.Equal("low", config.ToLlmSettings().ReasoningEffort);
    }
}
