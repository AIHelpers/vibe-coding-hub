using System.Collections.Concurrent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers;

/// <summary>
/// Serves the provider a flow step names (E5-S3). Each name gets one <see cref="ConfiguredAiProvider"/>, which reads the
/// settings on every call, so a key changed in Settings reaches steps that are already queued. Names without settings
/// resolve to null.
/// </summary>
public sealed class ConfiguredProviderResolver : IAiProviderResolver
{
    private readonly Func<string?> _defaultName;
    private readonly Func<string, ProviderConfiguration?> _getProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IAiProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ConfiguredProviderResolver(Func<string?> defaultName, Func<string, ProviderConfiguration?> getProvider, ILoggerFactory loggerFactory)
    {
        _defaultName = defaultName;
        _getProvider = getProvider;
        _loggerFactory = loggerFactory;
    }

    public string? DefaultProviderName => _defaultName()?.Trim().ToLowerInvariant() is { Length: > 0 } n ? n : null;

    public IAiProvider? Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        var name = providerName.Trim().ToLowerInvariant();
        if (_getProvider(name) == null) return null;
        return _providers.GetOrAdd(name, n => new ConfiguredAiProvider(() => Settings(n), _loggerFactory));
    }

    private (string Name, ProviderConfiguration Config)? Settings(string name)
    {
        var cfg = _getProvider(name);
        return cfg == null ? null : (name, WithApiKey(name, cfg));
    }

    /// <summary>The settings with the API key from the environment (<c>AI_CODE_AGENT_{NAME}_API_KEY</c> or the legacy <c>AIAGENT_{NAME}_API_KEY</c>) taking precedence.</summary>
    public static ProviderConfiguration WithApiKey(string providerName, ProviderConfiguration cfg)
    {
        var upper = providerName.ToUpperInvariant();
        var apiKey = Environment.GetEnvironmentVariable($"AI_CODE_AGENT_{upper}_API_KEY")
                  ?? Environment.GetEnvironmentVariable($"AIAGENT_{upper}_API_KEY")
                  ?? cfg.ApiKey;
        return cfg with { ApiKey = apiKey };
    }
}
