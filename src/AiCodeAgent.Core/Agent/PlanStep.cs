using System.Text.Json.Serialization;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>Status of a single plan step in the autonomous execution loop.</summary>
public enum PlanStepStatus
{
    /// <summary>Step has been planned but not yet started.</summary>
    Pending = 0,
    /// <summary>Step is currently executing (running edits/commands).</summary>
    Running = 1,
    /// <summary>Step is awaiting user approval at the step boundary.</summary>
    AwaitingApproval = 2,
    /// <summary>User approved the step; proceeding to next.</summary>
    Approved = 3,
    /// <summary>User rejected the step; execution halts.</summary>
    Rejected = 4,
    /// <summary>Step failed verification and exhausted retries.</summary>
    Failed = 5,
    /// <summary>Step completed successfully.</summary>
    Completed = 6,
    /// <summary>Step was skipped (e.g. after a replan).</summary>
    Skipped = 7
}

/// <summary>
/// A single step in a user-visible autonomous plan. Each step may touch
/// multiple files and/or run shell commands (build, test, lint).
/// </summary>
public class PlanStep
{
    /// <summary>1-based index for display ("Step 1", "Step 2", ...).</summary>
    public int Index { get; set; }

    /// <summary>Human-readable description of what this step does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Files the step is expected to touch (best-effort hint from planner).</summary>
    public List<string> FilesLikelyTouched { get; set; } = new();

    /// <summary>Optional verification command (build/test/lint) to run after the step.</summary>
    public string? VerifyCommand { get; set; }

    /// <summary>Current status of the step.</summary>
    [JsonIgnore]
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;

    /// <summary>Number of retry attempts so far for this step.</summary>
    [JsonIgnore]
    public int AttemptCount { get; set; }

    /// <summary>Output of the last verification command (stdout/stderr/exit code summary).</summary>
    [JsonIgnore]
    public string? LastVerifyOutput { get; set; }

    /// <summary>Diffs produced by this step (for reviewable display).</summary>
    [JsonIgnore]
    public List<DiffEntry> Diffs { get; set; } = new();

    /// <summary>Free-form note set when Status == Failed (e.g. "build failed after 3 retries").</summary>
    [JsonIgnore]
    public string? FailureReason { get; set; }
}