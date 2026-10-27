using System;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>A tool call that finished, as the sample project's checklist needs to see it.</summary>
/// <param name="ToolName">The tool, e.g. <c>edit_file</c> or <c>execute_command</c>.</param>
/// <param name="Command">The shell command for <c>execute_command</c>, else null.</param>
/// <param name="IsError">True when the call failed (a command that exited with a non-zero code is an error).</param>
public sealed record ToolFinishedInfo(string ToolName, string? Command, bool IsError);

public partial class ChatViewModel
{
    /// <summary>The user sent a message that starts an agent run. Used by the sample project's checklist.</summary>
    public event EventHandler? TaskSubmitted;

    /// <summary>A tool call finished. Used by the sample project's checklist.</summary>
    public event EventHandler<ToolFinishedInfo>? ToolFinished;

    private void RaiseToolFinished(ToolCall call, ToolResult result)
    {
        if (ToolFinished == null) return;
        string? command = null;
        if (call.Arguments.TryGetValue("command", out var value))
            command = value?.ToString();
        ToolFinished.Invoke(this, new ToolFinishedInfo(call.Name, command, result.IsError));
    }
}
