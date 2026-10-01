using AiCodeAgent.Core.Sessions;

namespace AiCodeAgent.Core.Tests.Sessions;

public class JsonlSessionStoreTests
{
    [Fact]
    public async Task AppendAndRead_RoundTrips_Entries()
    {
        var root = CreateTempDir();
        var store = new JsonlSessionStore(root);

        var sid = "test-session-1";
        await store.AppendAsync(sid, new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Payload = new() { ["content"] = "Hello" }
        });
        await store.AppendAsync(sid, new SessionEntry
        {
            Type = SessionEntryTypes.Assistant,
            Timestamp = new DateTime(2024, 1, 1, 12, 0, 1, DateTimeKind.Utc),
            Payload = new() { ["content"] = "Hi there!" }
        });

        var entries = new List<SessionEntry>();
        await foreach (var e in store.ReadAsync(sid))
            entries.Add(e);

        Assert.Equal(2, entries.Count);
        Assert.Equal(SessionEntryTypes.User, entries[0].Type);
        Assert.Equal("Hello", entries[0].Payload["content"]?.ToString());
        Assert.Equal(SessionEntryTypes.Assistant, entries[1].Type);
        Assert.Equal("Hi there!", entries[1].Payload["content"]?.ToString());
    }

    [Fact]
    public async Task ReadAsync_MissingSession_ReturnsEmpty()
    {
        var root = CreateTempDir();
        var store = new JsonlSessionStore(root);

        var entries = new List<SessionEntry>();
        await foreach (var e in store.ReadAsync("nonexistent"))
            entries.Add(e);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task ListAsync_ReturnsMetadataSortedByUpdatedAt()
    {
        var root = CreateTempDir();
        var store = new JsonlSessionStore(root);

        var oldTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var newTime = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        await store.AppendAsync("session-old", new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Timestamp = oldTime,
            Payload = new() { ["content"] = "old" }
        });
        await store.AppendAsync("session-new", new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Timestamp = newTime,
            Payload = new() { ["content"] = "new" }
        });

        var list = await store.ListAsync(Environment.CurrentDirectory);

        Assert.Equal(2, list.Count);
        // Sorted by UpdatedAt descending — newest first
        Assert.Equal("session-new", list[0].SessionId);
        Assert.Equal("session-old", list[1].SessionId);
        Assert.Equal(1, list[0].EntryCount);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var root = CreateTempDir();
        var store = new JsonlSessionStore(root);

        await store.AppendAsync("to-delete", new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Payload = new() { ["content"] = "bye" }
        });

        await store.DeleteAsync("to-delete");

        var entries = new List<SessionEntry>();
        await foreach (var e in store.ReadAsync("to-delete"))
            entries.Add(e);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task AppendAsync_BlankSessionId_Throws()
    {
        var root = CreateTempDir();
        var store = new JsonlSessionStore(root);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync("", new SessionEntry { Type = "x" }));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiagent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}