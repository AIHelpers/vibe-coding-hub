using AiCodeAgent.Indexing;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Indexing.Tests;

public class WorkspaceIndexerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceIndexStore _store;
    private readonly ILogger<WorkspaceIndexStore> _storeLogger;
    private readonly ILogger<WorkspaceIndexer> _indexerLogger;

    public WorkspaceIndexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-indexer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storeLogger = Substitute.For<ILogger<WorkspaceIndexStore>>();
        _indexerLogger = Substitute.For<ILogger<WorkspaceIndexer>>();
        _store = new WorkspaceIndexStore(_tempDir, _storeLogger, indexRoot: _tempDir);
    }

    [Fact]
    public async Task FullScan_IndexesFiles_SkipsIgnored()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "src", "Program.cs"), "class Program { }");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "src", "app.py"), "print('hi')");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bin", "out.dll"), "binary");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".git", "config"), "repo");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "node_modules", "lib.js"), "const x = 1;");

        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".gitignore"), "*.dll\nnode_modules/\n");

        using var indexer = new WorkspaceIndexer(_store, _indexerLogger, _tempDir, enableWatcher: false);

        // Act
        await indexer.FullScanAsync();

        // Assert
        var files = await _store.GetAllFilesAsync();
        var paths = files.Select(f => f.Path).ToList();

        Assert.Contains(Path.Combine(_tempDir, "src", "Program.cs"), paths);
        Assert.Contains(Path.Combine(_tempDir, "src", "app.py"), paths);
        Assert.DoesNotContain(Path.Combine(_tempDir, "bin", "out.dll"), paths); // skippable dir/file
        Assert.DoesNotContain(Path.Combine(_tempDir, ".git", "config"), paths); // hidden dir
        Assert.DoesNotContain(Path.Combine(_tempDir, "node_modules", "lib.js"), paths); // gitignore dir
    }

    [Fact]
    public async Task ScanCompleted_Event_Raised()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.cs"), "class A { }");

        var completed = false;
        using var indexer = new WorkspaceIndexer(_store, _indexerLogger, _tempDir, enableWatcher: false);
        indexer.ScanCompleted += (_, _) => completed = true;

        await indexer.FullScanAsync();

        Assert.True(completed);
    }

    [Fact]
    public async Task IndexChanged_Event_RaisedOnFullScan()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.cs"), "class A { }");

        var changed = false;
        using var indexer = new WorkspaceIndexer(_store, _indexerLogger, _tempDir, enableWatcher: false);
        indexer.IndexChanged += (_, _) => changed = true;

        await indexer.FullScanAsync();

        Assert.True(changed);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}