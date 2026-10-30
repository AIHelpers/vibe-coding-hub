using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>What a flow step hands to an external agent (E6-S5 prototype: an Agent Client Protocol agent).</summary>
public sealed record ExternalAgentRequest(
    string Command,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    string Prompt,
    PermissionMode PermissionMode,
    TimeSpan Timeout)
{
    /// <summary>
    /// Asked when the permission mode says "ask the user" (a command to run, a file to delete...). Returns true to allow.
    /// Null = nobody to ask, so such requests are refused.
    /// </summary>
    public Func<ExternalPermissionQuestion, CancellationToken, Task<bool>>? Ask { get; init; }
    /// <summary>Called with the full path just before the agent writes a file (the host takes a checkpoint).</summary>
    public Func<string, Task>? BeforeWrite { get; init; }
    /// <summary>Called after a write with (path, content before, content after) so the host can show the diff.</summary>
    public Action<string, string, string>? AfterWrite { get; init; }
}

/// <summary>What the agent wants to do, in terms the approval dialog can show.</summary>
public sealed record ExternalPermissionQuestion(string Title, string Kind);

/// <summary>One permission the agent asked for and what VCH answered.</summary>
public sealed record ExternalAgentPermission(string Title, string Kind, bool Allowed);

/// <param name="Text">The agent's final message: this is the step's output and flows into the next steps as <c>{input:id}</c>.</param>
public sealed record ExternalAgentResult(string Text, string StopReason, int ToolCalls, IReadOnlyList<ExternalAgentPermission> Permissions)
{
    /// <summary>Tokens the agent reported for the turn (ACP <c>usage</c> or <c>_meta.usage</c>); null when it reported none.</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>Where a flow step with an external agent starts it, and what it should be told.</summary>
/// <param name="Instructions">Persona and skill instructions of the character, prepended to the step's prompt (empty for a plain <c>acp</c> step).</param>
public sealed record ExternalAgentTarget(
    string Command,
    IReadOnlyList<string> Args,
    string? CharacterId,
    PermissionMode? PermissionMode,
    string Instructions);

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
