using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Coordinates exporting a session bundle: pulls the conversation from the
/// context manager, merges it with the recorder's event-derived data
/// (tool calls, changeset, checkpoints, roles), and writes the bundle to disk.
/// </summary>
public class SessionExportService
{
    private readonly IContextManager _contextManager;
    private readonly ILogger<SessionExportService> _logger;
    private readonly SessionRecorder _recorder;

    public SessionExportService(
        IContextManager contextManager,
        SessionRecorder recorder,
        ILogger<SessionExportService> logger)
    {
        _contextManager = contextManager;
        _recorder = recorder;
        _logger = logger;
    }

    /// <summary>
    /// Build a complete bundle for a session by merging the context manager's
    /// message log with the recorder's event-derived data.
    /// </summary>
    public async Task<SessionBundle> BuildBundleAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _recorder.GetSnapshot();

        // Pull the conversation from the context manager (source of truth for messages)
        var messages = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false);

        return new SessionBundle
        {
            SchemaVersion = SessionBundle.CurrentSchemaVersion,
            SessionId = string.IsNullOrEmpty(snapshot.SessionId) ? sessionId : snapshot.SessionId,
            CreatedAt = DateTime.UtcNow,
            Conversation = messages,
            ToolCallLog = snapshot.ToolCallLog,
            Changeset = snapshot.Changeset,
            CheckpointRefs = snapshot.CheckpointRefs,
            CheckpointSnapshots = snapshot.CheckpointSnapshots,
            RolesUsed = snapshot.RolesUsed,
            PromptPack = snapshot.PromptPack
        };
    }

    /// <summary>
    /// Export a session bundle to a file. Supports both plain JSON and
    /// `.agentsession` zip bundles.
    /// </summary>
    public async Task ExportAsync(
        string sessionId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var bundle = await BuildBundleAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await SessionExporter.ExportAsync(bundle, outputPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Exported session {SessionId} to {Path} ({MessageCount} messages, {ToolCallCount} tool calls)",
            sessionId,
            outputPath,
            bundle.Conversation.Count,
            bundle.ToolCallLog.Count);
    }

    /// <summary>
    /// Export a pre-built bundle to a file.
    /// </summary>
    public async Task ExportBundleAsync(
        SessionBundle bundle,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await SessionExporter.ExportAsync(bundle, outputPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Exported session {SessionId} to {Path} ({MessageCount} messages, {ToolCallCount} tool calls)",
            bundle.SessionId,
            outputPath,
            bundle.Conversation.Count,
            bundle.ToolCallLog.Count);
    }
}