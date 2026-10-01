using AiCodeAgent.Indexing.Models;

namespace AiCodeAgent.Indexing;

/// <summary>
/// High-level query facade over the workspace index. Applies fuzzy scoring
/// to files and symbols, returning ranked results for consumers such as the
/// @-mention popup, SharedContextStore, and command palette.
/// </summary>
public sealed class WorkspaceIndexQueryService
{
    private readonly WorkspaceIndexStore _store;

    public string WorkspaceId => _store.WorkspaceId;

    public WorkspaceIndexQueryService(WorkspaceIndexStore store)
    {
        _store = store;
    }

    /// <summary>Fuzzy search across indexed files.</summary>
    public async Task<List<FileMatch>> SearchFilesAsync(
        string? filter = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = filter ?? string.Empty;

        // SQL LIKE is too strict for fuzzy subsequence matching. Fetch a broad
        // candidate set (all files when the filter is short, otherwise a loose
        // per-character match) and let the fuzzy matcher do the real scoring.
        var sqlFilter = BuildLooseSqlFilter(query);
        var sqlResults = await _store.QueryFilesAsync(
            new FileQueryOptions { Filter = sqlFilter, Limit = Math.Max(limit * 5, 200) },
            cancellationToken).ConfigureAwait(false);

        var results = sqlResults
            .Select(r => new
            {
                Match = r,
                Score = FuzzyMatcher.Score(r.Path, query)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Match)
            .ToList();

        return results;
    }

    /// <summary>Fuzzy search across indexed symbols.</summary>
    public async Task<List<SymbolMatch>> SearchSymbolsAsync(
        string? filter = null,
        string? filePath = null,
        string? kind = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = filter ?? string.Empty;
        var sqlFilter = BuildLooseSqlFilter(query);

        var sqlResults = await _store.QuerySymbolsAsync(
            new SymbolQueryOptions
            {
                Filter = sqlFilter,
                FilePath = filePath,
                Kind = kind,
                Limit = Math.Max(limit * 5, 200)
            },
            cancellationToken).ConfigureAwait(false);

        var results = sqlResults
            .Select(r => new
            {
                Match = r,
                Score = FuzzyMatcher.Score(r.Name, query)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Match.Name)
            .Take(limit)
            .Select(x => x.Match)
            .ToList();

        return results;
    }

    /// <summary>All indexed files.</summary>
    public Task<List<IndexedFile>> GetAllFilesAsync(CancellationToken cancellationToken = default) =>
        _store.GetAllFilesAsync(cancellationToken);

    /// <summary>A single indexed file by path.</summary>
    public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default) =>
        _store.GetFileAsync(path, cancellationToken);

    /// <summary>Indexed file count.</summary>
    public Task<int> GetFileCountAsync(CancellationToken cancellationToken = default) =>
        _store.GetFileCountAsync(cancellationToken);

    /// <summary>Indexed symbol count.</summary>
    public Task<int> GetSymbolCountAsync(CancellationToken cancellationToken = default) =>
        _store.GetSymbolCountAsync(cancellationToken);

    /// <summary>Get all symbols for a specific file.</summary>
    public async Task<List<SymbolMatch>> GetSymbolsForFileAsync(
        string filePath,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var results = await _store.QuerySymbolsAsync(
            new SymbolQueryOptions { FilePath = filePath, Limit = limit },
            cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Builds a loose SQL LIKE filter that matches any file containing any
    /// single character of the query. This returns a broad candidate set that
    /// the fuzzy matcher then scores precisely.
    /// </summary>
    private static string BuildLooseSqlFilter(string query)
    {
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        // For short queries, just use the first character (broad enough).
        // For longer queries, use the first and last characters to narrow
        // the candidate set while still allowing fuzzy subsequence matches.
        if (query.Length <= 1)
            return query;

        var first = query[0];
        var last = query[^1];
        return $"{first}%{last}";
    }
}
