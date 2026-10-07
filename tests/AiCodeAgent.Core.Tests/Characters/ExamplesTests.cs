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
        Assert.Equal(8, all.Count);
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
        Assert.Equal(new[] { "alex-architect", "dana-dotnet", "mia-ml", "quinn-qa", "sam-developer", "software-developer" }, custom.Select(c => c.Id));
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
    public async Task ExampleDevelopers_ShareTemplate_WithTheirOwnSkills()
    {
        var (_, characters, _) = Load();

        var template = (await characters.GetAsync("software-developer"))!;
        Assert.True(template.IsTemplate);

        var dana = (await characters.GetAsync("dana-dotnet"))!;
        Assert.Equal(new[] { "release-notes", "csharp", "clean-architecture" }, dana.Skills);
        Assert.Equal(new[] { "csharp", "clean-architecture" }, dana.OwnSkills);
        Assert.Equal(new[] { "team-conventions" }, dana.PinnedSkills);
        Assert.Equal("implementer", dana.BaseRole);
        Assert.StartsWith("You are a professional software developer.", dana.Persona);

        var mia = (await characters.GetAsync("mia-ml"))!;
        Assert.Equal(new[] { "python", "machine-learning" }, mia.Skills);
        Assert.Equal("own", mia.SkillSources["python"]);

        var sam = (await characters.GetAsync("sam-developer"))!;
        Assert.Equal(new[] { "release-notes" }, sam.Skills);
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

    [Fact]
    public async Task ExampleFlow_IsValid_AndRunsWithParallelBranchesAndReviewLoop()
    {
        var (_, characters, presets) = Load();
        var json = await File.ReadAllTextAsync(Path.Combine(ExamplesDir(), "pipelines", "feature-team.json"));
        var flow = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;
        Assert.True(flow.IsFlow);

        var graph = AiCodeAgent.Core.Flows.FlowGraph.Build(flow, id => characters.GetAsync(id).GetAwaiter().GetResult());
        Assert.True(graph.IsValid, string.Join("\n", graph.Problems));
        Assert.Empty(graph.Problems);
        Assert.Equal("1: design · 2: backend ∥ model · 3: review · 4: release-notes", graph.DescribeWaves());

        // Quinn rejects once, then approves.
        var orchestrator = new AiCodeAgent.Core.Tests.Flows.ScriptedOrchestrator((node, _, call) =>
            node == "review" ? call == 1 ? "Add tests.\nOUTCOME: rejected" : "OUTCOME: approved" : null)
        {
            MeetUp = new HashSet<string> { "backend", "model" }
        };
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: presets, characters: characters);
        var runner = new SdlcPipelineRunner(coordinator, presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, characters);
        AiCodeAgent.Core.Flows.FlowRunResult? result = null;
        await foreach (var e in runner.RunAsync(flow, "add churn prediction", "s", Path.GetTempPath()))
            if (e is AiCodeAgent.Core.Flows.FlowFinishedEvent f) result = f.Result;

        Assert.NotNull(result);
        Assert.True(result!.Succeeded, result.Message);
        Assert.Equal(2, result.Nodes.Single(n => n.NodeId == "backend").Runs);
        Assert.Equal("dana-dotnet", result.Nodes.Single(n => n.NodeId == "backend").CharacterId);
        Assert.Equal(2, orchestrator.MaxConcurrent);
        Assert.Contains(orchestrator.Calls, c => c.Node == "release-notes");
    }
}
