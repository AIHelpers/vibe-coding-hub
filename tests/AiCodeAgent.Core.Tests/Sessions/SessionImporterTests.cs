using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Sessions;

public class SessionImporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IContextManager _contextManager;
    private readonly ICheckpointManager _checkpointManager;
    private readonly SessionImporter _importer;

    public SessionImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _contextManager = Substitute.For<IContextManager>();
        _contextManager.GetContextAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<Message>()));
        _contextManager.AddMessageAsync(Arg.Any<string>(), Arg.Any<Message>())
            .Returns(Task.CompletedTask);

        _checkpointManager = Substitute.For<ICheckpointManager>();
        _checkpointManager.GetCheckpointsForSessionAsync(Arg.Any<string>())
            .Returns(Task.FromResult(new List<CheckpointEntry>()));
        _checkpointManager.ImportCheckpointAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new CheckpointEntry
            {
                CheckpointId = "cp",
                FilePath = "file.cs",
                BackupPath = "backup"
            }));

        var logger = Substitute.For<ILogger<SessionImporter>>();
        _importer = new SessionImporter(_contextManager, _checkpointManager, logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ImportAsync_RehydratesConversation()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();

        var result = await _importer.ImportAsync(bundle, applyCheckpoints: false);

        Assert.Equal(bundle.SessionId, result.SessionId);
        Assert.Equal(bundle.Conversation.Count, result.MessageCount);
        Assert.Equal(bundle.ToolCallLog.Count, result.ToolCallCount);
        // Acceptance count tracked via Received calls
        await _contextManager.Received(bundle.Conversation.Count)
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<Message>());
    }

    [Fact]
    public async Task ImportAsync_NewSessionId_Overrides()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();

        var result = await _importer.ImportAsync(bundle, "new-session", applyCheckpoints: false);

        Assert.Equal("new-session", result.SessionId);
    }

    [Fact]
    public async Task ImportAsync_CheckpointMatch_ImportsWithoutConflict()
    {
        // Write a file matching the checkpoint's original content
        var testFile = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(testFile, "original");

        var bundle = SessionBundleTests.CreateSampleBundle();
        bundle.CheckpointRefs = new List<string> { "cp-1" };
        bundle.CheckpointSnapshots = new List<CheckpointSnapshotDto>
        {
            new()
            {
                CheckpointId = "cp-1",
                FilePath = testFile,
                OriginalContent = "original",
                TurnId = "turn-1"
            }
        };

        var result = await _importer.ImportAsync(bundle, applyCheckpoints: true);

        Assert.Equal(1, result.CheckpointCount);
        Assert.Empty(result.Conflicts);
        await _checkpointManager.Received(1).ImportCheckpointAsync(
            "cp-1", testFile, "original", "turn-1", Arg.Any<string>());
    }

    [Fact]
    public async Task ImportAsync_CheckpointDiverged_ReportsConflict()
    {
        // Write a file that has diverged from the checkpoint's original content
        var testFile = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(testFile, "different-content");

        var bundle = SessionBundleTests.CreateSampleBundle();
        bundle.CheckpointRefs = new List<string> { "cp-1" };
        bundle.CheckpointSnapshots = new List<CheckpointSnapshotDto>
        {
            new()
            {
                CheckpointId = "cp-1",
                FilePath = testFile,
                OriginalContent = "original",
                TurnId = "turn-1"
            }
        };

        var result = await _importer.ImportAsync(bundle, applyCheckpoints: true);

        Assert.Equal(0, result.CheckpointCount);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(ConflictKind.ContentDiverged, conflict.Kind);
        await _checkpointManager.DidNotReceive().ImportCheckpointAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ImportAsync_CheckpointFileMissing_ReportsConflict()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        bundle.CheckpointRefs = new List<string> { "missing-cp" };
        bundle.CheckpointSnapshots = new List<CheckpointSnapshotDto>
        {
            new()
            {
                CheckpointId = "missing-cp",
                FilePath = Path.Combine(_tempDir, "missing.cs"),
                OriginalContent = "original",
                TurnId = "turn-1"
            }
        };

        var result = await _importer.ImportAsync(bundle, applyCheckpoints: true);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(ConflictKind.FileMissing, conflict.Kind);
    }

    [Fact]
    public async Task ImportAsync_CheckpointRefWithoutSnapshot_ReportsMissingRef()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        bundle.CheckpointRefs = new List<string> { "cp-1" };
        bundle.CheckpointSnapshots = new List<CheckpointSnapshotDto>();

        var result = await _importer.ImportAsync(bundle, applyCheckpoints: true);

        Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.CheckpointRefMissing);
    }

    [Fact]
    public async Task ImportAsync_RestoreChangeset_PopulatesSharedChangeset()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        var changeset = new SharedChangeset();

        SessionImporter.RestoreChangeset(bundle, changeset);

        Assert.Equal(bundle.Changeset!.Entries.Count, changeset.Entries.Count);
        Assert.Equal(bundle.Changeset.Hunks.Count, changeset.Hunks.Count);
    }

    [Fact]
    public async Task LoadBundle_JsonFile_Deserializes()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        var path = Path.Combine(_tempDir, "session.agentsession.json");
        await File.WriteAllTextAsync(path, SessionExporter.ToJson(bundle));

        var loaded = await SessionImporter.LoadBundleAsync(path);

        Assert.Equal(bundle.SessionId, loaded.SessionId);
        Assert.Equal(bundle.Conversation.Count, loaded.Conversation.Count);
    }

    [Fact]
    public async Task LoadBundle_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SessionImporter.LoadBundleAsync(Path.Combine(_tempDir, "nope.json")));
    }
}