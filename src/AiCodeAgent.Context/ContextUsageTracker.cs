using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Context;

/// <summary>
/// Tracks live context-window usage for a session and decides when
/// compaction should be triggered.
/// </summary>
public class ContextUsageTracker : IContextUsageTracker
{
    private readonly IContextManager _contextManager;
    private readonly ConcurrentDictionary<string, int> _cachedTokens = new();
    private readonly ConcurrentDictionary<string, ContextLimits> _limits = new();

    private const int DefaultSoftLimit = 24_000;
    private const int DefaultHardLimit = 32_000;

    public ContextUsageTracker(IContextManager contextManager)
    {
        _contextManager = contextManager;
    }

    public int CurrentTokens(string sessionId) =>
        _cachedTokens.GetValueOrDefault(sessionId, 0);

    public int SoftLimit(string sessionId) =>
        _limits.TryGetValue(sessionId, out var lim) && lim.SoftLimit != 0
            ? lim.SoftLimit
            : DefaultSoftLimit;

    public int HardLimit(string sessionId) =>
        _limits.TryGetValue(sessionId, out var lim) && lim.HardLimit != 0
            ? lim.HardLimit
            : DefaultHardLimit;

    public async Task RefreshAsync(string sessionId)
    {
        var tokens = await _contextManager.GetTokenCountAsync(sessionId).ConfigureAwait(false);
        _cachedTokens[sessionId] = tokens;
    }

    public bool ShouldCompact(string sessionId) =>
        CurrentTokens(sessionId) >= SoftLimit(sessionId);

    public double UsageRatio(string sessionId)
    {
        var hard = HardLimit(sessionId);
        if (hard <= 0) return 0;
        return (double)CurrentTokens(sessionId) / hard;
    }

    /// <summary>Configure custom limits for a session.</summary>
    public void SetLimits(string sessionId, int softLimit, int hardLimit)
    {
        _limits[sessionId] = new ContextLimits(softLimit, hardLimit);
    }

    private record ContextLimits(int SoftLimit, int HardLimit);
}