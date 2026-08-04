using AiCodeAgent.Indexing;
using AiCodeAgent.Indexing.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Indexing.Tests;

public class WorkspaceIndexQueryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceIndexStore _store;
    private readonly WorkspaceIndexQueryService _query;
    private readonly ILogger<WorkspaceIndexStore> _logger;

    public WorkspaceIndexQueryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-query-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = Substitute.For<ILogger<WorkspaceIndexStore>>();
        _store = new WorkspaceIndexStore(_tempDir, _logger, indexRoot: _tempDir);
        _query = new WorkspaceIndexQueryService(_store);
        _store.Open();
    }

    [Fact]
    public async Task SearchFiles_UsesFuzzyScoring()
    {
        var now = DateTime.UtcNow;
        await _store.ReplaceAllFilesAsync(new[]
        {
            new IndexedFile(@"C:\Repo\src\ChatViewModel.cs", now, "h1"),
            new IndexedFile(@"C:\Repo\src\Models\ChatMessage.cs", now, "h2"),
            new IndexedFile(@"C:\Repo\tests\TerminalTests.cs", now, "h3")
        });

        var results = await _query.SearchFilesAsync("chatvm", limit: 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Path.Contains("ChatViewModel.cs"));
    }

    [Fact]
    public async Task SearchFiles_EmptyFilter_ReturnsAll()
    {
        var now = DateTime.UtcNow;
        await _store.ReplaceAllFilesAsync(new[]
        {
            new IndexedFile(@"C:\Repo\a.cs", now, "h1"),
            new IndexedFile(@"C:\Repo\b.cs", now, "h2")
        });

        var results = await _query.SearchFilesAsync(limit: 10);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchSymbols_ReturnsScoredMatches()
    {
        await _store.ReplaceSymbolsForFileAsync(
            @"C:\Repo\src\Program.cs",
            new[]
            {
                new IndexedSymbol("Program", "Class", @"C:\Repo\src\Program.cs", 1, _store.WorkspaceId),
                new IndexedSymbol("Main", "Method", @"C:\Repo\src\Program.cs", 8, _store.WorkspaceId),
                new IndexedSymbol("UserService", "Class", @"C:\Repo\src\Services\UserService.cs", 3, _store.WorkspaceId)
            });

        var results = await _query.SearchSymbolsAsync("main");

        Assert.Single(results);
        Assert.Equal("Main", results[0].Name);
    }

    [Fact]
    public async Task GetSymbolsForFile_ReturnsOnlyThatFile()
    {
        await _store.ReplaceSymbolsForFileAsync(
            @"C:\Repo\src\Program.cs",
            new[]
            {
                new IndexedSymbol("Program", "Class", @"C:\Repo\src\Program.cs", 1, _store.WorkspaceId),
                new IndexedSymbol("Main", "Method", @"C:\Repo\src\Program.cs", 8, _store.WorkspaceId)
            });

        var results = await _query.GetSymbolsForFileAsync(@"C:\Repo\src\Program.cs");

        Assert.Equal(2, results.Count);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}