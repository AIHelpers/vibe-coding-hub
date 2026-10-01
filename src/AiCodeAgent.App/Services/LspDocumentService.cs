using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;
using CoreLspDiagnosticItem = AiCodeAgent.Core.Models.LspDiagnosticItem;
using CoreLspDiagnosticsEvent = AiCodeAgent.Core.Models.LspDiagnosticsEvent;
using LanguageLspDiagnosticsEvent = AiCodeAgent.LanguageServices.Models.LspDiagnosticsEvent;
using LspDiagnostic = AiCodeAgent.LanguageServices.Models.Diagnostic;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Coordinates the LSP lifecycle for documents opened in the editor.
/// Sends didOpen/didChange/didClose, caches live diagnostics, raises events
/// for the UI (squiggly underlines) and publishes Core LspDiagnosticsEvent
/// onto the AgentEventBus so agents can read live diagnostics.
/// </summary>
public class LspDocumentService : IDisposable
{
    private readonly LanguageProviderRegistry _registry;
    private readonly IAgentEventBus _eventBus;
    private readonly ILogger<LspDocumentService> _logger;
    private readonly string _workspaceRoot;

    private readonly ConcurrentDictionary<string, LspDocumentState> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LspClient> _clientsByLang = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CoreLspDiagnosticItem>> _diagnosticsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when diagnostics for a file change (UI renders squiggles).</summary>
    public event EventHandler<LspDiagnosticsUpdatedEventArgs>? DiagnosticsUpdated;

    public string WorkspaceRoot => _workspaceRoot;

    public LspDocumentService(
        LanguageProviderRegistry registry,
        IAgentEventBus eventBus,
        ILogger<LspDocumentService> logger)
    {
        _registry = registry;
        _eventBus = eventBus;
        _logger = logger;
        _workspaceRoot = DetectWorkspaceRoot();
    }

    /// <summary>Notify the language server that a file was opened.</summary>
    public async Task OpenDocumentAsync(string filePath, string text, CancellationToken cancellationToken = default)
    {
        var provider = _registry.ResolveProvider(filePath);
        var client = await GetClientForFileAsync(filePath, cancellationToken);
        if (provider == null || client == null)
            return;

        var state = _documents.GetOrAdd(filePath, _ => new LspDocumentState());
        state.Version++;

        try
        {
            await client.DidOpenAsync(PathToUri(filePath), provider.LangId, state.Version, text, cancellationToken);
            state.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP didOpen failed for {File}", filePath);
        }
    }

