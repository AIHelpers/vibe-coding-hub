using System.Collections.Concurrent;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.LanguageServices;

/// <summary>
/// Resolves ILanguageProvider by file extension and lazily starts/reuses
/// one LspClient per language per workspace root.
/// </summary>
public sealed class LanguageProviderRegistry : IAsyncDisposable
{
    private readonly ILogger<LanguageProviderRegistry> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<ILanguageProvider> _providers;
    private readonly ConcurrentDictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<LspClient?>> _startTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _extensionToLangId = new(StringComparer.OrdinalIgnoreCase);

    public LanguageProviderRegistry(
        IEnumerable<ILanguageProvider> providers,
        ILogger<LanguageProviderRegistry> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _providers = providers.ToList();

        // Build extension -> langId lookup
        foreach (var provider in _providers)
        {
            foreach (var ext in provider.FileExtensions)
            {
                _extensionToLangId[ext] = provider.LangId;
            }
        }
    }

    public IReadOnlyList<ILanguageProvider> Providers => _providers;

    /// <summary>Resolve the language provider for a file path, or null if unsupported.</summary>
    public ILanguageProvider? ResolveProvider(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return null;

        if (!_extensionToLangId.TryGetValue(ext, out var langId))
            return null;

        return _providers.FirstOrDefault(p => p.LangId == langId);
    }

    /// <summary>Get (or lazily start) the LSP client for a file path.</summary>
    public async Task<LspClient?> GetOrStartClientAsync(string filePath, string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var provider = ResolveProvider(filePath);
        if (provider == null)
            return null;

        return await GetOrStartClientForLangAsync(provider.LangId, workspaceRoot, cancellationToken);
    }

    /// <summary>Get (or lazily start) the LSP client for a language ID.</summary>
    public async Task<LspClient?> GetOrStartClientForLangAsync(string langId, string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var key = $"{langId}|{workspaceRoot}";

        // Fast path: already started and initialized
        if (_clients.TryGetValue(key, out var existing))
            return existing;

        // Start (or await an in-flight start) exactly once per key
        var startTask = _startTasks.GetOrAdd(key, _ => StartClientAsync(langId, key, workspaceRoot));

        try
        {
            var client = await startTask.WaitAsync(cancellationToken);
            if (client != null)
            {
                _clients[key] = client;
            }
            return client;
        }
        catch
        {
            // Remove the failed start task so a future call can retry
            _startTasks.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<LspClient?> StartClientAsync(string langId, string key, string workspaceRoot)
    {
        var provider = _providers.FirstOrDefault(p => p.LangId == langId);
        if (provider == null)
            return null;

        var logger = _loggerFactory.CreateLogger<LspClient>();
        var client = new LspClient(langId, provider.Lsp, workspaceRoot, logger);

        try
        {
            await client.StartAsync();
            _clients[key] = client;
            _logger.LogInformation("LSP client started for {LangId} at {Root}", langId, workspaceRoot);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Get an existing client without starting it.</summary>
    public LspClient? GetClient(string langId, string workspaceRoot)
    {
        var key = $"{langId}|{workspaceRoot}";
        return _clients.TryGetValue(key, out var client) ? client : null;
    }

    /// <summary>Stop and dispose all LSP clients.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing LSP client for {LangId}", client.LangId);
            }
        }
        _clients.Clear();
    }
}