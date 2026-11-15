using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Planning;

/// <summary>One mark the model makes on a declared criterion.</summary>
public sealed class AcceptanceMarkArg
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>met | not_met</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "met";
    [JsonPropertyName("tests")] public List<string>? Tests { get; set; }
    [JsonPropertyName("files")] public List<string>? Files { get; set; }
    [JsonPropertyName("commands")] public List<string>? Commands { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>
/// The criteria of a run and the last mark on each, kept in memory per session so every agent of a flow sees the same list
/// (the audit trail, not this store, is what the evidence pack is built from).
/// </summary>
public static class AcceptanceStore
{
    public sealed record Criterion(string Id, string Text);

    private sealed class State
    {
        public readonly List<Criterion> Criteria = new();
        public readonly Dictionary<string, AcceptanceMarkArg> Marks = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, State> Sessions = new();

    public static void Declare(string sessionId, IReadOnlyList<string> texts)
    {
        var state = Sessions.GetOrAdd(sessionId, _ => new State());
        lock (state)
        {
            state.Criteria.Clear();
            state.Marks.Clear();
            for (var i = 0; i < texts.Count; i++) state.Criteria.Add(new Criterion("AC" + (i + 1), texts[i]));
        }
    }

    public static IReadOnlyList<Criterion> Criteria(string sessionId)
    {
        if (!Sessions.TryGetValue(sessionId, out var state)) return Array.Empty<Criterion>();
        lock (state) return state.Criteria.ToList();
    }

    /// <summary>The canonical id ("AC2") for what the model sent ("ac2"), or null when there is no such criterion.</summary>
    public static string? Resolve(string sessionId, string id) =>
        Criteria(sessionId).FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    public static void Mark(string sessionId, string canonicalId, AcceptanceMarkArg mark)
    {
        if (!Sessions.TryGetValue(sessionId, out var state)) return;
        lock (state) state.Marks[canonicalId] = mark;
    }

    public static AcceptanceMarkArg? MarkOf(string sessionId, string id)
    {
        if (!Sessions.TryGetValue(sessionId, out var state)) return null;
        lock (state) return state.Marks.TryGetValue(id, out var m) ? m : null;
    }

    public static void Clear(string sessionId) => Sessions.TryRemove(sessionId, out _);

    public static string Render(string sessionId)
    {
        var sb = new StringBuilder();
        foreach (var c in Criteria(sessionId))
        {
            var mark = MarkOf(sessionId, c.Id);
            var state = mark == null ? "[ ] not marked" : mark.Status == "not_met" ? "[!] not met" : "[x] met";
            sb.AppendLine($"{c.Id} {state}: {c.Text}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// E10-S2: the agent states what "done" means before it codes, and at the end marks each criterion with the test, file or
/// command that proves it. The marks are claims: the evidence pack checks every proof against the audit trail and shows
/// unconfirmed criteria as unproven.
/// </summary>
public class AcceptanceCriteriaTool : BaseTool
{
    public const string ToolName = "acceptance_criteria";
    public const int MaxCriteria = 12;
    public const int MaxCriterionChars = 300;

    public AcceptanceCriteriaTool(ILogger<AcceptanceCriteriaTool> logger) : base(logger) { }

    public override string Name => ToolName;
    public override string Description =>
        "Acceptance criteria for a task that changes code. BEFORE you edit anything call action 'declare' with 2-8 short, checkable " +
        "criteria (observable behaviour, not implementation steps); it replaces any earlier list and numbers them AC1, AC2… " +
        "AFTER your last edit and a passing test or build run, call action 'mark' once with one entry per criterion: status 'met' " +
        "or 'not_met', and the proof: 'tests' (test names, as printed by the runner), 'files' (paths you changed), 'commands' " +
        "(e.g. 'dotnet test'). A reviewer sees every criterion as proven only when the audit trail confirms a proof; a mark " +
        "without confirmable proof is shown as 'claimed', so cite what actually ran. Say 'not_met' honestly. Action 'show' lists the current state.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["action"] = new() { Type = "string", Description = "What to do", Enum = ["declare", "mark", "show"] },
                ["criteria"] = new()
                {
                    Type = "array",
                    Description = "For 'declare': the criteria, one checkable statement each",
                    Items = new PropertySchema { Type = "string", Description = "One acceptance criterion" }
                },
                ["marks"] = new()
                {
                    Type = "array",
                    Description = "For 'mark': one entry per criterion you are reporting on",
                    Items = new PropertySchema
                    {
                        Type = "object",
                        Properties = new()
                        {
                            ["id"] = new() { Type = "string", Description = "AC1, AC2…" },
                            ["status"] = new() { Type = "string", Description = "met or not_met", Enum = ["met", "not_met"] },
                            ["tests"] = new() { Type = "array", Description = "Test names that prove it", Items = new PropertySchema { Type = "string", Description = "A test name or part of it" } },
                            ["files"] = new() { Type = "array", Description = "Changed files that prove it", Items = new PropertySchema { Type = "string", Description = "A file path" } },
                            ["commands"] = new() { Type = "array", Description = "Commands that prove it", Items = new PropertySchema { Type = "string", Description = "A command or part of it" } },
                            ["note"] = new() { Type = "string", Description = "A short remark; never counted as proof" }
                        },
                        Required = ["id", "status"]
                    }
                }
            },
            Required = ["action"]
        }
    };

    public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var action = GetArg<string>(call, "action")?.Trim().ToLowerInvariant();
        var result = action switch
        {
            "declare" => Declare(call, context),
            "mark" => Mark(call, context),
            "show" => Show(context),
            _ => Error("'action' must be declare, mark or show.")
        };
        return Task.FromResult(result);
    }

    private ToolResult Declare(ToolCall call, AgentExecutionContext context)
    {
        List<string>? raw;
        try { raw = GetArg<List<string>>(call, "criteria"); }
        catch (Exception ex) { return Error($"Invalid 'criteria': {ex.Message}"); }

        var texts = (raw ?? new List<string>()).Select(t => (t ?? string.Empty).Trim()).ToList();
        if (texts.Count == 0) return Error("'criteria' is required: a list of at least one checkable statement.");
        if (texts.Any(t => t.Length == 0)) return Error("Every criterion needs non-empty text.");
        if (texts.Count > MaxCriteria) return Error($"At most {MaxCriteria} criteria; merge or drop the least important.");
        if (texts.Any(t => t.Length > MaxCriterionChars)) return Error($"Keep each criterion under {MaxCriterionChars} characters.");

        AcceptanceStore.Declare(context.SessionId, texts);
        return Success($"Declared {texts.Count} acceptance criteria:\n{AcceptanceStore.Render(context.SessionId)}\n" +
                       "Implement the task, run the tests, then call 'mark' with the proof for each.");
    }

    private ToolResult Mark(ToolCall call, AgentExecutionContext context)
    {
        if (AcceptanceStore.Criteria(context.SessionId).Count == 0)
            return Error("No criteria declared yet. Call action 'declare' first.");

        List<AcceptanceMarkArg>? marks;
        try { marks = GetArg<List<AcceptanceMarkArg>>(call, "marks"); }
        catch (Exception ex) { return Error($"Invalid 'marks': {ex.Message}"); }
        if (marks == null || marks.Count == 0) return Error("'marks' is required: one entry per criterion {id, status, tests/files/commands}.");

        // Validate everything first so a bad call changes nothing (the evidence pack replays only successful calls).
        var resolved = new List<(string Id, AcceptanceMarkArg Mark)>();
        foreach (var m in marks)
        {
            var id = AcceptanceStore.Resolve(context.SessionId, m.Id ?? string.Empty);
            if (id == null) return Error($"Unknown criterion '{m.Id}'. Known: {string.Join(", ", AcceptanceStore.Criteria(context.SessionId).Select(c => c.Id))}.");
            var status = (m.Status ?? string.Empty).Trim().ToLowerInvariant();
            if (status is not ("met" or "not_met")) return Error($"Invalid status '{m.Status}' for {id}. Use met or not_met.");
            m.Status = status;
            resolved.Add((id, m));
        }
        foreach (var (id, m) in resolved) AcceptanceStore.Mark(context.SessionId, id, m);

        var warnings = new StringBuilder();
        foreach (var (id, m) in resolved)
        {
            var hasProof = (m.Tests?.Count ?? 0) + (m.Files?.Count ?? 0) + (m.Commands?.Count ?? 0) > 0;
            if (m.Status == "met" && !hasProof)
                warnings.AppendLine($"{id} is marked met without proof; it will be shown as claimed. Cite tests, files or commands.");
        }
        var unmarked = AcceptanceStore.Criteria(context.SessionId).Where(c => AcceptanceStore.MarkOf(context.SessionId, c.Id) == null).Select(c => c.Id).ToList();
        if (unmarked.Count > 0) warnings.AppendLine($"Not marked yet: {string.Join(", ", unmarked)}; they will be shown as unproven.");

        return Success($"Marked {resolved.Count} criteria:\n{AcceptanceStore.Render(context.SessionId)}" +
                       (warnings.Length > 0 ? "\n" + warnings.ToString().TrimEnd() : string.Empty));
    }

    private ToolResult Show(AgentExecutionContext context)
    {
        var text = AcceptanceStore.Render(context.SessionId);
        return text.Length == 0 ? Success("No acceptance criteria declared in this session.") : Success(text);
    }
}
