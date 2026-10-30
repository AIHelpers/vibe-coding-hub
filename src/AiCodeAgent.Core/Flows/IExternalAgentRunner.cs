using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>What a flow step hands to an external agent (E6-S5 prototype: an Agent Client Protocol agent).</summary>
public sealed record ExternalAgentRequest(
    string Command,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    string Prompt,
    PermissionMode PermissionMode,
    TimeSpan Timeout);

/// <summary>One permission the agent asked for and what VCH answered.</summary>
public sealed record ExternalAgentPermission(string Title, string Kind, bool Allowed);

/// <param name="Text">The agent's final message: this is the step's output and flows into the next steps as <c>{input:id}</c>.</param>
public sealed record ExternalAgentResult(string Text, string StopReason, int ToolCalls, IReadOnlyList<ExternalAgentPermission> Permissions);

public sealed class ExternalAgentException : Exception
{
    public ExternalAgentException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Runs one prompt on an agent that lives outside VCH and returns its final message.</summary>
public interface IExternalAgentRunner
{
    /// <param name="onProgress">Short human-readable progress lines (tool calls, permission answers).</param>
    Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken);
}
