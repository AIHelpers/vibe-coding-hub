namespace AiCodeAgent.Core.Models;

public record AgentExecutionContext
{
    public string SessionId { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public Dictionary<string, string> Environment { get; init; } = new();
    public bool IsReadOnly { get; init; }
    public List<string> AllowedPaths { get; init; } = new();
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