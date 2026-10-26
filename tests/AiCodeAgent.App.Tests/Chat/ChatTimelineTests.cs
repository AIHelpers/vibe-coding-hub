using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using NSubstitute;

namespace AiCodeAgent.App.Tests.Chat;

/// <summary>E2-S3: event sequence → timeline item sequence.</summary>
public class ChatTimelineTests
{
    private static ToolCall Call(string name, string key, string value) =>
        new() { Name = name, Arguments = new Dictionary<string, object?> { [key] = value } };

    private static ToolResult Ok(string content = "ok") => new() { Content = content };

    /// <summary>A compact description of the timeline, e.g. "user:Fix it", "text:Reading…", "tool:read_file", "approval:edit_file".</summary>
    private static List<string> Shape(ChatTimeline t) => t.Items.Select(i => i switch
    {
        UserMessageItem u => $"user:{u.Content}",
        AssistantTextItem a => $"text:{a.Content}",
        SystemNoticeItem n => $"notice:{n.Content}",
        ToolCallItem c => $"tool:{c.Card.ToolName}",
        ApprovalItem p => $"approval:{p.ToolName}",
        _ => i.GetType().Name
    }).ToList();

    [Fact]
    public void ScriptedRun_ReadEditVerifyAnswer_MatchesEventOrderExactly()
    {
        var t = new ChatTimeline();
        var read = Call("read_file", "path", "src/Todo.Core/TodoService.cs");
        var edit = Call("edit_file", "path", "src/Todo.Core/TodoService.cs");
        var verify = Call("verify_changes", "scope", "all");

        t.AddUser("Add an Overdue filter");
        var run = t.BeginRun();
        run.AppendText("I'll read the service ");
        run.AppendText("first.");
        t.ToolStarted(read);
        t.ToolEnded(read, Ok("class TodoService {}"), TimeSpan.FromSeconds(0.1));
        run.AppendText("Overdue keeps the predicate:");
        var approval = t.AddApproval(edit, "Write");
        approval.Resolve(true);
        t.ToolStarted(edit);
        t.ToolEnded(edit, Ok(), TimeSpan.FromSeconds(0.2));
        run.AppendText("Running the build…");
        t.ToolStarted(verify);
        t.ToolEnded(verify, Ok("Build succeeded. 12 passed."), TimeSpan.FromSeconds(11.8));
        run.AppendText("Done: Overdue filter added and tests pass.");
        run.Complete();

        Assert.Equal(new[]
        {
            "user:Add an Overdue filter",
            "text:I'll read the service first.",
            "tool:read_file",
            "text:Overdue keeps the predicate:",
            "approval:edit_file",
            "tool:edit_file",
            "text:Running the build…",
            "tool:verify_changes",
            "text:Done: Overdue filter added and tests pass."
        }, Shape(t));

        Assert.Equal(4, run.TextItems.Count);
        Assert.All(t.Items.OfType<AssistantTextItem>(), b => Assert.False(b.IsStreaming));
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
    }

