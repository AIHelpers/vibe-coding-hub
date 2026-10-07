using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

public class CharacterResolverTests
{
    private static readonly AgentRolePreset Planner = CharacterRegistryTests.Presets[0];

    private static CharacterInfo Alex(Action<CharacterInfoBuilder>? configure = null)
    {
        var b = new CharacterInfoBuilder();
        configure?.Invoke(b);
        return b.Build();
    }

    internal sealed class CharacterInfoBuilder
    {
        public CharacterInfo Value = new()
        {
            Id = "alex",
            DisplayName = "Alex",
            Description = "Architect",
            BaseRole = "planner",
            Persona = "You are Alex.",
            Skills = new[] { "adr-writer" },
            PinnedSkills = new[] { "conventions" },
            Scope = SkillScope.Global
        };
        public CharacterInfo Build() => Value;
    }

    [Fact]
    public void Apply_FillsPromptToolsModeSkills()
    {
        var options = CharacterResolver.Apply(new AgentOptions(), Alex(), Planner);

        Assert.Equal("alex", options.CharacterId);
        Assert.Equal("planner", options.Role);
        Assert.StartsWith("You are acting as the character \"Alex\": Architect", options.RoleSystemPrompt);
        Assert.Contains("You are Alex.", options.RoleSystemPrompt);
        Assert.EndsWith("PLANNER-DUTIES", options.RoleSystemPrompt);
        Assert.Equal(new[] { "read_file", "grep" }, options.EnabledTools);
        Assert.Equal(PermissionMode.Plan, options.PermissionMode);
        Assert.Equal(new[] { "adr-writer" }, options.AllowedSkills);
        Assert.Equal(new[] { "conventions" }, options.PinnedSkills);
    }

    [Fact]
    public void Apply_ToolAddRemove_AndOverrides()
    {
        var c = Alex() with
        {
            ToolsAdd = new[] { "web_fetch" },
            ToolsRemove = new[] { "grep" },
            PermissionMode = PermissionMode.Ask,
            Model = "big-model"
        };

        var options = CharacterResolver.Apply(new AgentOptions(), c, Planner);

        Assert.Equal(new[] { "read_file", "web_fetch" }, options.EnabledTools);
        Assert.Contains("grep", options.DisabledTools);
        Assert.Equal(PermissionMode.Ask, options.PermissionMode);
        Assert.Equal("big-model", options.Model);
    }

    [Fact]
    public void Apply_ExplicitToolsReplaceBase()
    {
        var options = CharacterResolver.Apply(new AgentOptions(), Alex() with { Tools = new[] { "git" } }, Planner);
        Assert.Equal(new[] { "git" }, options.EnabledTools);
    }

    [Fact]
    public void Apply_NoBaseRole_MeansAllToolsAndGenericRole()
    {
        var c = Alex() with { BaseRole = null, ToolsRemove = new[] { "execute_command" } };
        var options = CharacterResolver.Apply(new AgentOptions(), c, null);

        Assert.Empty(options.EnabledTools);
        Assert.Contains("execute_command", options.DisabledTools);
        Assert.Equal("alex", options.Role);
        Assert.DoesNotContain("PLANNER-DUTIES", options.RoleSystemPrompt);
    }

    [Fact]
    public void Apply_KeepsExplicitStepValues_UnlessOverwrite()
    {
        var step = new AgentOptions
        {
            RoleSystemPrompt = "STEP PROMPT",
            EnabledTools = new() { "only_this" },
            Model = "step-model",
            AllowedSkills = new() { "step-skill" }
        };

        var kept = CharacterResolver.Apply(step, Alex() with { Model = "char-model" }, Planner);
        Assert.Equal("STEP PROMPT", kept.RoleSystemPrompt);
        Assert.Equal(new[] { "only_this" }, kept.EnabledTools);
        Assert.Equal("step-model", kept.Model);
        Assert.Equal(new[] { "step-skill" }, kept.AllowedSkills);

        var replaced = CharacterResolver.Apply(step, Alex() with { Model = "char-model" }, Planner, overwrite: true);
        Assert.Contains("You are Alex.", replaced.RoleSystemPrompt);
        Assert.Equal("char-model", replaced.Model);
        Assert.Equal(new[] { "adr-writer" }, replaced.AllowedSkills);
    }

    [Fact]
    public void Apply_BuiltInCharacter_KeepsAllSkillsAndPresetPromptOnly()
    {
        var builtIn = CharacterRegistry.FromPreset(Planner);
        var options = CharacterResolver.Apply(new AgentOptions(), builtIn, Planner);

        Assert.Null(options.AllowedSkills);
        Assert.Equal("PLANNER-DUTIES", options.RoleSystemPrompt);
    }
}
