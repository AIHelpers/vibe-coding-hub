using AiCodeAgent.Core.Configuration;
using System.Text.Json;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Usage;

/// <summary>USD price per one million tokens for a model family.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null, decimal? CacheWritePerMillion = null);

/// <summary>
/// Price lookup used by <see cref="SessionCostTracker"/>. Built-in numbers are ESTIMATES that go stale;
/// override or extend them with <c>~/.aiagent/pricing.json</c>:
/// <c>{ "claude-sonnet": { "InputPerMillion": 3, "OutputPerMillion": 15 } }</c>.
/// Keys match by case-insensitive substring of the model id (longest key wins). Unknown models cost 0
/// (local models such as Ollama are free) and are reported as "unpriced".
/// </summary>
public static class ModelPricing
{
    private static readonly Dictionary<string, ModelPrice> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus"] = new(15m, 75m),
        ["claude-sonnet"] = new(3m, 15m),
        ["claude-haiku"] = new(1m, 5m),
        ["gpt-4o-mini"] = new(0.15m, 0.60m),
        ["gpt-4o"] = new(2.5m, 10m),
        ["gpt-4.1"] = new(2m, 8m),
    };

    private static Dictionary<string, ModelPrice>? _overrides;
    private static readonly object Gate = new();

    public static string DefaultOverridePath => AppPaths.Pricing;

    /// <summary>Returns the price for a model id, or null when unknown.</summary>
    public static ModelPrice? Lookup(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var overrides = LoadOverrides();
        return Best(overrides, model) ?? CatalogPrice(model) ?? Best(Defaults, model);
    }

    /// <summary>The price <c>models.json</c> gives for exactly this model (or its dated snapshot), when it lists one.</summary>
    private static ModelPrice? CatalogPrice(string model)
    {
        // Exact id first, then the longest catalog id the model is a dated snapshot of ("gpt-4o-mini-2024-07-18"
        // must find gpt-4o-mini, not the shorter gpt-4o).
        var models = ModelCatalogLoader.Default.Models;
        var entry = models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
                    ?? models.Where(m => model.StartsWith(m.Id + "-", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(m => m.Id.Length).FirstOrDefault();
        return entry is { InputPerMillion: { } input, OutputPerMillion: { } output } ? new ModelPrice(input, output) : null;
    }

    private static ModelPrice? Best(IReadOnlyDictionary<string, ModelPrice> table, string model) =>
        table.Where(kv => model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
             .OrderByDescending(kv => kv.Key.Length)
             .Select(kv => kv.Value)
             .FirstOrDefault();

    private static Dictionary<string, ModelPrice> LoadOverrides()
    {
        lock (Gate)
        {
            if (_overrides != null) return _overrides;
            try
            {
                if (File.Exists(DefaultOverridePath))
                {
                    var json = File.ReadAllText(DefaultOverridePath);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    _overrides = new Dictionary<string, ModelPrice>(parsed ?? new(), StringComparer.OrdinalIgnoreCase);
                    return _overrides;
                }
            }
            catch { /* bad file: fall back to defaults */ }
            _overrides = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            return _overrides;
        }
    }

    /// <summary>The built-in estimates (read-only), keyed by a fragment of the model id.</summary>
    public static IReadOnlyDictionary<string, ModelPrice> BuiltIn => Defaults;

    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The rows of pricing.json, sorted by key, read from disk (not from the cache) so an editor shows what is really there.
    /// Empty when the file is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, ModelPrice>> LoadOverrideRows(string? path = null)
    {
        try
        {
            path ??= DefaultOverridePath;
            if (!File.Exists(path)) return Array.Empty<KeyValuePair<string, ModelPrice>>();
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(File.ReadAllText(path), StoreOptions);
            return (parsed ?? new()).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<KeyValuePair<string, ModelPrice>>();
        }
    }

    /// <summary>
    /// Writes the rows to pricing.json (the file <see cref="Lookup"/> reads) and drops the cache, so the next cost
    /// calculation, also in a task that is already running, uses the new prices.
    /// </summary>
    public static void SaveOverrides(IEnumerable<KeyValuePair<string, ModelPrice>> rows, string? path = null)
    {
        path ??= DefaultOverridePath;
        var table = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, price) in rows)
            if (!string.IsNullOrWhiteSpace(key))
                table[key.Trim()] = price;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(table, StoreOptions));
        File.Move(temp, path, overwrite: true);

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(DefaultOverridePath), StringComparison.OrdinalIgnoreCase))
            lock (Gate) _overrides = null;
    }

    /// <summary>Test hook: replace the override table.</summary>
    internal static void SetOverridesForTests(Dictionary<string, ModelPrice>? overrides)
    {
        lock (Gate) _overrides = overrides == null ? null : new(overrides, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cost in USD of one usage record. Cache reads default to 10% and writes to 125% of the input price.</summary>
    public static decimal Cost(string? model, TokenUsage usage)
    {
        var p = Lookup(model);
        if (p is null) return 0m;
        var read = p.CacheReadPerMillion ?? p.InputPerMillion * 0.10m;
        var write = p.CacheWritePerMillion ?? p.InputPerMillion * 1.25m;
        return (usage.PromptTokens * p.InputPerMillion
              + usage.CompletionTokens * p.OutputPerMillion
              + usage.CacheReadTokens * read
              + usage.CacheCreationTokens * write) / 1_000_000m;
    }
}
