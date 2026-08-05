using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Copy-on-write checkpointing for file edits.
/// Stores backup copies of files before modification.
/// </summary>
public class CheckpointManager : ICheckpointManager
{
    private readonly ConcurrentDictionary<string, CheckpointEntry> _checkpoints = new();
    private readonly string _checkpointDir;
    private readonly ILogger<CheckpointManager> _logger;

    public CheckpointManager(ILogger<CheckpointManager> logger)
    {
        _logger = logger;
        _checkpointDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent", "checkpoints");
        Directory.CreateDirectory(_checkpointDir);
    }

    public async Task<CheckpointEntry> CreateCheckpointAsync(string filePath, string turnId, string? sessionId = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found for checkpoint: {filePath}");

        var content = await File.ReadAllTextAsync(filePath);
        var fileHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content)));

        var checkpointId = $"{turnId}_{fileHash[..12]}";
        var backupPath = Path.Combine(_checkpointDir, $"{checkpointId}.bak");

        await File.WriteAllTextAsync(backupPath, content);

        var entry = new CheckpointEntry
        {
            CheckpointId = checkpointId,
            FilePath = filePath,
            BackupPath = backupPath,
            OriginalContent = content,
            Timestamp = DateTime.UtcNow,
            TurnId = turnId,
            SessionId = sessionId ?? string.Empty
        };

        _checkpoints[checkpointId] = entry;
        _logger.LogDebug("Created checkpoint {CheckpointId} for {FilePath}", checkpointId, filePath);

        return entry;
    }

    public async Task<bool> RestoreCheckpointAsync(string checkpointId)
    {
        if (!_checkpoints.TryGetValue(checkpointId, out var entry))
            return false;

        try
        {
            await File.WriteAllTextAsync(entry.FilePath, entry.OriginalContent);
            _logger.LogInformation("Restored checkpoint {CheckpointId} for {FilePath}", checkpointId, entry.FilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore checkpoint {CheckpointId}", checkpointId);
            return false;
        }
    }

    public Task<List<CheckpointEntry>> GetCheckpointsAsync(string turnId)
    {
        var entries = _checkpoints.Values
            .Where(e => e.TurnId == turnId)
            .OrderBy(e => e.Timestamp)
            .ToList();
        return Task.FromResult(entries);
    }

    public Task<List<CheckpointEntry>> GetCheckpointsForSessionAsync(string sessionId)
    {
        var entries = _checkpoints.Values
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Timestamp)
            .ToList();
        return Task.FromResult(entries);
    }

    public Task<CheckpointEntry> ImportCheckpointAsync(
        string checkpointId,
        string filePath,
        string originalContent,
        string turnId,
        string? sessionId = null)
    {
        var backupPath = Path.Combine(_checkpointDir, $"{checkpointId}.bak");
        File.WriteAllText(backupPath, originalContent);

        var entry = new CheckpointEntry
        {
            CheckpointId = checkpointId,
            FilePath = filePath,
            BackupPath = backupPath,
            OriginalContent = originalContent,
            Timestamp = DateTime.UtcNow,
            TurnId = turnId,
            SessionId = sessionId ?? string.Empty
        };

        _checkpoints[checkpointId] = entry;
        _logger.LogInformation("Imported checkpoint {CheckpointId} for {FilePath}", checkpointId, filePath);

        return Task.FromResult(entry);
    }

    public Task CleanupAsync(string turnId)
    {
        var keysToRemove = _checkpoints
            .Where(kvp => kvp.Value.TurnId == turnId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_checkpoints.TryRemove(key, out var entry))
            {
                try { File.Delete(entry.BackupPath); } catch { /* Best effort */ }
            }
        }

        return Task.CompletedTask;
    }
}