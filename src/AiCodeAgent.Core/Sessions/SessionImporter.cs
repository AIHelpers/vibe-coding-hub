using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>Result of a session import operation.</summary>
public sealed class SessionImportResult
{
    public string SessionId { get; init; } = string.Empty;
    public int MessageCount { get; init; }
    public int ToolCallCount { get; init; }
    public int HunkCount { get; init; }
    public int CheckpointCount { get; init; }
    public List<string> RolesUsed { get; init; } = new();
    public List<ImportConflict> Conflicts { get; init; } = new();
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>A conflict detected when importing a session into a diverged repo.</summary>
public sealed class ImportConflict
{
    public string FilePath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ConflictKind Kind { get; init; }
}

public enum ConflictKind
{
    /// <summary>The file no longer exists in the target repo.</summary>
    FileMissing,
    /// <summary>The file content has diverged from the checkpoint ref (hash mismatch).</summary>
    ContentDiverged,
    /// <summary>The checkpoint ref could not be matched to a local checkpoint.</summary>
    CheckpointRefMissing
}

/// <summary>
/// Rehydrates a <see cref="SessionBundle"/> into a new session: restores the
/// conversation into the context manager, restores the changeset into
/// <see cref="SharedChangeset"/>, and optionally re-applies checkpoints when
/// the target repo state matches (hash-check against checkpoint refs).
/// </summary>
public class SessionImporter
{
    private readonly IContextManager _contextManager;
    private readonly ICheckpointManager _checkpointManager;
    private readonly ILogger<SessionImporter> _logger;

    public SessionImporter(
        IContextManager contextManager,
        ICheckpointManager checkpointManager,
        ILogger<SessionImporter> logger)
    {
        _contextManager = contextManager;
        _checkpointManager = checkpointManager;
        _logger = logger;
    }

