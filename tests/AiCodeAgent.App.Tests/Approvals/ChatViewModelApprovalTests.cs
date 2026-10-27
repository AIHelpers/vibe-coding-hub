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

namespace AiCodeAgent.App.Tests.Approvals;

/// <summary>E2-S5: the session allow-list and "Deny with feedback" as the chat sees them.</summary>
public class ChatViewModelApprovalTests
{
    private static ChatViewModel CreateChat()
    {
        var bus = new AgentEventBus();
        var service = new AgentService(
            Substitute.For<IAgentOrchestrator>(),
            Substitute.For<IToolRegistry>(),
            Substitute.For<IContextManager>(),
            NullLogger<AgentService>.Instance,
            bus,
            new SessionRecorder(bus, NullLogger<SessionRecorder>.Instance));
        return new ChatViewModel(service, new EditorPaneViewModel(), new SharedChangeset());
    }

    private static ApprovalRequestEvent Request(string tool, string path = "a.cs") => new(
        new ToolCall { Name = tool, Arguments = new Dictionary<string, object?> { ["path"] = path, ["command"] = "dotnet test" } },
        new TaskCompletionSource<bool>(),
        tool == "execute_command" ? RiskLevel.Execute : RiskLevel.Write)
    {
        Preview = tool == "execute_command"
            ? new ToolPreview { Command = new CommandPreview(new[] { "dotnet test" }, "/repo", false) }
            : new ToolPreview { FileChange = new FileChangePreview(path, "old\n", "new\n") }
    };

    private static bool? Answer(ApprovalRequestEvent e) => e.Approval.Task.IsCompleted ? e.Approval.Task.Result : null;

    private static ApprovalItem LastApprovalItem(ChatViewModel vm) => vm.Timeline.Items.OfType<ApprovalItem>().Last();

    [AvaloniaFact]
    public void ApproveAllEditsThisTask_AutoApprovesLaterEdits_CommandsStillAsk_UntilTheTaskEnds()
    {
        var vm = CreateChat();
        vm.IsProcessing = true; // a task is running

        var first = Request("edit_file");
        vm.OnApprovalRequested(first);
        Assert.True(vm.ShowApprovalDialog);
        Assert.True(vm.CanApproveAllEdits);
        Assert.Equal(ApprovalPreviewKind.FileDiff, vm.ApprovalPreview!.Kind);

        vm.ApproveAllEditsThisTaskCommand.Execute(null);
        Assert.True(Answer(first));
        Assert.False(vm.ShowApprovalDialog);

        var laterEdit = Request("write_file", "b.cs");
        vm.OnApprovalRequested(laterEdit);
        Assert.True(Answer(laterEdit));               // no dialog
        Assert.False(vm.ShowApprovalDialog);
        Assert.Equal("✓ Approved · edits allowed for this task", LastApprovalItem(vm).StatusText);

        var command = Request("execute_command");
        vm.OnApprovalRequested(command);
        Assert.Null(Answer(command));                 // commands still ask
        Assert.True(vm.ShowApprovalDialog);
        Assert.False(vm.CanApproveAllEdits);
        Assert.Equal(ApprovalPreviewKind.Command, vm.ApprovalPreview!.Kind);
        vm.DenyToolCommand.Execute(null);
        Assert.False(Answer(command));

        vm.IsProcessing = false; // the task ended

        var nextTaskEdit = Request("edit_file");
        vm.OnApprovalRequested(nextTaskEdit);
        Assert.Null(Answer(nextTaskEdit));
        Assert.True(vm.ShowApprovalDialog);
    }

    [AvaloniaFact]
    public void ApproveAllEdits_AlsoApprovesEditsAlreadyWaitingInTheQueue()
    {
        var vm = CreateChat();
        vm.IsProcessing = true;
        var first = Request("edit_file", "a.cs");
        var queued = Request("multi_edit", "b.cs");
        var queuedCommand = Request("execute_command");
        vm.OnApprovalRequested(first);
        vm.OnApprovalRequested(queued);
        vm.OnApprovalRequested(queuedCommand);

        vm.ApproveAllEditsThisTaskCommand.Execute(null);

        Assert.True(Answer(first));
        Assert.True(Answer(queued));
        Assert.Null(Answer(queuedCommand));
        Assert.Equal("execute_command", vm.ApprovalToolName); // the dialog moved on to the command
    }

    [AvaloniaFact]
    public void AlwaysAllowTool_LastsAcrossTasks_UntilTheChatIsCleared()
    {
        var vm = CreateChat();
        vm.IsProcessing = true;
        var first = Request("execute_command");
        vm.OnApprovalRequested(first);
        Assert.Equal("Always allow execute_command this session", vm.AlwaysAllowLabel);

        vm.AlwaysAllowToolThisSessionCommand.Execute(null);
        Assert.True(Answer(first));

        vm.IsProcessing = false;
        vm.IsProcessing = true; // next task
        var second = Request("execute_command");
        vm.OnApprovalRequested(second);
        Assert.True(Answer(second));
        Assert.Equal("✓ Approved · always allowed this session", LastApprovalItem(vm).StatusText);

        var edit = Request("edit_file");
        vm.OnApprovalRequested(edit);
        Assert.Null(Answer(edit)); // only that tool
        vm.DenyToolCommand.Execute(null);

        vm.ClearChatCommand.Execute(null);
        var afterClear = Request("execute_command");
        vm.OnApprovalRequested(afterClear);
        Assert.Null(Answer(afterClear));
    }

    [AvaloniaFact]
    public void DenyWithFeedback_PutsTheTextOnTheRequest_AndDenies()
    {
        var vm = CreateChat();
        var request = Request("edit_file");
        vm.OnApprovalRequested(request);

        vm.StartDenyWithFeedbackCommand.Execute(null);
        Assert.True(vm.IsEnteringApprovalFeedback);
        vm.ApprovalFeedbackText = "  Keep the public API; add an overload.  ";
        vm.DenyWithFeedbackCommand.Execute(null);

        Assert.False(Answer(request));
        Assert.Equal("Keep the public API; add an overload.", request.Feedback.Text);
        Assert.False(vm.ShowApprovalDialog);
        Assert.False(vm.IsEnteringApprovalFeedback);
        Assert.Equal("✕ Declined · feedback: Keep the public API; add an overload.", LastApprovalItem(vm).StatusText);
    }

    [AvaloniaFact]
    public void CancelFeedback_KeepsTheRequestOpen()
    {
        var vm = CreateChat();
        var request = Request("edit_file");
        vm.OnApprovalRequested(request);

        vm.StartDenyWithFeedbackCommand.Execute(null);
        vm.ApprovalFeedbackText = "draft";
        vm.CancelDenyWithFeedbackCommand.Execute(null);

        Assert.Null(Answer(request));
        Assert.True(vm.ShowApprovalDialog);
        Assert.False(vm.IsEnteringApprovalFeedback);
        Assert.Equal(string.Empty, vm.ApprovalFeedbackText);
    }

    [AvaloniaFact]
    public void AgentRow_ShowsTheAgent_OrIsHiddenForThePlainAgent()
    {
        var vm = CreateChat();
        vm.OnApprovalRequested(Request("edit_file"));
        Assert.False(vm.HasApprovalAgent);
        vm.DenyToolCommand.Execute(null);

        vm.OnApprovalRequested(Request("edit_file"), "agent-2", "coder");
        Assert.True(vm.HasApprovalAgent);
        Assert.Equal("coder (agent-2)", vm.ApprovalAgent);
    }
}
