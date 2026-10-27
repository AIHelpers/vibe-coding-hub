using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// A tool that can describe what a call would do without doing it (no file writes, no process starts). The
/// orchestrator attaches the result to <see cref="ApprovalRequestEvent.Preview"/> so the approval dialog can show a
/// diff or the command instead of raw arguments.
/// </summary>
public interface IPreviewableTool
{
    Task<ToolPreview?> PreviewAsync(ToolCall call, AgentExecutionContext context);
}
