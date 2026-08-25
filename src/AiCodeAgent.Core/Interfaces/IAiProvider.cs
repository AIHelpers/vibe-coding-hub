using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

public interface IAiProvider
{
    string Name { get; }
    string[] SupportedModels { get; }
    
    Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
    
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    RiskLevel Risk { get; }
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context);
}

public interface IContextManager
{
    Task<List<Message>> GetContextAsync(string sessionId);
    Task AddMessageAsync(string sessionId, Message message);
    Task<int> GetTokenCountAsync(string sessionId);
    Task TrimContextAsync(string sessionId, int maxTokens);
    Task ClearAsync(string sessionId);

    /// <summary>List all known session IDs (for the session history panel).</summary>
    Task<IReadOnlyList<string>> GetSessionsAsync();
}

public interface IMemoryStore
{
    Task StoreAsync(string key, string content, Dictionary<string, string>? metadata = null);
    Task<List<MemoryEntry>> SearchAsync(string query, int topK = 5);
    Task<MemoryEntry?> GetAsync(string key);
    Task DeleteAsync(string key);
}

public interface ICodeIndexer
{
    Task IndexDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<List<CodeSnippet>> SearchAsync(string query, int topK = 10);
    Task<string> GetFileContentAsync(string path);
    IAsyncEnumerable<string> GetIndexedFilesAsync();
}

public interface IAgentOrchestrator
{
    Task<AgentResponse> RunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<AgentEvent> StreamRunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default);
}

public interface IAgentEventBus
{
    void Publish(AgentEvent evt);
    IAsyncEnumerable<AgentEvent> GetEventsAsync(CancellationToken cancellationToken = default);
}

public interface ICheckpointManager
{
    Task<CheckpointEntry> CreateCheckpointAsync(string filePath, string turnId, string? sessionId = null);
    Task<bool> RestoreCheckpointAsync(string checkpointId);
    Task<List<CheckpointEntry>> GetCheckpointsAsync(string turnId);
    Task<List<CheckpointEntry>> GetCheckpointsForSessionAsync(string sessionId);
    Task CleanupAsync(string turnId);

    /// <summary>
    /// Import a checkpoint snapshot (e.g. from a session bundle) into local
    /// checkpoint storage so it can be restored later.
    /// </summary>
    Task<CheckpointEntry> ImportCheckpointAsync(
        string checkpointId,
        string filePath,
        string originalContent,
        string turnId,
        string? sessionId = null);

    /// <summary>
    /// Restore a file from a checkpoint within a session, skipping symlinked
    /// and hard-linked files. Emits a <see cref="DiffProducedEvent"/> via the
    /// optional event bus so the user sees what changed.
    /// </summary>
    Task<bool> RestoreAsync(string sessionId, string checkpointId, IAgentEventBus? eventBus = null);

    /// <summary>List checkpoints for a session (survives session resume).</summary>
    Task<List<CheckpointEntry>> ListAsync(string sessionId);

    /// <summary>Keep only the <paramref name="keepCount"/> most recent checkpoints for a session.</summary>
    Task<int> PruneAsync(string sessionId, int keepCount);
}

public interface IPermissionService
{
    Task<bool> RequestApprovalAsync(ToolCall call, RiskLevel risk, AgentOptions options, string? agentId = null);
    void SetMode(PermissionMode mode, string? agentId = null);
    PermissionMode GetMode(string? agentId = null);
    PermissionMode CurrentMode { get; }
}

/// <summary>
/// Higher-level permission manager (Feature 10). Wraps <see cref="IPermissionService"/>
/// with a classifier, allow-rules, and scoped settings (org → project → personal).
/// The orchestrator and tools consult this before running file edits and shell
/// commands. Returns a <see cref="PermissionDecision"/> so callers can
/// distinguish "block" from "ask".
/// </summary>
public interface IPermissionManager
{
    /// <summary>Current effective mode (after applying scoped settings).</summary>
    Task<PermissionMode> GetModeAsync(string? agentId = null, CancellationToken ct = default);
    /// <summary>Set the mode at a particular scope.</summary>
    Task SetModeAsync(PermissionMode mode, PermissionScope scope = PermissionScope.Personal, string? agentId = null, CancellationToken ct = default);
    /// <summary>Cycle to the next mode (used by Shift+Tab in the CLI).</summary>
    Task<PermissionMode> CycleModeAsync(string? agentId = null, CancellationToken ct = default);
    /// <summary>
    /// Check whether an action may run without asking. Returns
    /// <see cref="PermissionDecision.Allow"/> to skip the prompt,
    /// <see cref="PermissionDecision.Ask"/> to prompt the user, and
    /// <see cref="PermissionDecision.Deny"/> to block outright.
    /// </summary>
    Task<PermissionDecision> CanExecuteAsync(ToolCall call, RiskLevel risk, AgentOptions options, string? agentId = null, CancellationToken ct = default);
    /// <summary>Add a persistent allow-rule at a given scope.</summary>
    Task AllowAsync(PermissionRule rule, CancellationToken ct = default);
    /// <summary>Load scoped settings from a settings file (e.g. ~/.aiagent/settings.json).</summary>
    Task LoadScopedSettingsAsync(string? settingsPath = null, CancellationToken ct = default);
}
