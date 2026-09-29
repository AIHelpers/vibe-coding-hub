using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Planning;

public sealed class TodoItem
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
    /// <summary>pending | in_progress | completed</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";
}

/// <summary>Per-run task list storage (in memory, keyed by session + agent).</summary>
public static class TodoStore
{
    private static readonly ConcurrentDictionary<string, List<TodoItem>> Lists = new();

    public static string Key(AgentExecutionContext context) => $"{context.SessionId}::{context.AgentId}";

    public static void Set(string key, List<TodoItem> items) => Lists[key] = items;

    public static IReadOnlyList<TodoItem> Get(string key) =>
        Lists.TryGetValue(key, out var items) ? items : Array.Empty<TodoItem>();

    public static void Clear(string key) => Lists.TryRemove(key, out _);

    public static string Render(IReadOnlyList<TodoItem> items)
    {
        var sb = new StringBuilder();
        foreach (var t in items)
        {
            var mark = t.Status switch { "completed" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            sb.AppendLine($"{mark} {t.Content}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The model's working checklist for multi-step tasks. Each call REPLACES the whole list, so the model always
/// sends the full current state. Shown back verbatim so it stays in context after compaction.
/// </summary>
public class TodoWriteTool : BaseTool
{
    private static readonly string[] Statuses = ["pending", "in_progress", "completed"];

    public TodoWriteTool(ILogger<TodoWriteTool> logger) : base(logger) { }

    public override string Name => "todo_write";
    public override string Description =>
        "Create and update a checklist for a multi-step task. Send the COMPLETE list every time (it replaces the previous one). " +
        "Keep exactly one item 'in_progress'; mark items 'completed' as soon as they are done. Use for tasks with 3+ steps.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["todos"] = new()
                {
                    Type = "array",
                    Description = "The full task list",
                    Items = new PropertySchema
                    {
                        Type = "object",
                        Properties = new()
                        {
                            ["content"] = new() { Type = "string", Description = "Imperative description of the task" },
                            ["status"] = new() { Type = "string", Description = "Task state", Enum = ["pending", "in_progress", "completed"] }
                        },
                        Required = ["content", "status"]
                    }
                }
            },
            Required = ["todos"]
        }
    };

    public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        List<TodoItem>? items;
        try { items = GetArg<List<TodoItem>>(call, "todos"); }
        catch (Exception ex) { return Task.FromResult(Error($"Invalid 'todos': {ex.Message}")); }

        if (items == null)
            return Task.FromResult(Error("'todos' is required and must be an array of {content, status}."));

        foreach (var t in items)
        {
            t.Status = (t.Status ?? "pending").Trim().ToLowerInvariant();
            if (!Statuses.Contains(t.Status))
                return Task.FromResult(Error($"Invalid status '{t.Status}' for '{t.Content}'. Use pending, in_progress or completed."));
            if (string.IsNullOrWhiteSpace(t.Content))
                return Task.FromResult(Error("Every todo needs non-empty 'content'."));
        }

        TodoStore.Set(TodoStore.Key(context), items);

        var inProgress = items.Count(t => t.Status == "in_progress");
        var note = inProgress > 1 ? "\nNote: more than one item is in_progress; work on one at a time." : string.Empty;
        return Task.FromResult(Success($"Todo list updated ({items.Count(t => t.Status == "completed")}/{items.Count} done):\n{TodoStore.Render(items)}{note}", items));
    }
}
