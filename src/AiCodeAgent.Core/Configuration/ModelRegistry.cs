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

    public ModelRegistry()
    {
        // Seed the registry with a small, opinionated default catalogue.
        Register(new ModelConfig
        {
            Id = "gpt-4o",
            Provider = "openai",
            Alias = SmartAlias,
            ContextWindow = 128_000,
            MaxOutputTokens = 16_384,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "gpt-4o-mini",
            Provider = "openai",
            Alias = FastAlias,
            ContextWindow = 128_000,
            MaxOutputTokens = 16_384,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "claude-3-5-sonnet-20241022",
            Provider = "anthropic",
            Alias = SmartAlias,
            ContextWindow = 200_000,
            MaxOutputTokens = 8_192,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "claude-3-5-haiku-20241022",
            Provider = "anthropic",
            Alias = FastAlias,
            ContextWindow = 200_000,
            MaxOutputTokens = 8_192,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "llama-3.1-8b-instruct",
            Provider = "ollama",
            Alias = FastAlias,
            ContextWindow = 32_768,
            MaxOutputTokens = 4_096,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "llama-3.1-70b-instruct",
            Provider = "ollama",
            Alias = SmartAlias,
            ContextWindow = 32_768,
            MaxOutputTokens = 4_096,
            SupportsStreaming = true
        });
        Register(new ModelConfig
        {
            Id = "local-model",
            Provider = "lmstudio",
            Alias = AutoAlias,
            ContextWindow = 8_192,
            MaxOutputTokens = 2_048,
            SupportsStreaming = true
        });
    }

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