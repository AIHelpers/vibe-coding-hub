using AiCodeAgent.App.ViewModels;
using Avalonia.Input;

namespace AiCodeAgent.App.Tests.Approvals;

public class ApprovalPolicyTests
{
    [Fact]
    public void ByDefault_NothingIsAutoApproved()
    {
        var policy = new ApprovalPolicy();
        Assert.False(policy.TryAutoApprove("edit_file", out _));
        Assert.False(policy.TryAutoApprove("execute_command", out _));
    }

    [Fact]
    public void AllEditsThisTask_CoversEditToolsOnly_UntilTheTaskEnds()
    {
        var policy = new ApprovalPolicy();
        policy.AllowEditsForTask();

        foreach (var tool in new[] { "edit_file", "multi_edit", "apply_patch", "write_file" })
        {
            Assert.True(policy.TryAutoApprove(tool, out var reason));
            Assert.Equal("edits allowed for this task", reason);
        }
        Assert.False(policy.TryAutoApprove("execute_command", out _));
        Assert.False(policy.TryAutoApprove("verify_changes", out _));

        policy.EndTask();
        Assert.False(policy.TryAutoApprove("edit_file", out _));
    }

    [Fact]
    public void AlwaysAllowTool_LastsTheSession_NotJustTheTask()
    {
        var policy = new ApprovalPolicy();
        policy.AllowToolForSession("execute_command");

        policy.EndTask();
        Assert.True(policy.TryAutoApprove("execute_command", out var reason));
        Assert.Equal("always allowed this session", reason);
        Assert.False(policy.TryAutoApprove("edit_file", out _));

        policy.ResetSession();
        Assert.False(policy.TryAutoApprove("execute_command", out _));
    }

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, false, false, ApprovalKeyAction.Approve)]
    [InlineData(Key.Enter, KeyModifiers.None, false, true, ApprovalKeyAction.None)]   // a focused button gets the Enter
    [InlineData(Key.Enter, KeyModifiers.None, true, false, ApprovalKeyAction.None)]   // feedback box: Enter sends feedback
    [InlineData(Key.Enter, KeyModifiers.Shift, false, false, ApprovalKeyAction.None)]
    [InlineData(Key.Escape, KeyModifiers.None, false, false, ApprovalKeyAction.Deny)]
    [InlineData(Key.Escape, KeyModifiers.None, true, false, ApprovalKeyAction.CloseFeedback)]
    [InlineData(Key.A, KeyModifiers.None, false, false, ApprovalKeyAction.None)]
    public void Keys(Key key, KeyModifiers modifiers, bool feedbackOpen, bool onButton, ApprovalKeyAction expected)
    {
        Assert.Equal(expected, ApprovalKeys.Resolve(key, modifiers, feedbackOpen, onButton));
    }
}
