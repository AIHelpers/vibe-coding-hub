using System.Text.Json;
using AiCodeAgent.Context;
using AiCodeAgent.Context.Persistence;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Context.Tests;

/// <summary>E2-S9: chat history survives a restart, a crash mid-write, a rewind and a trim.</summary>
public sealed class PersistentContextManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctx-persist-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "project-a");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private PersistentContextManager NewManager(string? project = null) =>
        new(new InMemoryContextManager(NullLogger<InMemoryContextManager>.Instance), new ChatSessionStore(_root), () => project ?? _project);

    private static Message User(string text) => new() { Role = MessageRole.User, Content = text };
    private static Message Assistant(string text, params ToolCall[] calls) =>
        new() { Role = MessageRole.Assistant, Content = text, ToolCalls = calls.Length == 0 ? null : calls.ToList() };
    private static Message ToolResult(string callId, string text) => new() { Role = MessageRole.Tool, ToolCallId = callId, Content = text };

    private string SessionFile(string id) => new ChatSessionStore(_root).FileFor(_project, id);

    [Fact]
    public async Task Messages_SurviveARestart_InOrder_WithToolCalls()
    {
        var first = NewManager();
        var call = new ToolCall { Id = "call-1", Name = "read_file", Arguments = new() { ["path"] = "a.cs" } };
        await first.AddMessageAsync("s1", User("Add a filter"));
        await first.AddMessageAsync("s1", Assistant("Reading first.", call));
        await first.AddMessageAsync("s1", ToolResult("call-1", "class A {}"));
        await first.AddMessageAsync("s1", Assistant("Done."));

        var afterRestart = NewManager(); // a new process: nothing in memory
        var messages = await afterRestart.GetContextAsync("s1");

        Assert.Equal(new[] { MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant }, messages.Select(m => m.Role));
        Assert.Equal(new[] { "Add a filter", "Reading first.", "class A {}", "Done." }, messages.Select(m => m.Content));
        Assert.Equal("call-1", messages[2].ToolCallId);
        var restoredCall = Assert.Single(messages[1].ToolCalls!);
        Assert.Equal("call-1", restoredCall.Id);
        Assert.Equal("read_file", restoredCall.Name);
        Assert.Equal("a.cs", restoredCall.Arguments["path"]?.ToString());
        Assert.True(await afterRestart.GetTokenCountAsync("s1") > 0);
    }

    [Fact]
    public async Task NextMessageAfterRestart_ContinuesTheOldConversation()
    {
        var first = NewManager();
        await first.AddMessageAsync("s1", User("one"));
        await first.AddMessageAsync("s1", Assistant("two"));

        var second = NewManager();
        await second.AddMessageAsync("s1", User("three"));

        var third = NewManager();
        Assert.Equal(new[] { "one", "two", "three" }, (await third.GetContextAsync("s1")).Select(m => m.Content));
    }

    [Fact]
    public async Task HalfWrittenLastLine_IsSkipped_AndLaterWritesStillWork()
    {
        var first = NewManager();
        await first.AddMessageAsync("s1", User("before the crash"));
        await first.AddMessageAsync("s1", Assistant("also before"));
        File.AppendAllText(SessionFile("s1"), "{\"op\":\"msg\",\"message\":{\"role\":1,\"content\":\"cut off mid-wri"); // crash: no newline, invalid JSON

        var afterCrash = NewManager();
        Assert.Equal(new[] { "before the crash", "also before" }, (await afterCrash.GetContextAsync("s1")).Select(m => m.Content));

        await afterCrash.AddMessageAsync("s1", User("after the crash"));
        var later = NewManager();
        Assert.Equal(new[] { "before the crash", "also before", "after the crash" }, (await later.GetContextAsync("s1")).Select(m => m.Content));
    }

    [Fact]
    public async Task ClearThenReAdd_LikeARewind_PersistsOnlyTheKeptMessages_AndKeepsTheTitle()
    {
        var m = NewManager();
        await m.AddMessageAsync("s1", User("keep"));
        await m.AddMessageAsync("s1", Assistant("keep too"));
        await m.AddMessageAsync("s1", User("undo me"));
        await m.RenameAsync("s1", "My title");

        var all = await m.GetContextAsync("s1");
        await m.ClearAsync("s1");
        foreach (var message in all.Take(2)) await m.AddMessageAsync("s1", message);

        var restarted = NewManager();
        Assert.Equal(new[] { "keep", "keep too" }, (await restarted.GetContextAsync("s1")).Select(x => x.Content));
        Assert.Equal("My title", (await restarted.ListAsync()).Single().Title);
    }

    [Fact]
    public async Task Trim_IsPersisted()
    {
        var m = NewManager();
        await m.AddMessageAsync("s1", User("task"));
        for (var i = 0; i < 20; i++) await m.AddMessageAsync("s1", Assistant(new string('x', 400) + i));

        await m.TrimContextAsync("s1", 300);
        var inMemory = (await m.GetContextAsync("s1")).Count;
        Assert.True(inMemory < 21);

        Assert.Equal(inMemory, (await NewManager().GetContextAsync("s1")).Count);
    }

    [Fact]
    public async Task Catalog_ListsNewestFirst_WithTitleFromTheFirstUserMessage_AndCounts()
    {
        var m = NewManager();
        await m.AddMessageAsync("old", User("Fix the login bug\nwith details"));
        await m.AddMessageAsync("old", Assistant("Looking."));
        await Task.Delay(30);
        await m.AddMessageAsync("new", User("Write the release notes"));
        await m.AddMessageAsync("new", Assistant("Here they are."));
        await m.AddMessageAsync("new", ToolResult("x", "tool output does not count"));

        var list = await NewManager().ListAsync();

        Assert.Equal(new[] { "new", "old" }, list.Select(i => i.SessionId));
        Assert.Equal("Write the release notes", list[0].Title);
        Assert.Equal("Fix the login bug", list[1].Title);
        Assert.Equal(2, list[0].MessageCount);
        Assert.Equal("new", (await NewManager().GetLatestAsync())!.SessionId);
    }

    [Fact]
    public async Task Catalog_OnlyListsSessionsOfTheCurrentWorkingDirectory_AndHidesEmptyOnes()
    {
        var a = NewManager("C:/proj/a");
        var b = NewManager("C:/proj/b");
        await a.AddMessageAsync("in-a", User("hello a"));
        await b.AddMessageAsync("in-b", User("hello b"));
        await a.AddMessageAsync("emptied", User("temp"));
        await a.ClearAsync("emptied");

        Assert.Equal(new[] { "in-a" }, (await NewManager("C:/proj/a").ListAsync()).Select(i => i.SessionId));
        Assert.Equal(new[] { "in-b" }, (await NewManager("C:/proj/b").ListAsync()).Select(i => i.SessionId));
    }

    [Fact]
    public async Task Rename_Cost_AndDelete()
    {
        var m = NewManager();
        await m.AddMessageAsync("s1", User("hello"));
        await m.RenameAsync("s1", "  Nicer name ");
        await m.RecordCostAsync("s1", 0.42m);

        var info = Assert.Single(await NewManager().ListAsync());
        Assert.Equal("Nicer name", info.Title);
        Assert.Equal(0.42m, info.CostUsd);

        await m.DeleteAsync("s1");
        Assert.Empty(await NewManager().ListAsync());
        Assert.Empty(await NewManager().GetContextAsync("s1"));
    }

    [Fact]
    public async Task AgentSubSessionIds_StayInMemoryOnly()
    {
        var m = NewManager();
        await m.AddMessageAsync("s1/coder", User("sub task"));

        Assert.Single(await m.GetContextAsync("s1/coder"));
        Assert.Empty(await NewManager().GetContextAsync("s1/coder"));
        Assert.False(ChatSessionStore.IsPersistableId("s1/coder"));
        Assert.False(ChatSessionStore.IsPersistableId("../escape"));
        Assert.True(ChatSessionStore.IsPersistableId("0a1b2c3d4e5f"));
    }

    [Fact]
    public async Task ListAsync_WorksWhileTheSessionFileIsStillBeingWritten()
    {
        var m = NewManager();
        await m.AddMessageAsync("s1", User("hello"));
        using var open = new FileStream(SessionFile("s1"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Single(await NewManager().ListAsync());
    }
}
