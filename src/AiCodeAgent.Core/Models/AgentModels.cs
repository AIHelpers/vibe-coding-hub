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

/// <summary>
/// Granular permission flags that control which risk categories
/// the agent is allowed to perform without asking.
/// When a flag is false the agent will prompt for approval on
/// any tool call that carries the corresponding risk level.
/// </summary>
public record GranularRights
{
    /// <summary>Allow read-only operations (file reads, grep, etc.) without asking.</summary>
    public bool AllowRead { get; init; } = true;
    /// <summary>Allow file edit/write operations without asking.</summary>
    public bool AllowEdit { get; init; } = false;
    /// <summary>Allow shell command execution without asking.</summary>
    public bool AllowExecute { get; init; } = false;
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
    public string CheckpointId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string TurnId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
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
    /// <summary>Granular per-risk-category rights (read/edit/execute). When set, these override the coarse PermissionMode for the corresponding risk levels.</summary>
    public GranularRights? Rights { get; init; }
    public string? SessionId { get; init; }
    /// <summary>Agent instance key for multi-agent sessions.</summary>
    public string? AgentId { get; init; }
    /// <summary>Role label (planner/implementer/reviewer) for display.</summary>
    public string? Role { get; init; }
    /// <summary>
    /// Role-specific system prompt instructions (typically sourced from an
    /// <see cref="AgentRolePreset"/>). When set, these are injected into the
    /// live system prompt sent to the model, in addition to the generic
    /// agent instructions. Without this, a Role label was cosmetic only.
    /// </summary>
    public string? RoleSystemPrompt { get; init; }
    /// <summary>
    /// Confirmed application requirements captured through the conversational
    /// clarification loop (Feature 8). When set, these are injected into the
    /// system prompt so generated code matches the user's intent.
    /// </summary>
    public AppRequirements? Requirements { get; init; }
    /// <summary>
    /// Project memory loaded from AIAGENT.md at session start. When set, it is
    /// injected as part of the system prompt so persistent instructions and
    /// conventions are honored every session.
    /// </summary>
    public AiCodeAgent.Core.Interfaces.ProjectMemory? ProjectMemory { get; init; }
    /// <summary>
    /// Bounded auto-memory content (learned preferences) loaded from
    /// <c>~/.aiagent/MEMORY.md</c> at session start (Feature 05). When set,
    /// injected as a system message alongside project memory.
    /// </summary>
    public string? AutoMemory { get; init; }
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
public record ApprovalRequestEvent(ToolCall Call, TaskCompletionSource<bool> Approval, RiskLevel Risk = RiskLevel.Write) : AgentEvent;
public record DiffProducedEvent(DiffEntry Diff) : AgentEvent;
public record CheckpointCreatedEvent(CheckpointEntry Checkpoint) : AgentEvent;
public record StatusUpdateEvent(string Status, string? Detail = null) : AgentEvent;
public record TokenUsageEvent(TokenUsage Usage) : AgentEvent;

/// <summary>Emitted when context is auto-compacted (older messages summarized).</summary>
public record ContextCompactedEvent(int MessagesBefore, int MessagesAfter, int TokensSaved, string? Focus) : AgentEvent;

/// <summary>Emitted when the thrashing guard stops compaction due to repeated refills.</summary>
public record ContextThrashingEvent(int Attempts, string Message) : AgentEvent;

// ===== Autonomous Multi-File Agent Events (Feature 4) =====

/// <summary>Emitted when the planner has produced a plan, before execution begins.</summary>
public record PlanGeneratedEvent(Agent.PlanStep[] Steps, string RunId) : AgentEvent;

/// <summary>Emitted when a plan step starts running.</summary>
public record PlanStepStartedEvent(int StepIndex, Agent.PlanStep Step) : AgentEvent;

/// <summary>Emitted when a plan step awaits user approval at a step boundary.</summary>
public record PlanStepApprovalEvent(int StepIndex, Agent.PlanStep Step, TaskCompletionSource<bool> Approval) : AgentEvent;

/// <summary>Emitted when a plan step completes (status reflects success/failure/skip).</summary>
public record PlanStepFinishedEvent(int StepIndex, Agent.PlanStep Step) : AgentEvent;

/// <summary>Emitted when the autonomous runner updates its execution log (step status change).</summary>
public record PlanStepStatusChangedEvent(int StepIndex, Agent.PlanStepStatus Status) : AgentEvent;

/// <summary>Emitted when the autonomous run finishes (all steps done, failed, or cancelled).</summary>
public record PlanRunFinishedEvent(Agent.ExecutionLog Log) : AgentEvent;

/// <summary>
/// LSP diagnostic update for a file. Published on the AgentEventBus so agents
/// can query live diagnostics instead of shelling out to run_diagnostics.
/// </summary>
public record LspDiagnosticsEvent(
    string FilePath,
    IReadOnlyList<LspDiagnosticItem> Diagnostics) : AgentEvent;

/// <summary>A single diagnostic reported by a language server.</summary>
public record LspDiagnosticItem(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string Severity,
    string Message,
    string? Source = null,
    string? Code = null);

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
    private readonly List<Diffing.DiffHunk> _hunks = new();