    [Fact]
    public void TextFromDifferentSteps_IsNeverConcatenated()
    {
        var t = new ChatTimeline();
        var run = t.BeginRun();
        run.AppendText("Reading first.");
        var c = Call("read_file", "path", "a.cs");
        t.ToolStarted(c);
        t.ToolEnded(c, Ok(), TimeSpan.Zero);
        run.AppendText("Overdue keeps…");

        var texts = t.Items.OfType<AssistantTextItem>().Select(a => a.Content).ToList();
        Assert.Equal(new[] { "Reading first.", "Overdue keeps…" }, texts);
        Assert.DoesNotContain(texts, s => s.Contains("first.Overdue", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolResult_UpdatesTheExistingCard_InPlace()
    {
        var t = new ChatTimeline();
        var c = Call("read_file", "path", "a.cs");
        t.ToolStarted(c);
        t.ToolEnded(c, Ok("body"), TimeSpan.FromSeconds(1));

        var item = Assert.Single(t.Items.OfType<ToolCallItem>());
        Assert.Equal("body", item.Card.Output);
        Assert.False(item.Card.IsRunning);
        Assert.Single(t.ToolCards);
    }

    [Fact]
    public void LeadingNewlinesOfANewBubble_AreTrimmed_AndBlankDeltasAddNothing()
    {
        var t = new ChatTimeline();
        var run = t.BeginRun();
        run.AppendText("\n\n");
        Assert.Empty(t.Items);

        run.AppendText("\n\nHello");
        Assert.Equal("Hello", Assert.IsType<AssistantTextItem>(Assert.Single(t.Items)).Content);
    }

    [Fact]
    public void Notes_AppendToTheOpenBubble_OrStartANewOneAfterATool()
    {
        var t = new ChatTimeline();
        var run = t.BeginRun();
        run.AppendText("Working");
        run.AppendNote("\n\n*Cancelled*");
        Assert.Equal("Working\n\n*Cancelled*", ((AssistantTextItem)t.Items[^1]).Content);

        var run2 = t.BeginRun();
        var c = Call("execute_command", "command", "dotnet build");
        t.ToolStarted(c);
        run2.AppendNote("\n\n**Error:** boom");
        Assert.Equal("**Error:** boom", ((AssistantTextItem)t.Items[^1]).Content);
    }

    [Fact]
    public void ARunWithoutText_GetsNoResponseBubble_AndARunWithText_DoesNot()
    {
        var t = new ChatTimeline();
        var silent = t.BeginRun();
        var c = Call("read_file", "path", "a.cs");
        t.ToolStarted(c);
        t.ToolEnded(c, Ok(), TimeSpan.Zero);
        silent.Complete();
        Assert.Equal($"text:{ChatTimeline.NoResponseText}", Shape(t)[^1]);

        var talking = t.BeginRun();
        talking.AppendText("Answer.");
        var before = t.Items.Count;
        talking.Complete();
        Assert.Equal(before, t.Items.Count);
    }

    [Fact]
    public void ASecondRun_NeverContinuesTheFirstRunsBubble()
    {
        var t = new ChatTimeline();
        var first = t.BeginRun();
        first.AppendText("one");
        var second = t.BeginRun();
        second.AppendText("two");

        Assert.Equal(new[] { "text:one", "text:two" }, Shape(t));
    }

    [Fact]
    public void MultiAgentText_GetsOneBubblePerAgent()
    {
        var t = new ChatTimeline();
        var run = t.BeginRun();
        run.AppendText("plan ", "a1", "planner");
        run.AppendText("ready", "a1", "planner");
        run.AppendText("coding", "a2", "coder");

        var bubbles = t.Items.OfType<AssistantTextItem>().ToList();
        Assert.Equal(2, bubbles.Count);
        Assert.Equal(("planner", "plan ready"), (bubbles[0].Label, bubbles[0].Content));
        Assert.Equal(("coder", "coding"), (bubbles[1].Label, bubbles[1].Content));
    }

    [Fact]
    public void AddChatMessage_MapsRolesToItemTypes()
    {
        var t = new ChatTimeline();
        t.Add(new ChatMessage { Role = "User", Content = "u" });
        t.Add(new ChatMessage { Role = "Assistant", Content = "a" });
        t.Add(new ChatMessage { Role = "System", Content = "s" });

        Assert.Equal(new[] { "user:u", "text:a", "notice:s" }, Shape(t));
        Assert.False(((AssistantTextItem)t.Items[1]).IsStreaming);
    }

    [Fact]
    public void ANoteAddedMidRun_ClosesTheBubble_SoLaterTextStartsANewOne()
    {
        var t = new ChatTimeline();
        var run = t.BeginRun();
        run.AppendText("before");
        t.AddNotice("Retrieved context");
        run.AppendText("after");

        Assert.Equal(new[] { "text:before", "notice:Retrieved context", "text:after" }, Shape(t));
    }

    [Fact]
    public void Approval_StatusFollowsTheAnswer_FirstAnswerWins()
    {
        var item = new ChatTimeline().AddApproval(Call("execute_command", "command", "rm -rf bin"), "Execute");
        Assert.True(item.IsPending);
        Assert.Equal("rm -rf bin", item.Subtitle);
        Assert.Contains("Run command", item.Title);

        item.Resolve(false);
        item.Resolve(true);
        Assert.True(item.IsDeclined);
        Assert.Equal("✕ Declined", item.StatusText);
    }

    [Fact]
    public void Clear_KeepsOnlyTheWelcomeWhenAsked()
    {
        var t = new ChatTimeline();
        t.Add(new ChatMessage { Role = "Assistant", Content = "Welcome" });
        t.AddUser("hi");
        t.ToolStarted(Call("read_file", "path", "a.cs"));

        t.Clear(keepWelcome: true);
        Assert.Equal(new[] { "text:Welcome" }, Shape(t));
        Assert.Empty(t.ToolCards);

        t.Clear();
        Assert.Empty(t.Items);
    }

    // ── Transcript (JSONL session store) → timeline, rewind numbering ──────────

    private static List<Message> Transcript()
    {
        var read = new ToolCall { Id = "c1", Name = "read_file", Arguments = new() { ["path"] = "a.cs" } };
        var edit = new ToolCall { Id = "c2", Name = "edit_file", Arguments = new() { ["path"] = "a.cs" } };
        var t0 = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        return new List<Message>
        {
            new() { Role = MessageRole.System, Content = "You are a coding agent", Timestamp = t0 },
            new() { Role = MessageRole.User, Content = "first request", Timestamp = t0.AddSeconds(1) },
            new() { Role = MessageRole.Assistant, Content = "Reading first.", ToolCalls = new() { read }, Timestamp = t0.AddSeconds(2) },
            new() { Role = MessageRole.Tool, ToolCallId = "c1", Content = "class A {}", Timestamp = t0.AddSeconds(3) },
            new() { Role = MessageRole.Assistant, Content = "", ToolCalls = new() { edit }, Timestamp = t0.AddSeconds(4) },
            new() { Role = MessageRole.Tool, ToolCallId = "c2", Content = "edited", Timestamp = t0.AddSeconds(5) },
            new() { Role = MessageRole.Assistant, Content = "Done.", Timestamp = t0.AddSeconds(6) },
            new() { Role = MessageRole.User, Content = "second request", Timestamp = t0.AddSeconds(7) },
            new() { Role = MessageRole.Assistant, Content = "Sure.", Timestamp = t0.AddSeconds(8) },
        };
    }

    [Fact]
    public void LoadTranscript_RebuildsTheChronologicalTimeline_WithToolCards()
    {
        var t = new ChatTimeline();
        t.Add(new ChatMessage { Role = "Assistant", Content = "Welcome" });
        t.AddUser("stale");

        t.LoadTranscript(Transcript(), keepWelcome: true);

        Assert.Equal(new[]
        {
            "text:Welcome",
            "user:first request",
            "text:Reading first.",
            "tool:read_file",
            "tool:edit_file",
            "text:Done.",
            "user:second request",
            "text:Sure."
        }, Shape(t));

        var cards = t.Items.OfType<ToolCallItem>().Select(i => i.Card).ToList();
        Assert.Equal(new[] { "class A {}", "edited" }, cards.Select(c => c.Output));
        Assert.All(cards, c => Assert.False(c.IsRunning));
        Assert.All(cards, c => Assert.Equal(string.Empty, c.DurationText)); // not stored, so not shown
    }

    [Fact]
    public void LoadTranscript_CallWithoutStoredResult_IsNotLeftRunning()
    {
        var call = new ToolCall { Id = "x", Name = "execute_command", Arguments = new() { ["command"] = "dotnet test" } };
        var t = new ChatTimeline();
        t.LoadTranscript(new[] { new Message { Role = MessageRole.Assistant, ToolCalls = new() { call } } }, keepWelcome: false);

        Assert.False(Assert.Single(t.ToolCards).IsRunning);
    }

    [Fact]
    public async Task LoadTranscript_UserMessageNumbers_MatchTheRewindPoints()
    {
        var transcript = Transcript();
        transcript.Insert(1, new Message { Role = MessageRole.User, Content = "   " }); // blank, still counted by the rewinder

        var context = Substitute.For<IContextManager>();
        context.GetContextAsync("s").Returns(Task.FromResult(transcript));
        var checkpoints = Substitute.For<ICheckpointManager>();
        checkpoints.GetCheckpointsForSessionAsync("s").Returns(Task.FromResult(new List<CheckpointEntry>()));
        var rewinder = new ConversationRewinder(context, checkpoints);

        var points = await rewinder.ListPointsAsync("s");
        var t = new ChatTimeline();
        t.LoadTranscript(await rewinder.GetTranscriptAsync("s"), keepWelcome: false);

        var users = t.Items.OfType<UserMessageItem>().ToList();
        Assert.Equal(2, users.Count); // the blank one is not shown
        foreach (var user in users)
        {
            var point = Assert.Single(points, p => p.Number == user.RewindNumber);
            Assert.Equal(transcript[point.MessageIndex].Content, user.Content);
            Assert.Same(user, t.Items[t.IndexOfRewindPoint(point.Number)]);
        }
        Assert.Equal(new[] { 2, 3 }, users.Select(u => u.RewindNumber!.Value));
        Assert.Equal("#2", users[0].RewindLabel);
        Assert.Equal(-1, t.IndexOfRewindPoint(1)); // blank message: no item
    }
}
