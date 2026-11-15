using System;

namespace AiCodeAgent.App.ViewModels;

/// <summary>An approval that waits for the user, as the chat reports it to the Review Queue.</summary>
/// <param name="Key">Identifies the approval; the queue answers by key.</param>
internal sealed record PendingApprovalInfo(
    string Key, string Risk, string ToolName, string Args, string Agent, string Role, ApprovalPreviewViewModel? Preview, DateTimeOffset Since);
