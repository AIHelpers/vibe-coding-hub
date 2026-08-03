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
    /// <summary>Agent that produced this diff hunk (for multi-agent attribution).</summary>
    public string? AgentId { get; init; }
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
    /// <summary>Agent instance key for multi-agent sessions.</summary>
    public string? AgentId { get; init; }
    /// <summary>Role label (planner/implementer/reviewer) for display.</summary>
    public string? Role { get; init; }
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
    /// <summary>Agent instance key for multi-agent sessions.</summary>
    public string? AgentId { get; init; }
    /// <summary>Role label (planner/implementer/reviewer) for display.</summary>
    public string? Role { get; init; }
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

/// <summary>Event tagged with the source agent for multi-agent sessions.</summary>
public record AgentTaggedEvent(AgentEvent Inner, string AgentId, string? Role = null) : AgentEvent;

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

// ===== Multi-Agent Session Models =====

/// <summary>Role preset definition (data-driven, extensible).</summary>
public record AgentRolePreset
{
    public string Role { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public PermissionMode DefaultPermissionMode { get; init; } = PermissionMode.Ask;
    public List<string> AllowedTools { get; init; } = new();
    public string? Description { get; init; }
}

/// <summary>A single step in a multi-agent session plan.</summary>
public record SessionStep
{
    public string AgentId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public AgentOptions Options { get; init; } = new();
}

/// <summary>Plan for a multi-agent session (sequential turn-taking).</summary>
public record SessionPlan
{
    public string SessionId { get; init; } = string.Empty;
    public string? UserGoal { get; init; }
    public List<SessionStep> Steps { get; init; } = new();
}

/// <summary>Shared staged edits across agents with per-hunk attribution.</summary>
public class SharedChangeset
{
    private readonly object _lock = new();
    private readonly List<DiffEntry> _entries = new();

    public IReadOnlyList<DiffEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public void Add(DiffEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    public void AddRange(IEnumerable<DiffEntry> entries)
    {
        lock (_lock) _entries.AddRange(entries);
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    public IEnumerable<DiffEntry> GetByAgent(string agentId)
    {
        lock (_lock) return _entries.Where(e => e.AgentId == agentId).ToList();
    }
}

/// <summary>Shared context store across agents (file/symbol context, token budget).</summary>
public class SharedContextStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _symbolCache = new(StringComparer.OrdinalIgnoreCase);
    private int _tokenBudget;

    public int TokenBudget
    {
        get { lock (_lock) return _tokenBudget; }
        set { lock (_lock) _tokenBudget = value; }
    }

    public void CacheFile(string path, string content)
    {
        lock (_lock) _fileCache[path] = content;
    }

    public bool TryGetFile(string path, out string? content)
    {
        lock (_lock) return _fileCache.TryGetValue(path, out content);
    }

    public void CacheSymbol(string key, object value)
    {
        lock (_lock) _symbolCache[key] = value;
    }

    public bool TryGetSymbol(string key, out object? value)
    {
        lock (_lock) return _symbolCache.TryGetValue(key, out value);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _fileCache.Clear();
            _symbolCache.Clear();
            _tokenBudget = 0;
        }
    }
}
