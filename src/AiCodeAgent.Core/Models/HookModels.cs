using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Models;

/// <summary>
/// Lifecycle events at which hooks can be invoked.
/// </summary>
public enum HookEvent
{
    /// <summary>Before a tool runs; can block or modify the tool call.</summary>
    PreToolUse,
    /// <summary>After a tool runs; can inspect the result.</summary>
    PostToolUse,
    /// <summary>Before the agent session starts processing.</summary>
    PreSessionStart,
    /// <summary>After the agent session ends.</summary>
    PostSessionEnd,
    /// <summary>When an error occurs during the agent loop.</summary>
    OnError
}

/// <summary>
/// A single hook definition: the command to run, the event it fires on,
/// and options controlling its behavior.
/// </summary>
public record HookDefinition
{
    /// <summary>Unique name for display in /hooks.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Event that triggers this hook.</summary>
    public HookEvent Event { get; init; } = HookEvent.PreToolUse;

    /// <summary>
    /// Shell command to execute. Supports placeholders:
    /// {tool}, {args}, {session}, {result}, {error}.
    /// </summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>When true, a non-zero exit (or deny output) blocks the tool call.</summary>
    public bool Blocking { get; init; }

    /// <summary>Timeout in seconds; 0 means no timeout.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Optional filter: only run for the named tool (PreToolUse/PostToolUse).</summary>
    public string? ToolFilter { get; init; }
}

/// <summary>
/// Context passed to hooks at invocation time.
/// </summary>
public record HookContext
{
    /// <summary>Event being fired.</summary>
    public HookEvent Event { get; init; }

    /// <summary>Session id (if available).</summary>
    public string? SessionId { get; init; }

    /// <summary>Tool call (for PreToolUse/PostToolUse).</summary>
    public ToolCall? ToolCall { get; init; }

    /// <summary>Tool result (for PostToolUse).</summary>
    public ToolResult? ToolResult { get; init; }

    /// <summary>Error (for OnError).</summary>
    public Exception? Error { get; init; }

    /// <summary>Working directory for the command process.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// Outcome of running a single hook.
/// </summary>
public record HookResult
{
    /// <summary>The hook definition that was executed.</summary>
    public HookDefinition Definition { get; init; } = null!;

    /// <summary>Captured stdout/stderr output.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Exit code of the process.</summary>
    public int ExitCode { get; init; }

    /// <summary>Whether the hook requested the action be blocked (deny).</summary>
    public bool Deny { get; init; }

    /// <summary>Whether the hook ran successfully (exit 0 and no timeout).</summary>
    public bool Success { get; init; }

    /// <summary>Error message if the hook failed to run.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when the hook timed out.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Aggregated result of running all hooks for a given event.
/// </summary>
public record HookRunResult
{
    public List<HookResult> Results { get; init; } = new();

    /// <summary>True when any blocking hook returned a deny.</summary>
    public bool Denied => Results.Any(r => r.Deny);

    /// <summary>Combined output of all hooks (for feedback into the loop).</summary>
    public string CombinedOutput => string.Join("\n", Results.Where(r => !string.IsNullOrWhiteSpace(r.Output)).Select(r => r.Output));
}