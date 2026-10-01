using System.Text.Json.Serialization;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Persisted record of an autonomous execution run: the original task,
/// the plan steps, and per-step results. Suitable for after-the-fact review.
/// </summary>
public class ExecutionLog
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "default";
    public string Task { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public List<PlanStep> Steps { get; set; } = new();
    public bool WasCancelled { get; set; }
    public string? FailureSummary { get; set; }

    [JsonIgnore]
    public bool IsRunning => FinishedAt == null && !WasCancelled;
}