using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Usage;

namespace AiCodeAgent.Core.Tests.Usage;

public class CostTrackingTests
{
    [Fact]
    public void Lookup_MatchesLongestKey()
    {
        ModelPricing.SetOverridesForTests(new());
        Assert.Equal(0.15m, ModelPricing.Lookup("gpt-4o-mini-2024")!.InputPerMillion);
        Assert.Equal(2.5m, ModelPricing.Lookup("gpt-4o")!.InputPerMillion);
    }

    [Fact]
    public void Lookup_UnknownModel_IsNull()
    {
        ModelPricing.SetOverridesForTests(new());
        Assert.Null(ModelPricing.Lookup("llama3:8b"));
        Assert.Null(ModelPricing.Lookup(null));
    }

    [Fact]
    public void Overrides_TakePrecedenceOverDefaults()
    {
        ModelPricing.SetOverridesForTests(new() { ["claude-sonnet"] = new ModelPrice(1m, 2m) });
        Assert.Equal(1m, ModelPricing.Lookup("claude-sonnet-4")!.InputPerMillion);
        ModelPricing.SetOverridesForTests(new());
    }

    [Fact]
    public void Cost_CountsCacheReadsAtDiscountAndWritesAtPremium()
    {
        ModelPricing.SetOverridesForTests(new() { ["m"] = new ModelPrice(10m, 20m) });
        var usage = new TokenUsage
        {
            PromptTokens = 1_000_000,
            CompletionTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheCreationTokens = 1_000_000
        };
        // 10 + 20 + 1 (10%) + 12.5 (125%)
        Assert.Equal(43.5m, ModelPricing.Cost("m", usage));
        ModelPricing.SetOverridesForTests(new());
    }

    [Fact]
    public void Tracker_AccumulatesAndReportsBudget()
    {
        ModelPricing.SetOverridesForTests(new() { ["m"] = new ModelPrice(1m, 1m) });
        var tracker = new SessionCostTracker();
        tracker.Record("s", "m", new TokenUsage { PromptTokens = 500_000 });
        Assert.False(tracker.IsOverBudget("s", 1m));
        tracker.Record("s", "m", new TokenUsage { CompletionTokens = 500_000 });
        Assert.True(tracker.IsOverBudget("s", 1m));
        Assert.False(tracker.IsOverBudget("s", null));
        Assert.False(tracker.IsOverBudget("other", 1m));
        ModelPricing.SetOverridesForTests(new());
    }

    [Fact]
    public void Tracker_FlagsUnpricedModels()
    {
        ModelPricing.SetOverridesForTests(new());
        var tracker = new SessionCostTracker();
        var snap = tracker.Record("s", "ollama/llama3", new TokenUsage { PromptTokens = 10 });
        Assert.True(snap.HasUnpricedUsage);
        Assert.Equal(0m, snap.CostUsd);
    }

    [Fact]
    public void CacheHitRatio_IsShareOfInputServedFromCache()
    {
        var snap = new CostSnapshot(100, 0, 300, 100, 0m, false);
        Assert.Equal(0.6, snap.CacheHitRatio, 3);
    }

    [Fact]
    public void TokenUsageEvent_Format_IncludesCostAndCache()
    {
        var evt = new TokenUsageEvent(new TokenUsage { PromptTokens = 100, CompletionTokens = 50, CacheReadTokens = 100 }, 0.42m);
        var text = evt.Format();
        Assert.Contains("$0.42", text);
        Assert.Contains("cache 50%", text);
    }
}
