using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Configuration;

/// <summary>How capable (and expensive) a model is. The chat picker's Fast / Auto / Smart choices map to these.</summary>
public enum ModelTier
{
    /// <summary>Cheap and quick (the "Fast" choice).</summary>
    Fast,
    /// <summary>The everyday default (the "Auto" choice).</summary>
    Balanced,
    /// <summary>The most capable model (the "Smart" choice).</summary>
    Frontier
}

/// <summary>One entry of <c>models.json</c>: what we know about a model besides whether an account can use it.</summary>
public sealed record CatalogModel(
    string Id,
    string Provider,
    ModelTier Tier,
    int ContextWindow = 128_000,
    decimal? InputPerMillion = null,
    decimal? OutputPerMillion = null,
    bool Retired = false);

/// <summary>A model shown in a picker: catalog metadata merged with what the account really lists.</summary>
/// <param name="Tier">Null for a model the catalog does not know (a local model, a brand-new release).</param>
public sealed record ModelOption(string Id, ModelTier? Tier, bool Retired, int? ContextWindow = null)
{
    /// <summary>The text the picker shows: the id, with "retired" next to retired models.</summary>
    public string Label => Retired ? $"{Id}  (retired)" : Id;
}

/// <summary>
/// The known models per provider (from <c>models.json</c>), in best-first order inside each tier. It decides the
/// default model, maps Auto / Fast / Smart to tiers and flags retired models. It never calls a provider: the
/// account's real model list is merged in by <see cref="Merge"/>.
/// </summary>
public sealed class ModelCatalog
{
    /// <summary>Alias of the balanced tier.</summary>
    public const string AutoAlias = "Auto";
    /// <summary>Alias of the fast tier.</summary>
    public const string FastAlias = "Fast";
    /// <summary>Alias of the frontier tier.</summary>
    public const string SmartAlias = "Smart";

    private static readonly Regex DateSuffix = new(@"-\d{8}$", RegexOptions.Compiled);

    private readonly List<CatalogModel> _models;

    public static ModelCatalog Empty { get; } = new(Array.Empty<CatalogModel>());

    public ModelCatalog(IEnumerable<CatalogModel> models)
    {
        // A later entry for the same provider + id replaces the earlier one but keeps the earlier position.
        _models = new List<CatalogModel>();
        foreach (var m in models)
        {
            var at = _models.FindIndex(x => Same(x, m.Provider, m.Id));
            if (at >= 0) _models[at] = m; else _models.Add(m);
        }
    }

    public IReadOnlyList<CatalogModel> Models => _models;

    /// <summary>Parses <c>{ "models": [ { "id", "provider", "tier", "contextWindow", "inputPerMillion", "outputPerMillion", "retired" } ] }</c>.</summary>
    /// <exception cref="FormatException">The text is not JSON of that shape.</exception>
    public static ModelCatalog Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var list = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("models", out var m) ? m : default;
            if (list.ValueKind != JsonValueKind.Array) throw new FormatException("models.json has no \"models\" array.");

