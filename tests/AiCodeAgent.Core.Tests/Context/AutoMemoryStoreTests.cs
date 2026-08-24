using System.Text.RegularExpressions;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Tests.Context;

public class AutoMemoryStoreTests : IDisposable
{
    private readonly string _tempFile;

    public AutoMemoryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"memory-{Guid.NewGuid():N}.md");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [Fact]
    public async Task CaptureAsync_PersistsLearningToFile()
    {
        var store = new AutoMemoryStore(filePath: _tempFile);
        await store.CaptureAsync("s1", new Learning { Text = "Prefer tabs over spaces" });

        Assert.True(File.Exists(_tempFile));
        var content = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("Prefer tabs over spaces", content);
    }

    [Fact]
    public async Task CaptureAsync_IgnoresDuplicates()
    {
        var store = new AutoMemoryStore(filePath: _tempFile);
        await store.CaptureAsync("s1", new Learning { Text = "Always use var" });
        await store.CaptureAsync("s1", new Learning { Text = "always use var" });

        var content = await store.LoadAsync();
        Assert.Single(content.Split('\n').Where(l => l.Contains("use var")));
    }

    [Fact]
    public async Task CaptureAsync_IgnoresEmptyText()
    {
        var store = new AutoMemoryStore(filePath: _tempFile);
        await store.CaptureAsync("s1", new Learning { Text = "" });

        Assert.False(File.Exists(_tempFile));
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyWhenFileMissing()
    {
        var store = new AutoMemoryStore(filePath: _tempFile);
        var content = await store.LoadAsync();
        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public async Task LoadAsync_BoundsByMaxLines()
    {
        // Write a file with 300 lines
        var lines = new List<string> { "# MEMORY.md", "" };
        for (int i = 0; i < 300; i++)
            lines.Add($"- [2026-01-01] Learning {i}");
        await File.WriteAllTextAsync(_tempFile, string.Join("\n", lines));

        var store = new AutoMemoryStore(filePath: _tempFile);
        var content = await store.LoadAsync();

        var lineCount = content.Split('\n').Length;
        Assert.True(lineCount <= AutoMemoryStore.MaxLines);
    }

    [Fact]
    public async Task LoadAsync_SeedsInMemoryBufferFromFile()
    {
        await File.WriteAllTextAsync(_tempFile, $"# MEMORY.md{Environment.NewLine}- [2026-01-01] Prefer tabs{Environment.NewLine}- [2026-01-02] Always use var");
        var store = new AutoMemoryStore(filePath: _tempFile);

        // First load seeds the buffer
        await store.LoadAsync();

        // Capturing a duplicate should be ignored
        await store.CaptureAsync("s1", new Learning { Text = "prefer tabs" });

        var content = await store.LoadAsync();
        var matchCount = Regex.Matches(content, "Prefer tabs").Count;
        Assert.Equal(1, matchCount);
    }

    [Fact]
    public void Bound_TruncatesByBytes()
    {
        var content = new string('a', AutoMemoryStore.MaxBytes + 1000);
        var bound = AutoMemoryStore.Bound(content);
        Assert.Equal(AutoMemoryStore.MaxBytes, bound.Length);
    }

    [Fact]
    public void Bound_TruncatesByLines()
    {
        var lines = Enumerable.Range(0, 300).Select(_ => "line");
        var content = string.Join("\n", lines);
        var bound = AutoMemoryStore.Bound(content);
        Assert.True(bound.Split('\n').Length <= AutoMemoryStore.MaxLines);
    }

    [Fact]
    public void Bound_HandlesEmpty()
    {
        Assert.Equal("", AutoMemoryStore.Bound(""));
        Assert.Null(AutoMemoryStore.Bound(null!));
    }

    [Fact]
    public void Fingerprint_NormalizesText()
    {
        var a = AutoMemoryStore.Fingerprint("Prefer   TABS");
        var b = AutoMemoryStore.Fingerprint("prefer tabs");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task GetFilePath_ReturnsConfiguredPath()
    {
        var store = new AutoMemoryStore(filePath: _tempFile);
        Assert.Equal(_tempFile, store.GetFilePath());
    }
}