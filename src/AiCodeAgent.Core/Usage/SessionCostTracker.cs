using System.Collections.Concurrent;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Usage;

/// <summary>Cumulative usage and cost for one session or one agent.</summary>
public sealed record CostSnapshot(
    int PromptTokens, int CompletionTokens, int CacheReadTokens, int CacheCreationTokens,
    decimal CostUsd, bool HasUnpricedUsage)
{
    /// <summary>Share of input tokens served from the prompt cache (0..1).</summary>
    public double CacheHitRatio
    {
        get
        {
            var total = PromptTokens + CacheReadTokens + CacheCreationTokens;
            return total == 0 ? 0 : (double)CacheReadTokens / total;
        }
    }
}

/// <summary>
/// Thread-safe running total of tokens and dollars keyed by session id (or agent id).
/// Register as a singleton; the orchestrator records every model response into it.
/// </summary>
public sealed class SessionCostTracker
{
    private sealed class Entry
    {
        public int Prompt, Completion, CacheRead, CacheWrite;
        public decimal Cost;
        public bool Unpriced;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Adds one response's usage and returns the new cumulative snapshot.</summary>
    public CostSnapshot Record(string key, string? model, TokenUsage usage)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            e.Prompt += usage.PromptTokens;
            e.Completion += usage.CompletionTokens;
            e.CacheRead += usage.CacheReadTokens;
            e.CacheWrite += usage.CacheCreationTokens;
            if (ModelPricing.Lookup(model) is null) e.Unpriced = true;
            e.Cost += ModelPricing.Cost(model, usage);
            return ToSnapshot(e);
        }
    }

    public CostSnapshot Get(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return new CostSnapshot(0, 0, 0, 0, 0m, false);
        lock (e) return ToSnapshot(e);
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    /// <summary>True when the key has a cap and its spend has reached it.</summary>
    public bool IsOverBudget(string key, decimal? maxUsd) =>
        maxUsd is > 0 && Get(key).CostUsd >= maxUsd.Value;

    private static CostSnapshot ToSnapshot(Entry e) =>
        new(e.Prompt, e.Completion, e.CacheRead, e.CacheWrite, e.Cost, e.Unpriced);
}
