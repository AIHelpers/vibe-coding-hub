using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Skills;

/// <summary>
/// Loads a skill's full instructions into the conversation. This is how the
/// model acts on the <c>&lt;available_skills&gt;</c> list: descriptions are cheap
/// and always present, the instructions only cost context once used.
/// Enforces the calling agent's allowed skills (<see cref="AgentExecutionContext.AllowedSkills"/>),
/// so a character can only load the skills assigned to it.
/// </summary>
public class UseSkillTool : BaseTool
{
    private readonly ISkillRegistry _skills;

    public UseSkillTool(ISkillRegistry skills, ILogger<UseSkillTool> logger) : base(logger) => _skills = skills;

    public override string Name => AgentOrchestrator.UseSkillToolName;

    public override string Description =>
        "Load the full instructions of a skill listed in <available_skills>. Call it as soon as a task matches a " +
        "skill's description, then follow the returned instructions. Optional 'args' passes context to the skill.";

    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["name"] = new() { Type = "string", Description = "Skill name exactly as listed in <available_skills>" },
                ["args"] = new() { Type = "string", Description = "Optional arguments or context for the skill" }
            },
            Required = ["name"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var name = GetArg<string>(call, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Error("'name' is required.");
        var args = GetArg<string>(call, "args");

        var visible = await _skills.ListAsync(context.CancellationToken).ConfigureAwait(false);
        var allowed = visible
            .Where(s => context.AllowedSkills is null || context.AllowedSkills.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();

        var match = allowed.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var exists = await _skills.GetAsync(name, context.CancellationToken).ConfigureAwait(false) is not null;
            var why = exists
                ? $"Skill '{name}' is not available to this agent."
                : $"Skill '{name}' does not exist.";
            var list = allowed.Count == 0 ? "none" : string.Join(", ", allowed);
            return Error($"{why} Available skills: {list}.");
        }

        var invocation = await _skills.InvokeAsync(match, args, context.CancellationToken).ConfigureAwait(false);
        if (invocation is null || string.IsNullOrWhiteSpace(invocation.Content))
            return Error($"Skill '{match}' could not be loaded (empty or unreadable file).");

        Logger.LogInformation("Agent {Agent} loaded skill {Skill}", context.AgentId ?? "main", match);
        return Success(invocation.ToPromptBlock(), new { skill = match });
    }
}