    public IReadOnlyList<DiffEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>Canonical store of parsed diff hunks (single- or multi-agent).</summary>
    public IReadOnlyList<Diffing.DiffHunk> Hunks
    {
        get { lock (_lock) return _hunks.ToList(); }
    }

    public void Add(DiffEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    public void AddRange(IEnumerable<DiffEntry> entries)
    {
        lock (_lock) _entries.AddRange(entries);
    }

    /// <summary>Add a parsed diff hunk to the canonical store.</summary>
    public void AddHunk(Diffing.DiffHunk hunk)
    {
        lock (_lock) _hunks.Add(hunk);
    }

    /// <summary>Add multiple parsed diff hunks.</summary>
    public void AddHunks(IEnumerable<Diffing.DiffHunk> hunks)
    {
        lock (_lock) _hunks.AddRange(hunks);
    }

    /// <summary>Update the status of a hunk by ID.</summary>
    public bool UpdateHunkStatus(string hunkId, Diffing.HunkStatus status)
    {
        lock (_lock)
        {
            var index = _hunks.FindIndex(h => h.HunkId == hunkId);
            if (index < 0) return false;
            var existing = _hunks[index];
            _hunks[index] = existing with { Status = status };
            return true;
        }
    }

    /// <summary>Get hunks for a specific file path.</summary>
    public IEnumerable<Diffing.DiffHunk> GetHunksForFile(string filePath)
    {
        lock (_lock) return _hunks.Where(h => h.FilePath == filePath).ToList();
    }

    /// <summary>Get hunks attributed to a specific agent.</summary>
    public IEnumerable<Diffing.DiffHunk> GetHunksByAgent(string agentId)
    {
        lock (_lock) return _hunks.Where(h => h.AgentId == agentId).ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _hunks.Clear();
        }
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

    /// <summary>
    /// Optional delegate used to query the workspace index for relevant files
    /// by name/symbol match when building agent context automatically.
    /// Set by the host (App/CLI) to avoid a hard dependency on the indexing project.
    /// </summary>
    public Func<string, int, Task<IReadOnlyList<string>>>? FileQueryDelegate { get; set; }

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

    /// <summary>
    /// Query the workspace index for files relevant to a filter (name/symbol match).
    /// Falls back to an empty list when no index query delegate is configured.
    /// </summary>
    public async Task<IReadOnlyList<string>> QueryRelevantFilesAsync(
        string filter,
        int limit = 20)
    {
        if (FileQueryDelegate == null)
            return Array.Empty<string>();

        try
        {
            return await FileQueryDelegate(filter, limit).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<string>();
        }
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
