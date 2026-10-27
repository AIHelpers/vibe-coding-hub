using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Context;
using AiCodeAgent.Context.Persistence;
using AiCodeAgent.Core.Models;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.App.Tests.Sessions;

/// <summary>
/// E2-S9: close the app mid-conversation, reopen, click Resume: the same timeline appears and the next message
/// has the previous context (store → timeline → agent context round trip).
/// </summary>
public sealed class SessionResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resume-" + Guid.NewGuid().ToString("N"));
    private const string Project = "C:/proj/app";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    /// <summary>A "process": its own in-memory manager over the shared folder on disk.</summary>
    private PersistentContextManager StartApp() =>
        new(new InMemoryContextManager(NullLogger<InMemoryContextManager>.Instance), new ChatSessionStore(_root), () => Project);

    private static async Task TalkAsync(PersistentContextManager context, string id)
    {
        var call = new ToolCall { Id = "c1", Name = "read_file", Arguments = new() { ["path"] = "Program.cs" } };
        await context.AddMessageAsync(id, new Message { Role = MessageRole.User, Content = "Add a filter to the list" });
        await context.AddMessageAsync(id, new Message { Role = MessageRole.Assistant, Content = "Let me look at the file.", ToolCalls = new() { call } });
        await context.AddMessageAsync(id, new Message { Role = MessageRole.Tool, ToolCallId = "c1", Content = "class Program {}" });
        await context.AddMessageAsync(id, new Message { Role = MessageRole.Assistant, Content = "Added the filter." });
        await context.AddMessageAsync(id, new Message { Role = MessageRole.User, Content = "Now sort it" });
    }

    [AvaloniaFact]
    public async Task Resume_RebuildsTheSameTimeline_AndTheAgentKeepsItsContext()
    {
        var firstRun = StartApp();
        await TalkAsync(firstRun, "chat-1");
        var beforeTimeline = new ChatTimeline();
        beforeTimeline.LoadTranscript(await firstRun.GetContextAsync("chat-1"), keepWelcome: false);

        // --- the app is closed and started again ---
        var secondRun = StartApp();
        var resume = new SessionResumeService(secondRun, secondRun);

        var last = await resume.FindLastAsync();
        Assert.NotNull(last);
        Assert.Equal("chat-1", last!.SessionId);
        Assert.Equal("Add a filter to the list", last.Title);

        var resumed = await resume.ResumeAsync("chat-1", maxTokens: 150_000);
        Assert.Null(resumed.Note);

        // The same timeline: user text, assistant text, the tool card with its result, the answer, the second question.
        var afterTimeline = new ChatTimeline();
        afterTimeline.LoadTranscript(resumed.Transcript, keepWelcome: false);
        Assert.Equal(beforeTimeline.Items.Select(i => i.GetType()), afterTimeline.Items.Select(i => i.GetType()));
        Assert.Equal(new[] { typeof(UserMessageItem), typeof(AssistantTextItem), typeof(ToolCallItem), typeof(AssistantTextItem), typeof(UserMessageItem) },
            afterTimeline.Items.Select(i => i.GetType()));
        Assert.Equal("Now sort it", afterTimeline.Items.OfType<UserMessageItem>().Last().Content);
        Assert.Equal(new int?[] { 1, 2 }, afterTimeline.Items.OfType<UserMessageItem>().Select(u => u.RewindNumber));

        // The agent context: what the model would be sent now includes everything before, and new messages append to it.
        await secondRun.AddMessageAsync("chat-1", new Message { Role = MessageRole.Assistant, Content = "Sorted." });
        var context = await secondRun.GetContextAsync("chat-1");
        Assert.Equal(6, context.Count);
        Assert.Equal("Add a filter to the list", context[0].Content);
        Assert.Equal("c1", context[2].ToolCallId);
        Assert.Equal("Sorted.", context[^1].Content);
    }

    [AvaloniaFact]
    public async Task AfterACrash_ResumeRestoresEverythingUpToTheCrash()
    {
        var firstRun = StartApp();
        await TalkAsync(firstRun, "chat-1");
        // The process died while the next message was being written.
        File.AppendAllText(new ChatSessionStore(_root).FileFor(Project, "chat-1"), "{\"op\":\"msg\",\"message\":{\"role\":2,\"content\":\"Sor");

        var secondRun = StartApp();
        var resumed = await new SessionResumeService(secondRun, secondRun).ResumeAsync("chat-1", 150_000);

        Assert.Equal(5, resumed.Transcript.Count);
        var timeline = new ChatTimeline();
        timeline.LoadTranscript(resumed.Transcript, keepWelcome: false);
        Assert.Equal("Now sort it", timeline.Items.OfType<UserMessageItem>().Last().Content);
    }

    [AvaloniaFact]
    public async Task HistoryOverTheTokenLimit_IsReduced_AndYouAreToldSo()
    {
        var firstRun = StartApp();
        await firstRun.AddMessageAsync("big", new Message { Role = MessageRole.User, Content = "the task" });
        for (var i = 0; i < 30; i++)
            await firstRun.AddMessageAsync("big", new Message { Role = MessageRole.Assistant, Content = new string('y', 800) + i });

        var secondRun = StartApp();
        var resumed = await new SessionResumeService(secondRun, secondRun).ResumeAsync("big", maxTokens: 1_000);

        Assert.NotNull(resumed.Note);
        Assert.True(resumed.Transcript.Count < 31);
        Assert.True(await secondRun.GetTokenCountAsync("big") <= 1_000);
        Assert.Equal("the task", resumed.Transcript[0].Content); // the task itself is pinned
    }

    [Fact]
    public async Task NothingToResume_WhenNoSessionHasMessages()
    {
        var app = StartApp();
        await app.AddMessageAsync("empty", new Message { Role = MessageRole.User, Content = "x" });
        await app.ClearAsync("empty");

        Assert.Null(await new SessionResumeService(app, app).FindLastAsync());
    }

    [Fact]
    public void Banner_ReadsLikeTheStory()
    {
        var info = new AiCodeAgent.Core.Sessions.ChatSessionInfo(
            "s", "Title", new DateTime(2026, 10, 27, 11, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 27, 11, 32, 0, DateTimeKind.Utc), 12, 0m);

        var text = ChatViewModel.FormatResumeOffer(info);

        Assert.StartsWith("Resume last session (12 messages, ", text);
        Assert.EndsWith(LocalTime.FormatShortTime(info.UpdatedAt) + ")", text);
    }
}
