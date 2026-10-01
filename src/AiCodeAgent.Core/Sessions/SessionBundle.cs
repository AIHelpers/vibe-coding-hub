using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// A recorded tool-call log entry — a flattened Start/End pair emitted on the
/// agent event bus. Carries optional agent/role attribution for multi-agent sessions.
/// </summary>
public sealed class ToolCallLogEntryDto
{
    public string ToolCallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public Dictionary<string, object?>? Arguments { get; init; }
    public string? Output { get; init; }
    public bool IsError { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? AgentId { get; init; }
    public string? Role { get; init; }
}

/// <summary>A serializable snapshot of a checkpoint (embeds the original file content).</summary>
public sealed class CheckpointSnapshotDto
{
    public string CheckpointId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string TurnId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

/// <summary>Serialized form of <see cref="SharedChangeset"/> (entries + hunks).</summary>
public sealed class ChangesetBundleDto
{
    public List<DiffEntry> Entries { get; init; } = new();
    public List<DiffHunk> Hunks { get; init; } = new();
}

/// <summary>
/// Top-level session bundle — schema v1 (see docs/plan/05-priority-session-export-import.md).
/// </summary>
public sealed class SessionBundle
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Existing message log format (<see cref="Message"/> records as stored by context managers).</summary>
    public List<Message> Conversation { get; set; } = new();

    /// <summary>ToolCallStart/End pairs flattened into single entries.</summary>
    public List<ToolCallLogEntryDto> ToolCallLog { get; set; } = new();

    /// <summary>SharedChangeset serialized, with per-hunk agent attribution.</summary>
    public ChangesetBundleDto? Changeset { get; set; }

    /// <summary>Checkpoint SHA refs (same-machine resume).</summary>
    public List<string> CheckpointRefs { get; set; } = new();

    /// <summary>Embedded checkpoint snapshots (cross-machine handoff).</summary>
    public List<CheckpointSnapshotDto> CheckpointSnapshots { get; set; } = new();

    /// <summary>Roles used in the session (e.g. planner, implementer, reviewer).</summary>
    public List<string> RolesUsed { get; set; } = new();

    /// <summary>Optional prompt pack embedding the role presets + reusable templates (see 5.4).</summary>
    public PromptPack? PromptPack { get; set; }
}
