using System.Collections.Generic;

namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Resolves model aliases (e.g. "Auto", "Fast", "Smart") to concrete
/// <see cref="ModelConfig"/> instances and enumerates known models per
/// provider. The registry ships with a default catalogue but can be
/// extended at runtime via <see cref="Register"/>.
/// </summary>
public class ModelRegistry
{
    /// <summary>Well-known alias that selects the provider's default model.</summary>
    public const string AutoAlias = "Auto";

    /// <summary>Well-known alias that selects a fast/cheap model.</summary>
    public const string FastAlias = "Fast";

    /// <summary>Well-known alias that selects a high-capability model.</summary>
    public const string SmartAlias = "Smart";

    private readonly Dictionary<string, ModelConfig> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelConfig>> _byAlias = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="catalog">The model list to seed from; defaults to <see cref="ModelCatalogLoader.Default"/>.</param>
    public ModelRegistry(ModelCatalog? catalog = null)
    {
        // Seed from the catalog: Auto = balanced tier, Fast = fast tier, Smart = frontier tier (best model first).
        // Retired models stay resolvable by id (so an old setting shows what it is) but never get an alias.
        foreach (var m in (catalog ?? ModelCatalogLoader.Default).Models)
        {
            Register(new ModelConfig
            {
                Id = m.Id,
                Provider = m.Provider,
                Alias = m.Retired ? null : AliasOf(m.Tier),
                ContextWindow = m.ContextWindow,
                MaxOutputTokens = 8_192,
                SupportsStreaming = true
            });
        }
    }

    private static string AliasOf(ModelTier tier) => tier switch
    {
        ModelTier.Fast => FastAlias,
        ModelTier.Frontier => SmartAlias,
        _ => AutoAlias
    };

    /// <summary>Registers (or replaces) a model configuration.</summary>
    public void Register(ModelConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new ArgumentException("ModelConfig.Id must be non-empty.", nameof(config));

        _byId[config.Id] = config;

        if (!string.IsNullOrWhiteSpace(config.Alias))
        {
            if (!_byAlias.TryGetValue(config.Alias!, out var list))
            {
                list = new List<ModelConfig>();
                _byAlias[config.Alias!] = list;
            }
            // Replace any existing entry for the same provider+id under this alias.
            list.RemoveAll(m => string.Equals(m.Id, config.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(config);
        }
    }

    /// <summary>Attempts to retrieve a model by its concrete id.</summary>
    public ModelConfig? GetById(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        return _byId.TryGetValue(modelId, out var config) ? config : null;
    }

    /// <summary>
    /// Resolves a model reference — which may be a concrete id or an alias —
    /// to a concrete <see cref="ModelConfig"/>. When <paramref name="alias"/>
    /// resolves to multiple models, the one whose <see cref="ModelConfig.Provider"/>
    /// matches <paramref name="preferredProvider"/> is chosen; otherwise the
    /// first registered model for that alias is returned.
    /// </summary>
    /// <param name="aliasOrId">An alias (e.g. "Auto") or a concrete model id.</param>
    /// <param name="preferredProvider">Provider name used to disambiguate aliases.</param>
    public ModelConfig? Resolve(string aliasOrId, string? preferredProvider = null)
    {
        if (string.IsNullOrWhiteSpace(aliasOrId))
            return null;

        // Direct id match wins first.
        if (_byId.TryGetValue(aliasOrId, out var direct))
            return direct;

        if (_byAlias.TryGetValue(aliasOrId, out var list) && list.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(preferredProvider))
            {
                var match = list.Find(m =>
                    string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return list[0];
        }

        return null;
    }

    /// <summary>Lists all registered models for a given provider.</summary>
    public IReadOnlyList<ModelConfig> ListForProvider(string provider)
    {
        var results = new List<ModelConfig>();
        foreach (var config in _byId.Values)
        {
            if (string.Equals(config.Provider, provider, StringComparison.OrdinalIgnoreCase))
                results.Add(config);
        }
        return results;
    }

    /// <summary>Lists all models registered under a given alias.</summary>
    public IReadOnlyList<ModelConfig> ListForAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return Array.Empty<ModelConfig>();
        return _byAlias.TryGetValue(alias, out var list) ? list : Array.Empty<ModelConfig>();
    }

    /// <summary>Lists all known aliases.</summary>
    public IReadOnlyCollection<string> ListAliases() => _byAlias.Keys;

    /// <summary>Lists all registered model ids.</summary>
    public IReadOnlyCollection<string> ListIds() => _byId.Keys;
}