            var result = new List<CatalogModel>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                var provider = Str(e, "provider");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(provider)) continue;
                result.Add(new CatalogModel(
                    id.Trim(),
                    provider.Trim().ToLowerInvariant(),
                    ParseTier(Str(e, "tier")),
                    Int(e, "contextWindow") ?? 128_000,
                    Dec(e, "inputPerMillion"),
                    Dec(e, "outputPerMillion"),
                    e.TryGetProperty("retired", out var r) && r.ValueKind == JsonValueKind.True));
            }
            return new ModelCatalog(result);
        }
        catch (JsonException ex)
        {
            throw new FormatException("models.json is not valid JSON: " + ex.Message, ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static ModelTier ParseTier(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "fast" => ModelTier.Fast,
        "frontier" or "smart" => ModelTier.Frontier,
        _ => ModelTier.Balanced
    };

    /// <summary>A copy of this catalog where <paramref name="overrides"/> replace entries with the same provider + id and add new ones.</summary>
    public ModelCatalog Overlay(ModelCatalog overrides)
    {
        var all = new List<CatalogModel>(_models);
        all.AddRange(overrides._models);
        return new ModelCatalog(all);
    }

    private static bool Same(CatalogModel m, string provider, string id) =>
        string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>The entry for exactly this provider + id, or null.</summary>
    public CatalogModel? Find(string provider, string id) =>
        _models.FirstOrDefault(m => Same(m, provider ?? string.Empty, id ?? string.Empty));

    /// <summary>
    /// Like <see cref="Find"/>, but a dated snapshot id such as <c>claude-sonnet-4-5-20250929</c> also finds the
    /// entry of its undated name, so an account's own ids still get metadata.
    /// </summary>
    public CatalogModel? Lookup(string provider, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Find(provider, id) ?? Find(provider, DateSuffix.Replace(id, string.Empty));
    }

    /// <summary>True when the catalog knows the model and marks it retired. Unknown models are not retired.</summary>
    public bool IsRetired(string provider, string? id) =>
        !string.IsNullOrWhiteSpace(id) && Lookup(provider, id) is { Retired: true };

    /// <summary>Catalog entries of a provider in file order, without retired ones unless asked.</summary>
    public IReadOnlyList<CatalogModel> ForProvider(string provider, bool includeRetired = false) =>
        _models.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)
                           && (includeRetired || !m.Retired)).ToList();

    /// <summary>
    /// The model a new setup should use: the provider's first <see cref="ModelTier.Balanced"/> model that the
    /// account actually lists (<paramref name="available"/>), never a retired one. Without a list (offline, or a
    /// fresh config) it is the first balanced model of the catalog. When the account lists no balanced model, the
    /// first other known, non-retired one it does list. Null when nothing fits.
    /// </summary>
    public string? DefaultModel(string provider, IEnumerable<string>? available = null)
    {
        var listed = Listed(available);
        var live = ForProvider(provider);

        var balanced = Pick(live.Where(m => m.Tier == ModelTier.Balanced), listed);
        if (balanced != null) return balanced;
        if (listed == null) return null;
        return Pick(live.Where(m => m.Tier != ModelTier.Balanced), listed);
    }

    /// <summary>The best non-retired model of a tier (best-first file order) that the account lists; tiers fall back to a neighbour.</summary>
    public string? ModelForTier(string provider, ModelTier tier, IEnumerable<string>? available = null)
    {
        var listed = Listed(available);
        var live = ForProvider(provider);
        foreach (var t in Preference(tier))
        {
            var id = Pick(live.Where(m => m.Tier == t), listed);
            if (id != null) return id;
        }
        return null;
    }

    private static ModelTier[] Preference(ModelTier tier) => tier switch
    {
        ModelTier.Fast => new[] { ModelTier.Fast, ModelTier.Balanced, ModelTier.Frontier },
        ModelTier.Frontier => new[] { ModelTier.Frontier, ModelTier.Balanced, ModelTier.Fast },
        _ => new[] { ModelTier.Balanced, ModelTier.Frontier, ModelTier.Fast }
    };

    /// <summary>"Auto" is the balanced tier, "Fast" the fast one and "Smart" the frontier one (case-insensitive).</summary>
    public static bool TryTierOfAlias(string? alias, out ModelTier tier)
    {
        switch (alias?.Trim().ToLowerInvariant())
        {
            case "auto": tier = ModelTier.Balanced; return true;
            case "fast": tier = ModelTier.Fast; return true;
            case "smart": tier = ModelTier.Frontier; return true;
            default: tier = default; return false;
        }
    }

    /// <summary>The concrete model for Auto / Fast / Smart, or null when <paramref name="alias"/> is not one of them.</summary>
    public string? ResolveAlias(string? alias, string provider, IEnumerable<string>? available = null) =>
        TryTierOfAlias(alias, out var tier) ? ModelForTier(provider, tier, available) : null;

    /// <summary>
    /// What a run should send as its model for the picker's value: Fast / Smart become that tier's model, a
    /// concrete id stays as it is, and Auto (or nothing) returns null so the provider's configured default is used.
    /// </summary>
    public string? ModelForRun(string? selected, string provider, IEnumerable<string>? available = null)
    {
        if (string.IsNullOrWhiteSpace(selected)) return null;
        if (!TryTierOfAlias(selected, out var tier))
            return IsRetired(provider, selected) ? null : selected; // a retired id would only fail: use the provider default
        return tier == ModelTier.Balanced ? null : ModelForTier(provider, tier, available);
    }

    /// <summary>
    /// Merges the account's real model ids with the catalog metadata: every id the account lists appears once,
    /// marked retired when the catalog says so. Non-retired known models come first (in catalog order), then
    /// unknown ones, then retired ones. Catalog models the account does not list are not included.
    /// </summary>
    public IReadOnlyList<ModelOption> Merge(string provider, IEnumerable<string> discovered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(ModelOption Option, int Group, int Order)>();
        var position = 0;
        foreach (var raw in discovered)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var meta = Lookup(provider, id);
            var option = new ModelOption(id, meta?.Tier, meta?.Retired ?? false, meta?.ContextWindow);
            var group = option.Retired ? 2 : meta == null ? 1 : 0;
            var order = meta == null ? position : _models.IndexOf(meta);
            rows.Add((option, group, order));
            position++;
        }
        return rows.OrderBy(r => r.Group).ThenBy(r => r.Order).Select(r => r.Option).ToList();
    }

    /// <summary>The offline list: the provider's non-retired catalog models.</summary>
    public IReadOnlyList<ModelOption> Fallback(string provider) =>
        ForProvider(provider).Select(m => new ModelOption(m.Id, m.Tier, false, m.ContextWindow)).ToList();

    /// <summary>The account's id that stands for a catalog id: the id itself, or its dated snapshot.</summary>
    private static string? Listing(string catalogId, HashSet<string>? listed)
    {
        if (listed == null) return catalogId;
        if (listed.Contains(catalogId)) return listed.First(x => string.Equals(x, catalogId, StringComparison.OrdinalIgnoreCase));
        return listed.FirstOrDefault(x => x.StartsWith(catalogId + "-", StringComparison.OrdinalIgnoreCase)
                                          && DateSuffix.IsMatch(x) && x.Length == catalogId.Length + 9);
    }

    private static string? Pick(IEnumerable<CatalogModel> candidates, HashSet<string>? listed)
    {
        foreach (var c in candidates)
        {
            var id = Listing(c.Id, listed);
            if (id != null) return id;
        }
        return null;
    }

    private static HashSet<string>? Listed(IEnumerable<string>? available) =>
        available == null ? null : new HashSet<string>(available.Where(a => !string.IsNullOrWhiteSpace(a)), StringComparer.OrdinalIgnoreCase);
}
