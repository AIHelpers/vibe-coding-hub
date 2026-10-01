namespace AiCodeAgent.Indexing.Models;

/// <summary>A single file entry in the workspace index.</summary>
public record IndexedFile(
    string Path,
    DateTime LastModified,
    string Hash);

/// <summary>A single symbol entry in the workspace index.</summary>
public record IndexedSymbol(
    string Name,
    string Kind,
    string FilePath,
    int Line,
    string WorkspaceId);

/// <summary>Result of a fuzzy file query.</summary>
public record FileMatch(
    string Path,
    string FileName,
    int Score);

/// <summary>Result of a symbol query.</summary>
public record SymbolMatch(
    string Name,
    string Kind,
    string FilePath,
    int Line,
    int Score);

/// <summary>Filter options for file queries.</summary>
public record FileQueryOptions
{
    public string? Filter { get; init; }
    public int Limit { get; init; } = 50;
    public bool IncludeDirectories { get; init; }
}

/// <summary>Filter options for symbol queries.</summary>
public record SymbolQueryOptions
{
    public string? Filter { get; init; }
    public string? FilePath { get; init; }
    public string? Kind { get; init; }
    public int Limit { get; init; } = 50;
}