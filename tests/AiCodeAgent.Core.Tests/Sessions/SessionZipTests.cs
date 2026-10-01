using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Sessions;

public class SessionZipTests : IDisposable
{
    private readonly string _tempDir;

    public SessionZipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ExportZip_RoundTrips_SnapshotsAndMetadata()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        var zipPath = Path.Combine(_tempDir, "session.agentsession");

        await SessionExporter.ExportAsync(bundle, zipPath);

        Assert.True(File.Exists(zipPath));
        Assert.True(new FileInfo(zipPath).Length > 0);

        // Load it back
        var loaded = await SessionImporter.LoadBundleAsync(zipPath);

        Assert.Equal(bundle.SessionId, loaded.SessionId);
        Assert.Equal(bundle.Conversation.Count, loaded.Conversation.Count);
        Assert.Equal(bundle.ToolCallLog.Count, loaded.ToolCallLog.Count);

        // Checkpoint snapshots should be rehydrated from embedded zip entries
        Assert.Equal(bundle.CheckpointSnapshots.Count, loaded.CheckpointSnapshots.Count);
        var snapshot = loaded.CheckpointSnapshots.First(s => s.CheckpointId == "cp-1");
        Assert.Equal("original", snapshot.OriginalContent);
        Assert.Equal(bundle.CheckpointSnapshots[0].FilePath, snapshot.FilePath);
    }

    [Fact]
    public async Task ExportJson_Zip_AreBothSupported()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();

        // JSON path
        var jsonPath = Path.Combine(_tempDir, "session.json");
        await SessionExporter.ExportAsync(bundle, jsonPath);
        Assert.True(File.Exists(jsonPath));

        // Zip path
        var zipPath = Path.Combine(_tempDir, "session.agentsession");
        await SessionExporter.ExportAsync(bundle, zipPath);
        Assert.True(File.Exists(zipPath));

        // Both load
        var fromJson = await SessionImporter.LoadBundleAsync(jsonPath);
        var fromZip = await SessionImporter.LoadBundleAsync(zipPath);
        Assert.Equal(bundle.SessionId, fromJson.SessionId);
        Assert.Equal(bundle.SessionId, fromZip.SessionId);
    }

    [Fact]
    public async Task ExportZip_CreatesOutputDirectory()
    {
        var bundle = SessionBundleTests.CreateSampleBundle();
        var nestedPath = Path.Combine(_tempDir, "nested", "dir", "session.agentsession");

        await SessionExporter.ExportAsync(bundle, nestedPath);

        Assert.True(File.Exists(nestedPath));
    }
}

public class SessionRecorderTests
{
    [Fact]
    public async Task Start_Stop_RecordsToolCalls_WithAgentAttribution()
    {
        var bus = new AgentEventBus();
        var logger = Substitute.For<ILogger<SessionRecorder>>();
        var recorder = new SessionRecorder(bus, logger);

        await recorder.StartAsync("session-1");

        var call = new ToolCall { Id = "call-1", Name = "ReadFile" };
        bus.Publish(new AgentTaggedEvent(
            new ToolCallStartEvent(call), "planner-1", "planner"));
        bus.Publish(new AgentTaggedEvent(
            new ToolCallEndEvent(call, new ToolResult { Content = "ok" }, TimeSpan.FromMilliseconds(10)),
            "planner-1", "planner"));

        // Deterministically wait until the recorder has drained the events
        await WaitForAsync(() => recorder.GetSnapshot().ToolCallLog.Count == 1);

        var bundle = recorder.GetSnapshot();

        var entry = Assert.Single(bundle.ToolCallLog);
        Assert.Equal("ReadFile", entry.ToolName);
        Assert.Equal("ok", entry.Output);
        Assert.Equal("planner-1", entry.AgentId);
        Assert.Equal("planner", entry.Role);
        Assert.Contains("planner", bundle.RolesUsed);
    }

    [Fact]
    public async Task Start_Stop_RecordsDiffsAndCheckpoints()
    {
        var bus = new AgentEventBus();
        var logger = Substitute.For<ILogger<SessionRecorder>>();
        var recorder = new SessionRecorder(bus, logger);

        await recorder.StartAsync("session-1");

        bus.Publish(new DiffProducedEvent(new DiffEntry
        {
            FilePath = "test.cs",
            DiffText = "-old\n+new",
            AgentId = "implementer-1"
        }));
        bus.Publish(new CheckpointCreatedEvent(new CheckpointEntry
        {
            CheckpointId = "cp-1",
            FilePath = "test.cs",
            OriginalContent = "original",
            TurnId = "turn-1",
            SessionId = "session-1"
        }));

        await WaitForAsync(() =>
            recorder.GetSnapshot().Changeset?.Entries.Count == 1 &&
            recorder.GetSnapshot().CheckpointRefs.Count == 1);

        var bundle = recorder.GetSnapshot();

        Assert.Single(bundle.Changeset!.Entries);
        Assert.Contains(bundle.Changeset.Hunks, h => h.FilePath == "test.cs");
        Assert.Contains("cp-1", bundle.CheckpointRefs);
        Assert.Single(bundle.CheckpointSnapshots);
    }

    [Fact]
    public async Task GetSnapshot_DoesNotMutateLiveBundle()
    {
        var bus = new AgentEventBus();
        var logger = Substitute.For<ILogger<SessionRecorder>>();
        var recorder = new SessionRecorder(bus, logger);

        await recorder.StartAsync("session-1");
        bus.Publish(new DiffProducedEvent(new DiffEntry
        {
            FilePath = "test.cs",
            DiffText = "-old\n+new"
        }));

        await WaitForAsync(() => recorder.GetSnapshot().Changeset?.Entries.Count == 1);
        var snapshot = recorder.GetSnapshot();
        var snapshotCountAfter = snapshot.Changeset!.Entries.Count;

        // Record another event — the snapshot should be unaffected
        bus.Publish(new DiffProducedEvent(new DiffEntry
        {
            FilePath = "other.cs",
            DiffText = "-a\n+b"
        }));
        await WaitForAsync(() => recorder.Bundle.Changeset?.Entries.Count == 2);

        // Snapshot still has 1 entry; live bundle has 2
        Assert.Equal(snapshotCountAfter, snapshot.Changeset.Entries.Count);
        Assert.Equal(2, recorder.Bundle.Changeset!.Entries.Count);
    }

    /// <summary>Poll until the predicate is true (with a timeout).</summary>
    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for recorder condition.");
            await Task.Delay(10);
        }
    }
}