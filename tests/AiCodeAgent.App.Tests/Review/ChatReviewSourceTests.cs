using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.App.Tests.Review;

public class ChatReviewSourceTests
{
    private static ChatViewModel CreateChat()
    {
        var bus = new AgentEventBus();
        var service = new AgentService(
            Substitute.For<IAgentOrchestrator>(), Substitute.For<IToolRegistry>(), Substitute.For<IContextManager>(),
            NullLogger<AgentService>.Instance, bus, new SessionRecorder(bus, NullLogger<SessionRecorder>.Instance));
        return new ChatViewModel(service, new EditorPaneViewModel(), new SharedChangeset());
    }

    private static ApprovalRequestEvent Request(string path) => new(
        new ToolCall { Name = "edit_file", Arguments = new Dictionary<string, object?> { ["path"] = path } },
        new TaskCompletionSource<bool>())
    {
        Preview = new ToolPreview { FileChange = new FileChangePreview(path, "old\n", "new\n") }
    };

    [AvaloniaFact]
    public void PendingApprovals_AreListed_AndCanBeAnsweredOutOfOrder()
    {
        var chat = CreateChat();
        chat.IsProcessing = true;
        var first = Request("a.cs");
        var second = Request("b.cs");
        chat.OnApprovalRequested(first);
        chat.OnApprovalRequested(second);

        var source = new ChatReviewSource(chat);
        var items = source.Pending();
        Assert.Equal(2, items.Count(i => i.Kind == ReviewKind.Approval));

        var secondItem = items.Single(i => i.Detail.Contains("b.cs") || i.Preview?.FilePath == "b.cs");
        Assert.True(secondItem.Approve().Done);
        Assert.True(second.Approval.Task.IsCompleted && second.Approval.Task.Result);
        Assert.False(first.Approval.Task.IsCompleted);
        Assert.Single(source.Pending(), i => i.Kind == ReviewKind.Approval);
    }

    [AvaloniaFact]
    public void DenyWithComment_ReturnsTheDenial_AndTheListShrinks()
    {
        var chat = CreateChat();
        chat.IsProcessing = true;
        var req = Request("a.cs");
        chat.OnApprovalRequested(req);
        var source = new ChatReviewSource(chat);
        var item = source.Pending().Single(i => i.Kind == ReviewKind.Approval);

        Assert.True(item.RequestChanges!("Use the helper instead").Done);

        Assert.True(req.Approval.Task.IsCompleted);
        Assert.False(req.Approval.Task.Result);
        Assert.DoesNotContain(source.Pending(), i => i.Kind == ReviewKind.Approval);
    }

    [AvaloniaFact]
    public void ApprovalsInReviewQueue_HidesTheDialog_AndRestoresItWhenOff()
    {
        var chat = CreateChat();
        chat.IsProcessing = true;
        chat.ApprovalsInReviewQueue = true;
        chat.OnApprovalRequested(Request("a.cs"));
        Assert.False(chat.ShowApprovalDialog);

        chat.ApprovalsInReviewQueue = false;
        Assert.True(chat.ShowApprovalDialog);
    }

    [AvaloniaFact]
    public void ReviewComment_IsRefused_WhileTheAgentIsBusy_OrADraftExists()
    {
        var chat = CreateChat();
        chat.IsProcessing = true;
        Assert.False(chat.TrySendReviewComment("change it", out var busy));
        Assert.NotEmpty(busy);

        chat.IsProcessing = false;
        chat.InputText = "half-written";
        Assert.False(chat.TrySendReviewComment("change it", out _));
        Assert.False(chat.TrySendReviewComment("   ", out _));
        Assert.Equal("half-written", chat.InputText);
    }
}
