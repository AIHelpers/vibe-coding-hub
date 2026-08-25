using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Agent;

/// <summary>
/// Tool that spawns a subagent to work on a sub-task. The subagent runs in an
/// isolated session with its own context window and tool set. Its final output
/// is returned to the parent as the tool result.
/// </summary>
public class SpawnSubagentTool : BaseTool
{
    private readonly ISubagentRunner _runner;

    public SpawnSubagentTool(ISubagentRunner runner, ILogger<SpawnSubagentTool> logger)
        : base(logger)
    {
        _runner = runner;
    }

    public override string Name => "spawn_subagent";

    public override string Description =>
        "Spawn a subagent that works on a sub-task in an isolated session. " +
        "Use for focused, scoped work such as 'find all usages of X' or " +
        "'generate tests for Y'. Returns the subagent's final answer.";

    public override RiskLevel Risk => RiskLevel.Write;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["task"] = new() { Type = "string", Description = "The task to delegate to the subagent." },
                ["fork"] = new() { Type = "boolean", Description = "If true, fork the parent's context into the subagent." },
                ["model"] = new() { Type = "string", Description = "Optional model override for the subagent." },
                ["maxIterations"] = new() { Type = "integer", Description = "Max tool iterations the subagent may perform." },
                ["timeoutSeconds"] = new() { Type = "integer", Description = "Subagent timeout in seconds." },
                ["enabledTools"] = new() { Type = "array", Description = "Optional list of tool names the subagent may use." },
                ["role"] = new() { Type = "string", Description = "Optional role prompt for the subagent." },
            },
            Required = ["task"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var task = GetArg<string>(call, "task") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(task))
            return Error("A non-empty 'task' argument is required.");

        var fork = GetArg(call, "fork", false);
        var model = GetArg<string>(call, "model") ?? string.Empty;
        var maxIterations = GetArg(call, "maxIterations", 12);
        var timeoutSeconds = GetArg(call, "timeoutSeconds", 600);
        var enabledTools = GetArg(call, "enabledTools", Array.Empty<string>());
        var role = GetArg<string>(call, "role") ?? string.Empty;

        var subContext = new SubagentContext
        {
            ParentSessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            MaxIterations = maxIterations,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            EnabledTools = enabledTools.Length == 0 ? null : enabledTools.ToList(),
            Role = string.IsNullOrWhiteSpace(role) ? null : role,
        };

        try
        {
            var summary = fork
                ? await _runner.ForkAsync(task, subContext).ConfigureAwait(false)
                : await _runner.RunAsync(task, subContext).ConfigureAwait(false);

            var payload = new
            {
                summary.SubagentId,
                summary.Forked,
                summary.ToolCallCount,
                summary.Duration,
                summary.WasCancelled,
                summary.Error,
                Content = summary.Content,
            };

            return Success(summary.Content ?? string.Empty, payload);
        }
        catch (OperationCanceledException)
        {
            return Error("Subagent was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SpawnSubagent failed");
            return Error($"Subagent failed: {ex.Message}");
        }
    }
}