    /// <summary>
    /// Import a session bundle from a file. Supports both plain JSON and
    /// `.agentsession` zip bundles.
    /// </summary>
    public async Task<SessionImportResult> ImportAsync(
        string bundlePath,
        string? newSessionId = null,
        bool applyCheckpoints = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var bundle = await LoadBundleAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        return await ImportAsync(bundle, newSessionId, applyCheckpoints, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Import an in-memory session bundle.</summary>
    public async Task<SessionImportResult> ImportAsync(
        SessionBundle bundle,
        string? newSessionId = null,
        bool applyCheckpoints = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        SessionExporter.ValidateSchema(bundle);

        var sessionId = string.IsNullOrEmpty(newSessionId)
            ? bundle.SessionId
            : newSessionId;
        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString();

        var conflicts = new List<ImportConflict>();

        // 1. Rehydrate conversation into the context manager
        foreach (var message in bundle.Conversation)
        {
            await _contextManager.AddMessageAsync(sessionId, message).ConfigureAwait(false);
        }

        // 2. Restore changeset into SharedChangeset (as Pending/Accepted per original hunk status)
        var hunks = new List<DiffHunk>();
        if (bundle.Changeset != null)
        {
            foreach (var hunk in bundle.Changeset.Hunks)
            {
                hunks.Add(hunk);
            }
        }

        // 3. Optionally re-apply checkpoints if the target repo state matches
        var checkpointCount = 0;
        if (applyCheckpoints)
        {
            foreach (var snapshot in bundle.CheckpointSnapshots)
            {
                var conflict = await TryApplyCheckpointAsync(snapshot, sessionId, cancellationToken).ConfigureAwait(false);
                if (conflict != null)
                {
                    conflicts.Add(conflict);
                }
                else
                {
                    checkpointCount++;
                }
            }

            // Checkpoint refs that have no embedded snapshot are only usable
            // on the same machine — surface a warning if they can't be matched.
            foreach (var refId in bundle.CheckpointRefs)
            {
                if (bundle.CheckpointSnapshots.Any(s => s.CheckpointId == refId))
                    continue;

                var existing = await _checkpointManager.GetCheckpointsForSessionAsync(bundle.SessionId).ConfigureAwait(false);
                if (!existing.Any(c => c.CheckpointId == refId))
                {
                    conflicts.Add(new ImportConflict
                    {
                        FilePath = string.Empty,
                        Kind = ConflictKind.CheckpointRefMissing,
                        Message = $"Checkpoint ref '{refId}' has no embedded snapshot and could not be " +
                                  "matched to a local checkpoint. It will not be restorable on this machine."
                    });
                }
            }
        }

        return new SessionImportResult
        {
            SessionId = sessionId,
            MessageCount = bundle.Conversation.Count,
            ToolCallCount = bundle.ToolCallLog.Count,
            HunkCount = hunks.Count,
            CheckpointCount = checkpointCount,
            RolesUsed = bundle.RolesUsed,
            Conflicts = conflicts
        };
    }

    /// <summary>Load a bundle from a file (JSON or `.agentsession` zip).</summary>
    public static async Task<SessionBundle> LoadBundleAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(bundlePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Session bundle not found: {fullPath}");

        if (fullPath.EndsWith(".agentsession", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadZipBundleAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return SessionExporter.FromJson(json);
    }

    /// <summary>Restore a bundle's changeset into a <see cref="SharedChangeset"/>.</summary>
    public static void RestoreChangeset(SessionBundle bundle, SharedChangeset changeset)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(changeset);

        if (bundle.Changeset == null)
            return;

        changeset.AddRange(bundle.Changeset.Entries);
        changeset.AddHunks(bundle.Changeset.Hunks);
    }

    private async Task<ImportConflict?> TryApplyCheckpointAsync(
        CheckpointSnapshotDto snapshot,
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(snapshot.FilePath);

        // File must exist in the target repo
        if (!File.Exists(fullPath))
        {
            return new ImportConflict
            {
                FilePath = snapshot.FilePath,
                Kind = ConflictKind.FileMissing,
                Message = $"File '{snapshot.FilePath}' does not exist in the target repo. " +
                          "Checkpoint will not be applied."
            };
        }

        // Hash-check against the checkpoint ref: the current file content must
        // match the original content the checkpoint was created from.
        var currentContent = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var currentHash = ComputeSha256(currentContent);
        var originalHash = ComputeSha256(snapshot.OriginalContent);

        if (!string.Equals(currentHash, originalHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ImportConflict
            {
                FilePath = snapshot.FilePath,
                Kind = ConflictKind.ContentDiverged,
                Message = $"File '{snapshot.FilePath}' has diverged from the checkpoint ref. " +
                          "Applying the stale diff could overwrite newer changes. Checkpoint will not be applied."
            };
        }

        // Repo state matches — import the checkpoint so it can be restored later
        await _checkpointManager.ImportCheckpointAsync(
            snapshot.CheckpointId,
            snapshot.FilePath,
            snapshot.OriginalContent,
            snapshot.TurnId,
            sessionId).ConfigureAwait(false);

        return null;
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static async Task<SessionBundle> LoadZipBundleAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

        var jsonEntry = archive.GetEntry(SessionExporter.BundleEntryName)
            ?? throw new InvalidDataException(
                $"Invalid .agentsession bundle: missing '{SessionExporter.BundleEntryName}' entry.");

        SessionBundle bundle;
        await using (var entryStream = jsonEntry.Open())
        {
            bundle = await JsonSerializer.DeserializeAsync<SessionBundle>(entryStream, JsonOptions.Default, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Session bundle JSON is empty or invalid.");
        }
        SessionExporter.ValidateSchema(bundle);

        // Load checkpoint metadata from the sidecar (if present)
        var meta = new List<CheckpointSnapshotDto>();
        var metaEntry = archive.GetEntry(SessionExporter.CheckpointsMetaEntryName);
        if (metaEntry != null)
        {
            await using var metaStream = metaEntry.Open();
            meta = await JsonSerializer.DeserializeAsync<List<CheckpointSnapshotDto>>(
                    metaStream, JsonOptions.Default, cancellationToken)
                .ConfigureAwait(false) ?? new List<CheckpointSnapshotDto>();
        }

        // Rehydrate embedded checkpoint snapshots from the checkpoints/ directory
        var snapshots = new List<CheckpointSnapshotDto>();
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("checkpoints/", StringComparison.OrdinalIgnoreCase)))
        {
            var checkpointId = Path.GetFileNameWithoutExtension(entry.FullName);
            string content;
            await using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var existing = meta.FirstOrDefault(s => s.CheckpointId == checkpointId);
            if (existing != null)
            {
                snapshots.Add(new CheckpointSnapshotDto
                {
                    CheckpointId = existing.CheckpointId,
                    FilePath = existing.FilePath,
                    OriginalContent = content,
                    Timestamp = existing.Timestamp,
                    TurnId = existing.TurnId,
                    SessionId = existing.SessionId
                });
            }
            else
            {
                snapshots.Add(new CheckpointSnapshotDto
                {
                    CheckpointId = checkpointId,
                    FilePath = string.Empty,
                    OriginalContent = content,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        // Also include any metadata entries whose content files were not embedded
        // (e.g. older bundles) — preserving what we know.
        foreach (var metaSnapshot in meta.Where(m => !snapshots.Any(s => s.CheckpointId == m.CheckpointId)))
        {
            snapshots.Add(metaSnapshot);
        }

        if (snapshots.Count > 0)
        {
            bundle.CheckpointSnapshots = snapshots;
        }

        return bundle;
    }
}