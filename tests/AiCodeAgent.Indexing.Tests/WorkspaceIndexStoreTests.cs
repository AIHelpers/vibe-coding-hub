using AiCodeAgent.Indexing;
using AiCodeAgent.Indexing.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Indexing.Tests;

public class WorkspaceIndexStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceIndexStore _store;
    private readonly ILogger<WorkspaceIndexStore> _logger;

    public WorkspaceIndexStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = Substitute.For<ILogger<WorkspaceIndexStore>>();
        _store = new WorkspaceIndexStore(_tempDir, _logger, indexRoot: _tempDir);
    }

    [Fact]
    public void ComputeWorkspaceId_IsStable()
    {
        var id1 = WorkspaceId.Compute(@"C:\Repo\Workspace");
        var id2 = WorkspaceId.Compute(@"C:\Repo\Workspace");

        Assert.Equal(id1, id2);
        Assert.Equal(12, id1.Length);
    }

    [Fact]
    public async Task ReplaceAllFiles_ThenQuery_ReturnsMatches()
    {
        _store.Open();

        var now = DateTime.UtcNow;
        await _store.ReplaceAllFilesAsync(new[]
        {
            new IndexedFile(@"C:\Repo\src\Program.cs", now, "abc123"),
            new IndexedFile(@"C:\Repo\src\Models\User.cs", now, "def456"),
            new IndexedFile(@"C:\Repo\tests\TestRunner.cs", now, "789abc")
        });

        var count = await _store.GetFileCountAsync();
        Assert.Equal(3, count);

        var matches = await _store.QueryFilesAsync(new FileQueryOptions { Filter = "user" });
        Assert.Single(matches);
        Assert.Contains("User.cs", matches[0].Path);

        var all = await _store.GetAllFilesAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task UpsertFile_ThenGetFile_ReturnsRecord()
    {
        _store.Open();

        var file = new IndexedFile(@"C:\Repo\src\app.cs", DateTime.UtcNow, "hash-1");
        await _store.UpsertFileAsync(file);

        var fetched = await _store.GetFileAsync(@"C:\Repo\src\app.cs");
        Assert.NotNull(fetched);
        Assert.Equal("hash-1", fetched!.Hash);

        // Update
        var updated = file with { Hash = "hash-2" };
        await _store.UpsertFileAsync(updated);

        fetched = await _store.GetFileAsync(@"C:\Repo\src\app.cs");
        Assert.NotNull(fetched);
        Assert.Equal("hash-2", fetched.Hash);
    }

    [Fact]
    public async Task ReplaceSymbols_ThenQuerySymbols_ReturnsMatches()
    {
        _store.Open();

        var symbols = new[]
        {
            new IndexedSymbol("Program", "Class", @"C:\Repo\src\Program.cs", 1, _store.WorkspaceId),
            new IndexedSymbol("Main", "Method", @"C:\Repo\src\Program.cs", 8, _store.WorkspaceId),
            new IndexedSymbol("UserService", "Class", @"C:\Repo\src\Services\UserService.cs", 3, _store.WorkspaceId)
        };

        await _store.ReplaceSymbolsForFileAsync(@"C:\Repo\src\Program.cs", symbols.Where(s => s.FilePath == @"C:\Repo\src\Program.cs").ToList());
        await _store.ReplaceSymbolsForFileAsync(@"C:\Repo\src\Services\UserService.cs", symbols.Where(s => s.FilePath == @"C:\Repo\src\Services\UserService.cs").ToList());

        var symbolCount = await _store.GetSymbolCountAsync();
        Assert.Equal(3, symbolCount);

        var matches = await _store.QuerySymbolsAsync(new SymbolQueryOptions { Filter = "main" });
        Assert.Single(matches);
        Assert.Equal("Main", matches[0].Name);
        Assert.Equal(8, matches[0].Line);
    }

    [Fact]
    public async Task DeleteFile_RemovesFileAndSymbols()
    {
        _store.Open();

        await _store.UpsertFileAsync(new IndexedFile(@"C:\Repo\src\delete.cs", DateTime.UtcNow, "h"));
        await _store.ReplaceSymbolsForFileAsync(
            @"C:\Repo\src\delete.cs",
            new[] { new IndexedSymbol("DeleteMe", "Class", @"C:\Repo\src\delete.cs", 1, _store.WorkspaceId) });

        await _store.DeleteFileAsync(@"C:\Repo\src\delete.cs");

        Assert.Null(await _store.GetFileAsync(@"C:\Repo\src\delete.cs"));
        var symbols = await _store.QuerySymbolsAsync(new SymbolQueryOptions { Filter = "deleteme" });
        Assert.Empty(symbols);
    }

    [Fact]
    public async Task DatabasePath_UsesWorkspaceHash()
    {
        var id = WorkspaceId.Compute(_tempDir);
        Assert.EndsWith($"{id}.db", _store.DatabasePath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}