    /// <summary>Notify the language server that a file's content changed.</summary>
    public async Task UpdateDocumentAsync(string filePath, string text, CancellationToken cancellationToken = default)
    {
        var client = await GetClientForFileAsync(filePath, cancellationToken);
        if (client == null)
            return;

        var state = _documents.GetOrAdd(filePath, _ => new LspDocumentState());
        state.Version++;

        try
        {
            await client.DidChangeAsync(PathToUri(filePath), state.Version, text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP didChange failed for {File}", filePath);
        }
    }

    /// <summary>Notify the language server that a file was closed.</summary>
    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var client = await GetClientForFileAsync(filePath, cancellationToken);
        if (client == null)
            return;

        _documents.TryRemove(filePath, out _);

        try
        {
            await client.DidCloseAsync(PathToUri(filePath), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP didClose failed for {File}", filePath);
        }
    }

    /// <summary>Get the latest cached diagnostics for a file (empty if none).</summary>
    public IReadOnlyList<CoreLspDiagnosticItem> GetCachedDiagnostics(string filePath) =>
        _diagnosticsCache.TryGetValue(NormalizePath(filePath), out var diags) ? diags : Array.Empty<CoreLspDiagnosticItem>();

    /// <summary>Request hover content at the given position.</summary>
    public async Task<Hover?> HoverAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var client = await EnsureOpenForRequestAsync(filePath, cancellationToken);
        if (client == null)
            return null;

        try
        {
            return await client.HoverAsync(PathToUri(filePath), new Position(line, character), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP hover failed for {File}", filePath);
            return null;
        }
    }

    /// <summary>Request go-to-definition at the given position.</summary>
    public async Task<List<Location>> DefinitionAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var client = await EnsureOpenForRequestAsync(filePath, cancellationToken);
        if (client == null)
            return new List<Location>();

        try
        {
            return await client.DefinitionAsync(PathToUri(filePath), new Position(line, character), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP definition failed for {File}", filePath);
            return new List<Location>();
        }
    }

    /// <summary>Request code completions at the given position.</summary>
    public async Task<List<CompletionItem>> CompletionAsync(string filePath, int line, int character, CompletionContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = await EnsureOpenForRequestAsync(filePath, cancellationToken);
        if (client == null)
            return new List<CompletionItem>();

        try
        {
            return await client.CompletionAsync(PathToUri(filePath), new Position(line, character), context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP completion failed for {File}", filePath);
            return new List<CompletionItem>();
        }
    }

    /// <summary>Request all references to a symbol at the given position.</summary>
    public async Task<List<Location>> ReferencesAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var client = await EnsureOpenForRequestAsync(filePath, cancellationToken);
        if (client == null)
            return new List<Location>();

        try
        {
            return await client.ReferencesAsync(PathToUri(filePath), new Position(line, character), true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP references failed for {File}", filePath);
            return new List<Location>();
        }
    }

    /// <summary>Request document symbols for a file.</summary>
    public async Task<List<DocumentSymbol>> DocumentSymbolsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var client = await EnsureOpenForRequestAsync(filePath, cancellationToken);
        if (client == null)
            return new List<DocumentSymbol>();

        try
        {
            return await client.DocumentSymbolsAsync(PathToUri(filePath), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP documentSymbols failed for {File}", filePath);
            return new List<DocumentSymbol>();
        }
    }

    public void Dispose()
    {
        foreach (var client in _clientsByLang.Values)
        {
            try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing LSP client"); }
        }
        _clientsByLang.Clear();
        _documents.Clear();
        _diagnosticsCache.Clear();
    }

    // ===== Private helpers =====

    private async Task<LspClient?> EnsureOpenForRequestAsync(string filePath, CancellationToken cancellationToken)
    {
        var provider = _registry.ResolveProvider(filePath);
        var client = await GetClientForFileAsync(filePath, cancellationToken);
        if (provider == null || client == null)
            return null;

        // Ensure the document is open at the server before issuing a request.
        var state = _documents.GetOrAdd(filePath, _ => new LspDocumentState());
        if (!state.IsOpen)
        {
            try
            {
                var text = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, cancellationToken) : string.Empty;
                await OpenDocumentAsync(filePath, text, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ensure LSP document open for {File}", filePath);
            }
        }

        return client;
    }

    private async Task<LspClient?> GetClientForFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var provider = _registry.ResolveProvider(filePath);
        if (provider == null)
            return null;

        if (_clientsByLang.TryGetValue(provider.LangId, out var existing))
            return existing;

        var client = await _registry.GetOrStartClientAsync(filePath, _workspaceRoot, cancellationToken);
        if (client == null)
            return null;

        // Subscribe to diagnostics once per client
        client.DiagnosticsPublished += OnDiagnosticsPublished;
        _clientsByLang[provider.LangId] = client;
        return client;
    }

    private void OnDiagnosticsPublished(object? sender, LanguageLspDiagnosticsEvent e)
    {
        var items = e.Diagnostics
            .Select(ToCoreItem)
            .ToList();

        var key = NormalizePath(e.FilePath);
        _diagnosticsCache[key] = items;

        try
        {
            // Raise UI event (renders squiggles on the editor)
            DiagnosticsUpdated?.Invoke(this, new LspDiagnosticsUpdatedEventArgs(e.FilePath, items));

            // Publish onto the AgentEventBus so agents can read live diagnostics.
            // Use the absolute path so agent queries by file path match.
            _eventBus.Publish(new CoreLspDiagnosticsEvent(e.FilePath, items));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching LSP diagnostics for {File}", e.FilePath);
        }
    }

    private static CoreLspDiagnosticItem ToCoreItem(LspDiagnostic d) => new(
        StartLine: d.Range.Start.Line,
        StartCharacter: d.Range.Start.Character,
        EndLine: d.Range.End.Line,
        EndCharacter: d.Range.End.Character,
        Severity: d.Severity.ToString().ToLowerInvariant(),
        Message: d.Message,
        Source: d.Source,
        Code: d.Code);

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string PathToUri(string path)
    {
        var full = Path.GetFullPath(path);
        return new Uri(full).AbsoluteUri;
    }

    private static string DetectWorkspaceRoot()
    {
        var start = Directory.GetCurrentDirectory();
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")) ||
                    Directory.GetFiles(dir.FullName, "*.sln").Length > 0 ||
                    Directory.GetFiles(dir.FullName, "*.slnx").Length > 0 ||
                    File.Exists(Path.Combine(dir.FullName, "package.json")) ||
                    File.Exists(Path.Combine(dir.FullName, "pyproject.toml")) ||
                    Directory.GetFiles(dir.FullName, "*.csproj").Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        catch { /* fall through */ }

        return start;
    }
}

/// <summary>Per-document state tracked for LSP synchronization.</summary>
public class LspDocumentState
{
    public int Version { get; set; }
    public bool IsOpen { get; set; }
}

/// <summary>Event args for UI diagnostics updates.</summary>
public class LspDiagnosticsUpdatedEventArgs : EventArgs
{
    public string FilePath { get; }
    public IReadOnlyList<CoreLspDiagnosticItem> Diagnostics { get; }

    public LspDiagnosticsUpdatedEventArgs(string filePath, IReadOnlyList<CoreLspDiagnosticItem> diagnostics)
    {
        FilePath = filePath;
        Diagnostics = diagnostics;
    }
}