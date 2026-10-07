using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

/// <summary>The files under <c>examples/</c> must stay valid and wire together.</summary>
public class ExamplesTests
{
    private static string ExamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "examples");
    }

    private static (SkillRegistry Skills, CharacterRegistry Characters, RolePresetLoader Presets) Load()
    {
        var root = ExamplesDir();
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, Path.Combine(root, "skills"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(root, "characters"));
        return (skills, characters, presets);
    }

    [Fact]
    public async Task ExampleSkills_AreValidWithoutWarnings()
    {
        var (skills, _, _) = Load();
        var all = await skills.ListAllAsync();
        Assert.Equal(4, all.Count);
        Assert.All(all, s =>
        {
            Assert.True(s.IsValid, string.Join(" ", s.ValidationErrors));
            Assert.Empty(s.Warnings);
        });
    }

    [Fact]
    public async Task ExampleCharacters_AreValid_AndReferenceExistingSkills()
    {
        var (skills, characters, _) = Load();
        var custom = (await characters.ListAsync()).Where(c => !c.IsBuiltIn).ToList();
        Assert.Equal(new[] { "alex-architect", "quinn-qa", "sam-developer" }, custom.Select(c => c.Id));
        Assert.All(custom, c =>
        {
            Assert.True(c.IsValid, string.Join(" ", c.ValidationErrors));
            Assert.Empty(c.Warnings);
        });
        Assert.Empty(await new SkillMaintenance(skills, characters).FindDanglingReferencesAsync());

        var checks = await SkillDoctor.DiagnoseAsync(skills, characters);
        Assert.DoesNotContain(checks, c => c.Status != DoctorStatus.Ok);
    }

    [Fact]
    public async Task ExamplePipeline_CastsAllThreeCharacters()
    {
        var (_, characters, presets) = Load();
        var json = await File.ReadAllTextAsync(Path.Combine(ExamplesDir(), "pipelines", "team-sdlc.json"));
        var pipeline = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: presets, characters: characters);
        var runner = new SdlcPipelineRunner(coordinator, presets, Substitute.For<IAgentOrchestrator>(), NullLogger<SdlcPipelineRunner>.Instance, characters);
        var plan = runner.BuildPlan(pipeline, "add pagination", "s", "/w");

        Assert.Equal(new[] { "alex-architect", "sam-developer", "quinn-qa" }, plan.Steps.Select(s => s.CharacterId));
        Assert.Equal(PermissionMode.Plan, plan.Steps[0].Options.PermissionMode);
        Assert.Contains("web_fetch", plan.Steps[0].Options.EnabledTools);
        Assert.All(plan.Steps, s => Assert.Contains("team-conventions", s.Options.PinnedSkills));
    }
}
