using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// The result returned to the main agent when a subagent finishes.
/// Subagent tool calls are kept out of the main conversation context;
/// only this summary is surfaced.
/// </summary>
public record SubagentSummary
{
    /// <summary>Unique id of the subagent session.</summary>
    public string SubagentId { get; init; } = string.Empty;

    /// <summary>The task that was delegated to the subagent.</summary>
    public string Task { get; init; } = string.Empty;

    /// <summary>Whether the session was forked from the parent conversation.</summary>
    public bool Forked { get; init; }

    /// <summary>The final natural-language answer produced by the subagent.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Number of tool calls executed by the subagent (not surfaced in main context).</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Token usage accumulated by the subagent.</summary>
    public TokenUsage Usage { get; init; } = new();

    /// <summary>Wall-clock duration of the subagent session.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Whether the subagent was cancelled or timed out.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>Non-null when the subagent ended with an error.</summary>
    public string? Error { get; init; }

    /// <summary>Convenience: an XML-tagged block to inject into the main agent's context.</summary>
    public string ToPromptBlock() =>
        $"<subagent_result id=\"{SubagentId}\">\n" +
        $"# Subagent result ({SubagentId})\nTask: {Task}\n" +
        (Forked ? "Mode: forked from current conversation\n" : "Mode: fresh context\n") +
        $"Tool calls: {ToolCallCount}  |  Tokens: {Usage.TotalTokens}  |  Duration: {Duration.TotalSeconds:F1}s\n" +
        (WasCancelled ? "Status: cancelled\n" : Error is null ? "Status: completed\n" : $"Status: error ({Error})\n") +
        $"Summary:\n{Content}\n" +
        "</subagent_result>";
}