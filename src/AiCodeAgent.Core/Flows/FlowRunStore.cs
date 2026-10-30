using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Flows;

/// <summary>One tool call a step made.</summary>
public sealed record FlowToolCallRecord(string Name, string? Summary, bool IsError, double DurationMs);

/// <summary>A file change a step produced (the unified diff text of one edit).</summary>
public sealed record FlowDiffRecord(string FilePath, string DiffText);

/// <summary>One run of one step in a saved flow run. The step's answer is in <see cref="OutputFile"/>.</summary>
public sealed record FlowStepRecord
{
    public string NodeId { get; init; } = string.Empty;
    /// <summary>1 = first run of the step, 2+ = loop rounds.</summary>
    public int Round { get; init; } = 1;
    public string? CharacterId { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public FlowNodeState State { get; init; }
    public string? Outcome { get; init; }
    public string? Detail { get; init; }
    /// <summary>The output came from an earlier run (replay) and the step did not run again.</summary>
    public bool Reused { get; init; }
    /// <summary>The full prompt the step received, with its inputs.</summary>
    public string? Prompt { get; init; }
    /// <summary>Steps whose outputs went into the prompt.</summary>
    public List<string> InputFrom { get; init; } = new();
    /// <summary>Markdown file (next to <c>run.json</c>) holding the step's answer.</summary>
    public string? OutputFile { get; init; }
    public List<FlowToolCallRecord> ToolCalls { get; init; } = new();
    public List<FlowDiffRecord> Diffs { get; init; } = new();
    /// <summary>Tokens, cost and duration; null for a reused step or one that never called the model.</summary>
    public FlowStepRunUsage? Usage { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>A line of the run's event log.</summary>
public sealed record FlowLogEntry(DateTimeOffset At, string Kind, string? NodeId, string Text);

/// <summary>Everything saved about one flow run: <c>run.json</c>.</summary>
public sealed record FlowRunRecord
{
    public string Id { get; init; } = string.Empty;
    public string FlowName { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    /// <summary>The session-wide model the run started with (--model / the chat picker), if any.</summary>
    public string? SessionModel { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public FlowRunStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public decimal? BudgetUsd { get; init; }
    public decimal CostUsd { get; init; }
    public int Tokens { get; init; }
    /// <summary>Set when this run is a re-run: the run whose outputs it reused, and the step it restarted from.</summary>
    public string? ReplayOfRunId { get; init; }
    public string? ReplayFromNodeId { get; init; }
    /// <summary>The flow as it was when the run started, so an old run can be opened even after the flow changed.</summary>
    public SdlcPipelineDefinition Definition { get; init; } = new();
    public List<FlowStepRecord> Steps { get; init; } = new();
    public List<FlowLogEntry> Events { get; init; } = new();
    /// <summary>Hash of the last line of <c>audit.jsonl</c> when the run ended: keep it elsewhere to prove the log was not cut or rewritten (E7-S2).</summary>
    public string? AuditHead { get; init; }
    public int? AuditEntries { get; init; }

    [JsonIgnore] public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>A line of the history list.</summary>
public sealed record FlowRunSummary(
    string RunId, string FlowName, string Task, DateTimeOffset StartedAt, TimeSpan Duration, FlowRunStatus Status,
    string Message, decimal CostUsd, int Tokens, string? ReplayOfRunId, string? ReplayFromNodeId);

/// <summary>The outputs a re-run takes over from an earlier run.</summary>
public sealed record FlowReusedStep(string NodeId, string? CharacterId, string Text, string? Outcome);

/// <summary>
/// What a re-run does: <see cref="Reused"/> steps keep their recorded output and do not run, <see cref="NotRun"/> steps
/// were not run in the original either (an unchosen branch), everything else runs again.
/// </summary>
public sealed record FlowReplayPlan(
    string SourceRunId, string FromNodeId,
    IReadOnlyDictionary<string, FlowReusedStep> Reused,
    IReadOnlyCollection<string> NotRun);

/// <summary>
/// Saves every flow run under <c>~/.aiagent/runs/flows/&lt;flow&gt;/&lt;runId&gt;/</c>: <c>run.json</c> (definition snapshot,
/// event log, one record per step run) plus one Markdown file per step output. Keeps the newest N runs per flow.
/// </summary>
public sealed class FlowRunStore
{
    public const string RunFileName = "run.json";
    public const int DefaultRetention = 50;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _root;
    private readonly Func<int> _retention;

    /// <param name="root">Folder that holds one folder per flow; defaults to <see cref="AppPaths.FlowRuns"/>.</param>
    /// <param name="retention">How many runs to keep per flow (0 or less = keep all); read on every save so a settings change applies at once.</param>
    public FlowRunStore(string? root = null, Func<int>? retention = null)
    {
        _root = root ?? AppPaths.FlowRuns;
        _retention = retention ?? (() => DefaultRetention);
    }

    public string Root => _root;

    /// <summary>A sortable id: UTC time plus a short random suffix.</summary>
    public static string NewRunId(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];

    public string FlowDirectory(string flow) => Path.Combine(_root, SafeName(flow));

    public string RunDirectory(string flow, string runId) => Path.Combine(FlowDirectory(flow), SafeName(runId));

    /// <summary>Writes the run (outputs first, <c>run.json</c> last, so a half-written run is never listed) and applies the retention.</summary>
    /// <param name="outputs">Step output files: file name → Markdown text.</param>
    /// <returns>The run's folder.</returns>
    public string Save(FlowRunRecord record, IReadOnlyDictionary<string, string> outputs)
    {
        var dir = RunDirectory(record.FlowName, record.Id);
        Directory.CreateDirectory(dir);
        foreach (var (file, text) in outputs)
            File.WriteAllText(Path.Combine(dir, SafeName(file)), text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, RunFileName), JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false));
        Prune(record.FlowName);
        return dir;
    }

    /// <summary>The runs of a flow, newest first. Folders without a readable <c>run.json</c> are skipped.</summary>
    public IReadOnlyList<FlowRunSummary> List(string flow)
    {
        var result = new List<FlowRunSummary>();
        foreach (var id in RunIds(flow))
        {
            var r = Load(flow, id);
            if (r == null) continue;
            result.Add(new FlowRunSummary(r.Id, r.FlowName, r.Task, r.StartedAt, r.Duration, r.Status, r.Message,
                r.CostUsd, r.Tokens, r.ReplayOfRunId, r.ReplayFromNodeId));
        }
        return result;
    }

    public FlowRunRecord? Load(string flow, string runId)
    {
        var file = Path.Combine(RunDirectory(flow, runId), RunFileName);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<FlowRunRecord>(File.ReadAllText(file), JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Markdown answer of a step run, or null when the file is gone.</summary>
    public string? LoadOutput(string flow, string runId, FlowStepRecord step)
    {
        if (string.IsNullOrEmpty(step.OutputFile)) return null;
        var file = Path.Combine(RunDirectory(flow, runId), SafeName(step.OutputFile));
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (IOException) { return null; }
    }

    public bool Delete(string flow, string runId)
    {
        var dir = RunDirectory(flow, runId);
        if (!Directory.Exists(dir)) return false;
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes the oldest runs beyond the retention. Returns how many were removed.</summary>
    public int Prune(string flow)
    {
        var keep = _retention();
        if (keep <= 0) return 0;
        var removed = 0;
        foreach (var id in RunIds(flow).Skip(keep))
            if (Delete(flow, id)) removed++;
        return removed;
    }

    private IEnumerable<string> RunIds(string flow)
    {
        var dir = FlowDirectory(flow);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        // Ids start with a UTC timestamp, so name order is time order.
        return Directory.EnumerateDirectories(dir)
            .Where(d => File.Exists(Path.Combine(d, RunFileName)))
            .Select(d => Path.GetFileName(d)!)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Plans a re-run from <paramref name="fromNodeId"/>: the step and everything after it run again; every other step
    /// keeps its recorded output. Fails (null plan, message in <paramref name="error"/>) when a step that must be reused
    /// failed in the original run, or the step is not part of the flow.
    /// </summary>
    public FlowReplayPlan? BuildReplay(FlowRunRecord record, string fromNodeId, out string? error)
    {
        error = null;
        var graph = FlowGraph.Build(record.Definition, null);
        var start = graph.Nodes.FirstOrDefault(n => n.Id.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase));
        if (start == null)
        {
            error = $"'{fromNodeId}' is not a step of this flow.";
            return null;
        }

        var again = graph.Descendants(start.Id);
        again.Add(start.Id);
        var reused = new Dictionary<string, FlowReusedStep>(StringComparer.OrdinalIgnoreCase);
        var notRun = new List<string>();
        foreach (var node in graph.Nodes.Where(n => !again.Contains(n.Id)))
        {
            var steps = record.Steps.Where(s => s.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Round).ToList();
            var last = steps.LastOrDefault(s => s.State == FlowNodeState.Done);
            if (last != null && steps.Last().State == FlowNodeState.Done)
            {
                reused[node.Id] = new FlowReusedStep(node.Id, last.CharacterId, LoadOutput(record.FlowName, record.Id, last) ?? string.Empty, last.Outcome);
                continue;
            }
            if (steps.Any(s => s.State == FlowNodeState.Failed))
            {
                error = $"'{node.Id}' failed in that run, so its output cannot be reused. Re-run from '{node.Id}' instead.";
                return null;
            }
            notRun.Add(node.Id); // never ran, was skipped or cancelled: it stays out of this run too
        }
        return new FlowReplayPlan(record.Id, start.Id, reused, notRun);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '#' ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 || s == ".." ? "_" : s;
    }
}

/// <summary>Markdown for the replay view: what one step received, did and produced.</summary>
public static class FlowRunMarkdown
{
    /// <param name="outputOf">Reads a step run's saved answer.</param>
    public static string Step(FlowRunRecord run, string nodeId, Func<FlowStepRecord, string?> outputOf)
    {
        var steps = run.Steps.Where(s => s.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Round).ToList();
        if (steps.Count == 0) return $"_{nodeId} did not run in this flow run._";

        var sb = new StringBuilder();
        foreach (var s in steps)
        {
            sb.Append("## ").Append(nodeId);
            if (steps.Count > 1 || s.Round > 1) sb.Append(" — round ").Append(s.Round);
            if (s.Reused) sb.Append(" (reused)");
            sb.AppendLine().AppendLine();

            var facts = new List<string> { $"**{s.State.ToString().ToLowerInvariant()}**" + (s.Outcome != null ? $" → {s.Outcome}" : "") };
            if (!string.IsNullOrEmpty(s.CharacterId)) facts.Add($"character `{s.CharacterId}`");
            var model = string.IsNullOrEmpty(s.Model) ? s.Usage?.Model : s.Model;
            var provider = string.IsNullOrEmpty(s.Provider) ? s.Usage?.Provider : s.Provider;
            if (s.Usage != null || !string.IsNullOrEmpty(model))
                facts.Add("model `" + (string.IsNullOrEmpty(model) ? "(default)" : string.IsNullOrEmpty(provider) ? model : $"{provider}/{model}") + "`");
            if (s.Usage != null)
                facts.Add($"{FlowUsageFormat.Tokens(s.Usage.TokensIn)} in / {FlowUsageFormat.Tokens(s.Usage.TokensOut)} out · {(s.Usage.Unpriced ? "unpriced" : FlowUsageFormat.Cost(s.Usage.CostUsd))}");
            if (s.Duration > TimeSpan.Zero) facts.Add($"{s.Duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");
            sb.AppendLine(string.Join(" · ", facts));
            if (!string.IsNullOrEmpty(s.Detail) && !s.Reused) sb.AppendLine().AppendLine($"> {s.Detail}");
            sb.AppendLine();

            if (s.Reused)
                sb.AppendLine("_Reused from the original run; the step did not run again._").AppendLine();
            else if (!string.IsNullOrEmpty(s.Prompt))
            {
                sb.AppendLine(s.InputFrom.Count > 0 ? $"### Prompt (inputs from {string.Join(", ", s.InputFrom)})" : "### Prompt").AppendLine();
                sb.AppendLine("````text").AppendLine(s.Prompt).AppendLine("````").AppendLine();
            }

            sb.AppendLine("### Output").AppendLine();
            var output = outputOf(s);
            sb.AppendLine(string.IsNullOrWhiteSpace(output) ? "_(no output)_" : output.Trim()).AppendLine();

            if (s.ToolCalls.Count > 0)
            {
                sb.AppendLine($"### Tool calls ({s.ToolCalls.Count})").AppendLine();
                foreach (var t in s.ToolCalls)
                    sb.AppendLine($"- {(t.IsError ? "✕" : "✓")} `{t.Name}`{(string.IsNullOrEmpty(t.Summary) ? "" : " " + t.Summary)} ({t.DurationMs:0} ms)");
                sb.AppendLine();
            }
            if (s.Diffs.Count > 0)
            {
                sb.AppendLine($"### Changes ({s.Diffs.Count})").AppendLine();
                foreach (var d in s.Diffs)
                    sb.AppendLine($"**{d.FilePath}**").AppendLine().AppendLine("````diff").AppendLine(d.DiffText.TrimEnd()).AppendLine("````").AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }
}
