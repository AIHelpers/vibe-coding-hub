using AiCodeAgent.Core.Sessions;

namespace AiCodeAgent.Core.Tests.Sessions;

public class SessionPersistenceManagerTests
{
    [Fact]
    public async Task AppendAsync_WritesToActiveSession()
    {
        var store = new JsonlSessionStore(CreateTempDir());
        var manager = new SessionPersistenceManager(store);

        await manager.AppendAsync(new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Payload = new() { ["content"] = "ping" }
        });

        var (id, history) = await manager.ResumeAsync(manager.ActiveSessionId);
        Assert.Equal(manager.ActiveSessionId, id);
        Assert.Single(history);
        Assert.Equal("ping", history[0].Payload["content"]?.ToString());
    }

    [Fact]
    public async Task ResumeAsync_LoadsExistingHistory()
    {
        var store = new JsonlSessionStore(CreateTempDir());
        var manager = new SessionPersistenceManager(store);

        await manager.AppendAsync(new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Payload = new() { ["content"] = "first" }
        });
        await manager.AppendAsync(new SessionEntry
        {
            Type = SessionEntryTypes.Assistant,
            Payload = new() { ["content"] = "second" }
        });

        var (_, history) = await manager.ResumeAsync(manager.ActiveSessionId);
        Assert.Equal(2, history.Count);
        Assert.Equal("first", history[0].Payload["content"]?.ToString());
        Assert.Equal("second", history[1].Payload["content"]?.ToString());
    }

    [Fact]
    public async Task ForkAsync_CopiesHistoryToNewId_AndLeavesOriginal()
    {
        var store = new JsonlSessionStore(CreateTempDir());
        var manager = new SessionPersistenceManager(store);

        await manager.AppendAsync(new SessionEntry
        {
            Type = SessionEntryTypes.User,
            Payload = new() { ["content"] = "original" }
        });
        var originalId = manager.ActiveSessionId;

        var newId = await manager.ForkAsync(originalId);

        Assert.NotEqual(originalId, newId);
        Assert.Equal(newId, manager.ActiveSessionId);

        // New session has copied history
        var (_, newHistory) = await manager.ResumeAsync(newId);
        Assert.Single(newHistory);
        Assert.Equal("original", newHistory[0].Payload["content"]?.ToString());

        // Original session is unchanged
        var (_, oldHistory) = await manager.ResumeAsync(originalId);
        Assert.Single(oldHistory);
        Assert.Equal("original", oldHistory[0].Payload["content"]?.ToString());
    }

    [Fact]
    public void CreateNew_ChangesActiveSessionId()
    {
        var store = new JsonlSessionStore(CreateTempDir());
        var manager = new SessionPersistenceManager(store);
        var first = manager.ActiveSessionId;

        var second = manager.CreateNew();

        Assert.NotEqual(first, second);
        Assert.Equal(second, manager.ActiveSessionId);
    }

    [Fact]
    public async Task ResumeAsync_WithEmptySession_ReturnsEmptyHistory()
    {
        var store = new JsonlSessionStore(CreateTempDir());
        var manager = new SessionPersistenceManager(store);

        var (id, history) = await manager.ResumeAsync("never-existed");
        Assert.Equal("never-existed", id);
        Assert.Empty(history);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiagent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}