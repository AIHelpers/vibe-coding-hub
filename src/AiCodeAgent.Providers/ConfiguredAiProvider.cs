using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers;

/// <summary>
/// The provider the app talks to, always built from the <em>current</em> settings. When the provider, URL, key or
/// model changes (setup wizard, Settings), the next call uses a new inner provider, so no restart is needed.
/// </summary>
public sealed class ConfiguredAiProvider : IAiProvider
{
    private readonly Func<(string Name, ProviderConfiguration Config)?> _resolve;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private IAiProvider? _inner;
    private string _key = string.Empty;

    /// <param name="resolve">Returns the provider name and settings to use now, or null when none is configured.</param>
    public ConfiguredAiProvider(Func<(string Name, ProviderConfiguration Config)?> resolve, ILoggerFactory loggerFactory)
    {
        _resolve = resolve;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The provider for the settings as they are right now (rebuilt only when they changed).</summary>
    public IAiProvider Current
    {
        get
        {
            var resolved = _resolve();
            var key = resolved is { } r
                ? string.Join("|", r.Name, r.Config.BaseUrl, r.Config.ApiKey, r.Config.DefaultModel, r.Config.VerifySsl, r.Config.TimeoutSeconds, r.Config.Headers.Count)
                : string.Empty;
            lock (_gate)
            {
                if (_inner == null || key != _key)
                {
                    _inner = resolved is { } x
                        ? ProviderFactory.CreateOrFallback(x.Name, x.Config, _loggerFactory)
                        : new NoOpAiProvider(_loggerFactory.CreateLogger<NoOpAiProvider>(), "No AI provider is configured.");
                    _key = key;
                }
                return _inner;
            }
        }
    }

    public string Name => Current.Name;
    public string[] SupportedModels => Current.SupportedModels;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) =>
        Current.CompleteAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default) =>
        Current.StreamAsync(request, cancellationToken);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Current.IsAvailableAsync(cancellationToken);

    public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        Current.GetAvailableModelsAsync(cancellationToken);
}
