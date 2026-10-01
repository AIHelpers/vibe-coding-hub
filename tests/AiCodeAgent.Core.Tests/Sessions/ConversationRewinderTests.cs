using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Sessions;

public class ConversationRewinderTests : IDisposable
{
    private sealed class FakeContext : IContextManager
    {
        public readonly Dictionary<string, List<Message>> Store = new();
        public Task<List<Message>> GetContextAsync(string s) => Task.FromResult(Store.TryGetValue(s, out var l) ? l.ToList() : new List<Message>());
        public Task AddMessageAsync(string s, Message m) { (Store.TryGetValue(s, out var l) ? l : Store[s] = new()).Add(m); return Task.CompletedTask; }
        public Task<int> GetTokenCountAsync(string s) => Task.FromResult(0);
        public Task TrimContextAsync(string s, int max) => Task.CompletedTask;
        public Task ClearAsync(string s) { Store.Remove(s); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetSessionsAsync() => Task.FromResult<IReadOnlyList<string>>(Store.Keys.ToList());
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiagent-rewind-" + Guid.NewGuid().ToString("N"));
    private readonly FakeContext _ctx = new();
    private readonly CheckpointManager _checkpoints = new(Substitute.For<ILogger<CheckpointManager>>());
    private readonly ConversationRewinder _rewinder;
    private readonly string _session = "s-" + Guid.NewGuid().ToString("N");

    public ConversationRewinderTests()
    {
        Directory.CreateDirectory(_dir);
        _rewinder = new ConversationRewinder(_ctx, _checkpoints);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static Message Msg(MessageRole role, string text, DateTime at) => new() { Role = role, Content = text, Timestamp = at };

    /// <summary>user1 → assistant → (edit a.txt) → user2 → assistant → (edit a.txt again, create b.txt)</summary>
    private async Task<string> SeedAsync()
    {
        var a = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(a, "v0");
        var t0 = DateTime.UtcNow;

        await _ctx.AddMessageAsync(_session, Msg(MessageRole.User, "first request", t0));
        await _ctx.AddMessageAsync(_session, Msg(MessageRole.Assistant, "did first", t0.AddSeconds(1)));
        await _checkpoints.CreateCheckpointAsync(a, "turn1", _session);   // captures "v0"
        await File.WriteAllTextAsync(a, "v1");
        await Task.Delay(30);

        var t1 = DateTime.UtcNow;
        await _ctx.AddMessageAsync(_session, Msg(MessageRole.User, "second request", t1));
        await _ctx.AddMessageAsync(_session, Msg(MessageRole.Assistant, "did second", t1.AddSeconds(1)));
        await _checkpoints.CreateCheckpointAsync(a, "turn2", _session);   // captures "v1"
        await File.WriteAllTextAsync(a, "v2");
        var b = Path.Combine(_dir, "b.txt");
        await _checkpoints.CreateCheckpointAsync(b, "turn2", _session);   // b did not exist
        await File.WriteAllTextAsync(b, "new");
        return a;
    }

    [Fact]
    public async Task ListPoints_NumbersUserMessages_AndCountsChangedFiles()
    {
        await SeedAsync();
        var points = await _rewinder.ListPointsAsync(_session);

        Assert.Equal(2, points.Count);
        Assert.Equal("first request", points[0].Preview);
        Assert.Equal(2, points[0].FilesChanged);   // a.txt and b.txt changed after message 1
        Assert.Equal(2, points[1].FilesChanged);
        Assert.Equal(2, points[1].MessageIndex);
    }

    [Fact]
    public async Task Rewind_Both_RestoresFilesAndDropsMessages()
    {
        var a = await SeedAsync();

        var result = await _rewinder.RewindAsync(_session, 2, RewindMode.Both);

        Assert.True(result.Success, result.Message);
        Assert.Equal("second request", result.RestoredUserText);
        Assert.Equal(2, result.MessagesRemoved);
        Assert.Equal("v1", File.ReadAllText(a));                    // state before message 2
        Assert.False(File.Exists(Path.Combine(_dir, "b.txt")));     // file created after message 2 is removed
        Assert.Equal(2, (await _ctx.GetContextAsync(_session)).Count);
    }

    [Fact]
    public async Task Rewind_ToFirstMessage_RestoresOriginalContent()
    {
        var a = await SeedAsync();

        await _rewinder.RewindAsync(_session, 1, RewindMode.Both);

        Assert.Equal("v0", File.ReadAllText(a));                    // earliest checkpoint wins
        Assert.Empty(await _ctx.GetContextAsync(_session));
    }

    [Fact]
    public async Task Rewind_ConversationOnly_LeavesFilesAlone()
    {
        var a = await SeedAsync();

        await _rewinder.RewindAsync(_session, 2, RewindMode.Conversation);

        Assert.Equal("v2", File.ReadAllText(a));
        Assert.Equal(2, (await _ctx.GetContextAsync(_session)).Count);
    }

    [Fact]
    public async Task Rewind_CodeOnly_LeavesConversationAlone()
    {
        var a = await SeedAsync();

        var result = await _rewinder.RewindAsync(_session, 2, RewindMode.Code);

        Assert.Equal("v1", File.ReadAllText(a));
        Assert.Equal(4, (await _ctx.GetContextAsync(_session)).Count);
        Assert.Null(result.RestoredUserText);
    }

    [Fact]
    public async Task Rewind_UnknownNumber_FailsWithoutChanges()
    {
        var a = await SeedAsync();
        var result = await _rewinder.RewindAsync(_session, 9, RewindMode.Both);
        Assert.False(result.Success);
        Assert.Equal("v2", File.ReadAllText(a));
        Assert.Equal(4, (await _ctx.GetContextAsync(_session)).Count);
    }

    [Fact]
    public async Task Fork_CopiesHistoryBeforeMessage_AndLeavesOriginalAndFilesUntouched()
    {
        var a = await SeedAsync();

        var (newId, message) = await _rewinder.ForkAsync(_session, 2);

        Assert.NotNull(newId);
        Assert.NotEqual(_session, newId);
        Assert.Equal(2, (await _ctx.GetContextAsync(newId!)).Count);
        Assert.Equal(4, (await _ctx.GetContextAsync(_session)).Count);
        Assert.Equal("v2", File.ReadAllText(a));
        Assert.Contains("Files were not changed", message);
    }

    [Fact]
    public async Task GetTranscript_ReflectsRewind_ForRebuildingTheChatView()
    {
        await SeedAsync();
        Assert.Equal(4, (await _rewinder.GetTranscriptAsync(_session)).Count);

        var result = await _rewinder.RewindAsync(_session, 2, RewindMode.Conversation);

        Assert.True(result.Success);
        var transcript = await _rewinder.GetTranscriptAsync(_session);
        Assert.Equal(2, transcript.Count);
        Assert.Equal("did first", transcript[^1].Content);
        Assert.Equal("second request", result.RestoredUserText);
    }
}
