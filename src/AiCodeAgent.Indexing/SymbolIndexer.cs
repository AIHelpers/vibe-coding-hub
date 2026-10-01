using AiCodeAgent.Indexing.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Indexing;

/// <summary>
/// Populates the symbol table by querying LSP <c>workspace/symbol</c> and
/// per-file <c>textDocument/documentSymbol</c>. Falls back gracefully when no
/// LSP provider is active for a language.
/// </summary>
public sealed class SymbolIndexer
{
    private readonly WorkspaceIndexStore _store;
    private readonly LanguageProviderRegistry _registry;
    private readonly string _workspaceRoot;
    private readonly ILogger<SymbolIndexer> _logger;

    public SymbolIndexer(
        WorkspaceIndexStore store,
        LanguageProviderRegistry registry,
        string workspaceRoot,
        ILogger<SymbolIndexer> logger)
    {
        _store = store;
        _registry = registry;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>Index symbols for a single file using the LSP client if available.</summary>
    public async Task IndexFileSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var symbols = await FetchSymbolsForFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (symbols.Count == 0)
            return;

        await _store.ReplaceSymbolsForFileAsync(
            filePath,
            symbols,
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Indexed {Count} symbols for {File}", symbols.Count, filePath);
    }

    /// <summary>Index symbols for all supported files in the workspace (best-effort).</summary>
    public async Task IndexWorkspaceSymbolsAsync(
        CancellationToken cancellationToken = default)
    {
        var files = await _store.GetAllFilesAsync(cancellationToken).ConfigureAwait(false);
        var providerFiles = files
            .Select(f => f.Path)
            .Where(f => _registry.ResolveProvider(f) != null)
            .ToList();

        _logger.LogInformation("Indexing symbols for {Count} language-supported files", providerFiles.Count);

        var indexed = 0;
        foreach (var file in providerFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var symbols = await FetchSymbolsForFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (symbols.Count > 0)
            {
                await _store.ReplaceSymbolsForFileAsync(file, symbols, cancellationToken).ConfigureAwait(false);
                indexed++;
            }
        }

        _logger.LogInformation("Symbol indexing complete: {Indexed} files indexed", indexed);
    }

    /// <summary>Fetch symbols for a file via LSP document symbols.</summary>
    public async Task<List<IndexedSymbol>> FetchSymbolsForFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await _registry.GetOrStartClientAsync(filePath, _workspaceRoot, cancellationToken)
                .ConfigureAwait(false);
            if (client == null)
                return new List<IndexedSymbol>();

            var uri = PathToUri(filePath);
            var documentSymbols = await client.DocumentSymbolsAsync(uri, cancellationToken)
                .ConfigureAwait(false);

            var results = new List<IndexedSymbol>();
            FlattenDocumentSymbols(documentSymbols, filePath, results);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch LSP symbols for {File}", filePath);
            return new List<IndexedSymbol>();
        }
    }

    /// <summary>Fetch workspace symbols via LSP <c>workspace/symbol</c>.</summary>
    public async Task<List<IndexedSymbol>> FetchWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var results = new List<IndexedSymbol>();

        foreach (var langId in _registry.Providers.Select(p => p.LangId))
        {
            try
            {
                var client = await _registry.GetOrStartClientForLangAsync(langId, _workspaceRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (client == null)
                    continue;

                var symbolInfos = await client.WorkspaceSymbolsAsync(query, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in symbolInfos)
                {
                    var filePath = UriToPath(info.Location.Uri);
                    results.Add(new IndexedSymbol(
                        Name: info.Name,
                        Kind: SymbolKindToString(info.Kind),
                        FilePath: filePath,
                        Line: info.Location.Range.Start.Line + 1,
                        WorkspaceId: _store.WorkspaceId));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "workspace/symbol failed for language {LangId}", langId);
            }
        }

        return results;
    }

    private void FlattenDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols,
        string filePath,
        List<IndexedSymbol> output)
    {
        foreach (var sym in symbols)
        {
            var line = sym.SelectionRange?.Start.Line ?? sym.Range?.Start.Line ?? 0;
            output.Add(new IndexedSymbol(
                Name: sym.Name,
                Kind: SymbolKindToString(sym.Kind),
                FilePath: filePath,
                Line: line + 1,
                WorkspaceId: _store.WorkspaceId));

            if (sym.Children.Count > 0)
            {
                FlattenDocumentSymbols(sym.Children, filePath, output);
            }
        }
    }

    private static string SymbolKindToString(int kind) => kind switch
    {
        1 => "File",
        2 => "Module",
        3 => "Namespace",
        4 => "Package",
        5 => "Class",
        6 => "Method",
        7 => "Property",
        8 => "Field",
        9 => "Constructor",
        10 => "Enum",
        11 => "Interface",
        12 => "Function",
        13 => "Variable",
        14 => "Constant",
        15 => "String",
        16 => "Number",
        17 => "Boolean",
        18 => "Array",
        19 => "Object",
        20 => "Key",
        21 => "Null",
        22 => "EnumMember",
        23 => "Struct",
        24 => "Event",
        25 => "Operator",
        26 => "TypeParameter",
        _ => "Symbol"
    };

    private static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri[7..]);
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
                path = path[1..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        return uri;
    }
}