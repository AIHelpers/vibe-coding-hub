using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

public class CheckpointManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CheckpointManager _manager;

    public CheckpointManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-cp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manager = new CheckpointManager(Substitute.For<ILogger<CheckpointManager>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateCheckpointAsync_SavesOriginalContent()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "original");

        var entry = await _manager.CreateCheckpointAsync(filePath, "turn1", "session1");

        Assert.Equal("original", entry.OriginalContent);
        Assert.Equal("turn1", entry.TurnId);
        Assert.Equal("session1", entry.SessionId);
        Assert.True(File.Exists(entry.BackupPath));
    }

    [Fact]
    public async Task RestoreAsync_RestoresOriginalContent()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "original");

        var entry = await _manager.CreateCheckpointAsync(filePath, "turn1", "session1");

        // Modify the file after the checkpoint.
        await File.WriteAllTextAsync(filePath, "modified");

        var restored = await _manager.RestoreAsync("session1", entry.CheckpointId);

        Assert.True(restored);
        Assert.Equal("original", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task RestoreAsync_RejectsWrongSession()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "original");

        var entry = await _manager.CreateCheckpointAsync(filePath, "turn1", "session1");

        var restored = await _manager.RestoreAsync("other-session", entry.CheckpointId);

        Assert.False(restored);
    }

    [Fact]
    public async Task ListAsync_ReturnsCheckpointsForSessionSortedNewestFirst()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "v1");

        await _manager.CreateCheckpointAsync(filePath, "turn1", "session1");
        await Task.Delay(20);
        await _manager.CreateCheckpointAsync(filePath, "turn2", "session1");
        await Task.Delay(20);
        var otherPath = Path.Combine(_tempDir, "other.txt");
        await File.WriteAllTextAsync(otherPath, "other");
        await _manager.CreateCheckpointAsync(otherPath, "turn3", "session-other");

        var list = await _manager.ListAsync("session1");

        Assert.Equal(2, list.Count);
        Assert.Equal("turn2", list[0].TurnId);
        Assert.Equal("turn1", list[1].TurnId);
    }

    [Fact]
    public async Task PruneAsync_KeepsOnlyMostRecent()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "content");

        var c1 = await _manager.CreateCheckpointAsync(filePath, "turn1", "session1");
        await Task.Delay(20);
        var c2 = await _manager.CreateCheckpointAsync(filePath, "turn2", "session1");
        await Task.Delay(20);
        var c3 = await _manager.CreateCheckpointAsync(filePath, "turn3", "session1");

        var removed = await _manager.PruneAsync("session1", 1);

        Assert.Equal(2, removed);
        var remaining = await _manager.ListAsync("session1");
        Assert.Single(remaining);
        Assert.Equal(c3.CheckpointId, remaining[0].CheckpointId);
        Assert.False(File.Exists(c1.BackupPath));
        Assert.False(File.Exists(c2.BackupPath));
        Assert.True(File.Exists(c3.BackupPath));
    }

    [Fact]
    public async Task RestoreAsync_SkipsSymlinkedFiles()
    {
        // Create a real file, then a symlink pointing to it.
        var target = Path.Combine(_tempDir, "target.txt");
        await File.WriteAllTextAsync(target, "target-content");
        var link = Path.Combine(_tempDir, "link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            // No privilege to create symlinks (common on Windows without dev mode); skip.
            return;
        }

        var entry = await _manager.ImportCheckpointAsync(
            "cp-sym", link, "original", "turn1", "session1");

        var restored = await _manager.RestoreAsync("session1", entry.CheckpointId);

        Assert.False(restored);
        // The symlink target must not have been overwritten.
        Assert.Equal("target-content", await File.ReadAllTextAsync(target));
    }
}