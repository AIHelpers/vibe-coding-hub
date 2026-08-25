using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Spawns and runs subagents in their own context window, keeping their
/// tool calls out of the main conversation. The main agent receives only
/// a <see cref="SubagentSummary"/> when the subagent finishes.
/// </summary>
public interface ISubagentRunner
{
    /// <summary>Spawn a subagent with a fresh context window.</summary>
    Task<SubagentSummary> RunAsync(
        string task,
        SubagentContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Spawn a subagent with a fork (copy) of the current conversation.</summary>
    Task<SubagentSummary> ForkAsync(
        string task,
        SubagentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-subagent execution settings. The <see cref="ParentSessionId"/> is used
/// for forking; <see cref="WorkingDirectory"/> and tool access are inherited
/// from the parent agent configuration unless overridden here.
/// </summary>
public record SubagentContext
{
    /// <summary>Parent session id (used for forking and permission inheritance).</summary>
    public string ParentSessionId { get; init; } = string.Empty;

    /// <summary>Working directory the subagent operates in.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Model id; null inherits parent default.</summary>
    public string? Model { get; init; }

    /// <summary>Max agent loop iterations for the subagent.</summary>
    public int MaxIterations { get; init; } = 12;

    /// <summary>Max tokens for the subagent's context window.</summary>
    public int MaxTokens { get; init; } = 32768;

    /// <summary>Optional role/preset label for the subagent.</summary>
    public string? Role { get; init; }

    /// <summary>Optional allow-list of tool names the subagent may use; null = all.</summary>
    public List<string>? EnabledTools { get; init; }

    /// <summary>Overall hard timeout for the subagent session.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}