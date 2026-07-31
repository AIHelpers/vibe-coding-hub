namespace AiCodeAgent.Core.Models;

public enum RiskLevel
{
    /// <summary>Read-only operations (file reads, grep, etc.)</summary>
    Read = 0,
    /// <summary>Write operations (file edits, writes, etc.)</summary>
    Write = 1,
    /// <summary>Shell execution, git operations, etc.</summary>
    Execute = 2
}

public enum PermissionMode
{
    /// <summary>Ask for every write/execute operation</summary>
    Ask = 0,
    /// <summary>Auto-approve edits, ask for execution</summary>
    AutoEdit = 1,
    /// <summary>Auto-approve everything</summary>
    FullAuto = 2,
    /// <summary>Read-only planning mode</summary>
    Plan = 3
}

public record PermissionSettings
{
    public PermissionMode Mode { get; init; } = PermissionMode.Ask;
    public Dictionary<string, bool> AllowedTools { get; init; } = new(); // "always_allow" list
    public bool AlwaysAllowRead { get; init; } = true;
    public string? ProjectAllowlistPath { get; init; }
}

public record CheckpointEntry
{
    public string FilePath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string TurnId { get; init; } = string.Empty;
}

public record DiffEntry
{
    public string FilePath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public string ModifiedContent { get; init; } = string.Empty;
    public string DiffText { get; init; } = string.Empty;
    public bool IsAccepted { get; init; }
    public bool IsRejected { get; init; }
}

public class ToolCallCard
{
    public string ToolName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? Output { get; set; }
    public TimeSpan? Duration { get; set; }
    public bool IsError { get; set; }
    public bool IsExpanded { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool NeedsApproval { get; set; }
    public string? ApprovalId { get; set; }
}

public record AgentExecutionContext
{
    public string SessionId { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public Dictionary<string, string> Environment { get; init; } = new();
    public bool IsReadOnly { get; init; }
    public List<string> AllowedPaths { get; init; } = new();
    public PermissionSettings? Permissions { get; init; }
}

public record AgentOptions
{
    public string? Model { get; init; }
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public int MaxIterations { get; init; } = 50;
    public int MaxTokens { get; init; } = 200_000;
    public bool AutoApprove { get; init; } = false;
    public bool Verbose { get; init; } = false;
    public List<string> EnabledTools { get; init; } = new();
    public bool IsReadOnly { get; init; } = false;
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Ask;
    public string? SessionId { get; init; }
}

public record AgentResponse
{
    public string Content { get; init; } = string.Empty;
    public List<ToolExecution> ToolExecutions { get; init; } = new();
    public TokenUsage TotalUsage { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public bool WasCancelled { get; init; }
}

public record ToolExecution
{
    public ToolCall Call { get; init; } = null!;
    public ToolResult Result { get; init; } = null!;
    public TimeSpan Duration { get; init; }
}

public abstract record AgentEvent;
public record TextDeltaEvent(string Delta) : AgentEvent;
public record ThinkingEvent(string Content) : AgentEvent;
public record ToolCallStartEvent(ToolCall Call) : AgentEvent;
public record ToolCallEndEvent(ToolCall Call, ToolResult Result, TimeSpan Duration) : AgentEvent;
public record AgentFinishedEvent(AgentResponse Response) : AgentEvent;
public record AgentErrorEvent(Exception Error) : AgentEvent;
public record ApprovalRequestEvent(ToolCall Call, TaskCompletionSource<bool> Approval) : AgentEvent;
public record DiffProducedEvent(DiffEntry Diff) : AgentEvent;
public record CheckpointCreatedEvent(CheckpointEntry Checkpoint) : AgentEvent;
public record StatusUpdateEvent(string Status, string? Detail = null) : AgentEvent;
public record TokenUsageEvent(TokenUsage Usage) : AgentEvent;

public record MemoryEntry
{
    public string Key { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new();
    public float Score { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public record CodeSnippet
{
    public string FilePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Language { get; init; } = string.Empty;
    public float Score { get; init; }
}