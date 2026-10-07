using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
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
    private readonly ICharacterRegistry? _characters;
    private readonly RolePresetLoader? _presets;

    public SpawnSubagentTool(
        ISubagentRunner runner,
        ILogger<SpawnSubagentTool> logger,
        ICharacterRegistry? characters = null,
        RolePresetLoader? presets = null)
        : base(logger)
    {
        _runner = runner;
        _characters = characters;
        _presets = presets;
    }

    /// <summary>"id — description" lines for the valid user + built-in characters (shown in the tool schema).</summary>
    private string CharacterHint()
    {
        if (_characters == null) return string.Empty;
        try
        {
            var list = _characters.ListAsync().GetAwaiter().GetResult().Where(c => c.IsValid && !c.IsTemplate).ToList();
            if (list.Count == 0) return string.Empty;
            return " Available: " + string.Join("; ", list.Select(c =>
                string.IsNullOrWhiteSpace(c.Description) ? c.Id : $"{c.Id} ({Truncate(c.Description, 60)})")) + ".";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Lower rank = more restrictive. A subagent never gets a looser mode than its parent.</summary>
    internal static PermissionMode MostRestrictive(PermissionMode a, PermissionMode b)
    {
        static int Rank(PermissionMode m) => m switch
        {
            PermissionMode.Plan => 0,
            PermissionMode.Ask => 1,
            PermissionMode.AutoEdit => 2,
            _ => 3
        };
        return Rank(a) <= Rank(b) ? a : b;
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
                ["character"] = new() { Type = "string", Description = "Optional character id: the subagent runs as that character, with its persona, tools and skills." + CharacterHint() },
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
        var characterId = GetArg<string>(call, "character")?.Trim() ?? string.Empty;

        var subContext = new SubagentContext
        {
            ParentSessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            MaxIterations = maxIterations,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            EnabledTools = enabledTools.Length == 0 ? null : enabledTools.ToList(),
            Role = string.IsNullOrWhiteSpace(role) ? null : role,
            PermissionMode = context.Permissions?.Mode ?? AiCodeAgent.Core.Models.PermissionMode.Plan,
            IsReadOnly = context.IsReadOnly,
            AllowedPaths = context.AllowedPaths.ToList(),
            RoleSystemPrompt = string.IsNullOrWhiteSpace(role) ? null : role,
            // Without a character the subagent inherits the parent's skills.
            AllowedSkills = context.AllowedSkills?.ToList(),
        };

        if (!string.IsNullOrEmpty(characterId))
        {
            var applied = await ApplyCharacterAsync(subContext, characterId, enabledTools.Length > 0, string.IsNullOrWhiteSpace(model)).ConfigureAwait(false);
            if (applied.Error != null) return Error(applied.Error);
            subContext = applied.Context!;
        }

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

    private async Task<(SubagentContext? Context, string? Error)> ApplyCharacterAsync(
        SubagentContext subContext, string characterId, bool toolsGiven, bool modelFromCharacter)
    {
        if (_characters == null)
            return (null, "Characters are not available in this session.");
        var character = await _characters.GetAsync(characterId).ConfigureAwait(false);
        if (character == null)
        {
            var known = (await _characters.ListAsync().ConfigureAwait(false)).Select(c => c.Id);
            return (null, $"Unknown character '{characterId}'. Available: {string.Join(", ", known)}.");
        }
        if (!character.IsValid)
            return (null, $"Character '{characterId}' is invalid: {string.Join(" ", character.ValidationErrors)}");

        var basePreset = character.BaseRole == null ? null : _presets?.GetPreset(character.BaseRole);
        var template = new AgentOptions
        {
            EnabledTools = subContext.EnabledTools ?? new(),
            RoleSystemPrompt = subContext.RoleSystemPrompt,
            Model = subContext.Model,
            PermissionMode = subContext.PermissionMode
        };
        var resolved = CharacterResolver.Apply(template, character, basePreset, overwrite: false, useBaseRolePermission: true);

        return (subContext with
        {
            CharacterId = character.Id,
            Role = character.BaseRole ?? character.Id,
            RoleSystemPrompt = resolved.RoleSystemPrompt,
            EnabledTools = toolsGiven ? subContext.EnabledTools : (resolved.EnabledTools.Count == 0 ? null : resolved.EnabledTools),
            DisabledTools = resolved.DisabledTools,
            Model = modelFromCharacter ? resolved.Model : subContext.Model,
            // The character's skills replace the parent's; built-ins (null) keep the parent's restriction.
            AllowedSkills = character.Skills == null ? subContext.AllowedSkills : character.Skills.ToList(),
            PinnedSkills = resolved.PinnedSkills,
            // Never looser than the parent run.
            PermissionMode = MostRestrictive(subContext.PermissionMode, resolved.PermissionMode)
        }, null);
    }
}
