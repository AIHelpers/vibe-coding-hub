using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

/// <summary>Characters driving the workflow: plan steps, pipeline stages, subagents.</summary>
public class CharacterWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly RolePresetLoader _presets;
    private readonly CharacterRegistry _characters;

    public CharacterWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "char-flow-" + Guid.NewGuid().ToString("N"));
        _globalDir = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_globalDir);
        _presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance, _globalDir);

        Write("alex-architect", """
            ---
            id: alex-architect
            display-name: Alex
            description: Architect
            base-role: planner
            skills: [adr-writer]
            pinned-skills: [conventions]
            ---
            ALEX-PERSONA
            """);
        Write("sam-dev", """
            ---
            id: sam-dev
            description: Developer
            base-role: implementer
            model: dev-model
            permission-mode: full-auto
            tools: { remove: [git] }
            skills: [tdd]
            ---
            SAM-PERSONA
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private void Write(string id, string content) => File.WriteAllText(Path.Combine(_globalDir, id + ".md"), content);

    private AgentSessionCoordinator Coordinator() =>
        new(Substitute.For<ILogger<AgentSessionCoordinator>>(), presetLoader: _presets, characters: _characters);

    // ===================== Coordinator =====================

    [Fact]
    public void Coordinator_StepWithCharacter_GetsPersonaToolsSkills()
    {
        var options = Coordinator().ResolveStepOptions(new SessionStep
        {
            AgentId = "a1",
            Role = "planner",
            CharacterId = "alex-architect",
            Options = new AgentOptions { PermissionMode = PermissionMode.Ask }
        });

        Assert.Equal("alex-architect", options.CharacterId);
        Assert.Equal("a1", options.AgentId);
        Assert.Contains("ALEX-PERSONA", options.RoleSystemPrompt);
        Assert.Contains("PLANNER agent", options.RoleSystemPrompt);
        Assert.Equal(new[] { "adr-writer" }, options.AllowedSkills);
        Assert.Equal(new[] { "conventions" }, options.PinnedSkills);
        Assert.DoesNotContain("write_file", options.EnabledTools);
        // No explicit permission-mode on the character: the step's mode is kept.
        Assert.Equal(PermissionMode.Ask, options.PermissionMode);
    }

    [Fact]
    public void Coordinator_UserFileNamedLikeRole_AppliesToPlainRoleSteps()
    {
        Write("planner", "---\nid: planner\ndescription: custom\nbase-role: planner\nskills: [release-notes]\n---\nCUSTOM-PLANNER");

        var options = Coordinator().ResolveStepOptions(new SessionStep { AgentId = "planner", Role = "planner" });

        Assert.Equal(new[] { "release-notes" }, options.AllowedSkills);
        Assert.Contains("CUSTOM-PLANNER", options.RoleSystemPrompt);
    }

    [Fact]
    public void Coordinator_BuiltInRole_KeepsPresetBehavior()
    {
        var options = Coordinator().ResolveStepOptions(new SessionStep { AgentId = "r", Role = "reviewer" });

        Assert.Null(options.CharacterId);
        Assert.Null(options.AllowedSkills);
        Assert.Equal(_presets.GetPreset("reviewer")!.SystemPrompt, options.RoleSystemPrompt);
    }

    [Fact]
    public void Coordinator_UnknownCharacter_FallsBackToRole()
    {
        var options = Coordinator().ResolveStepOptions(new SessionStep { AgentId = "x", Role = "reviewer", CharacterId = "ghost" });
        Assert.Null(options.CharacterId);
        Assert.Equal(_presets.GetPreset("reviewer")!.SystemPrompt, options.RoleSystemPrompt);
    }

    [Fact]
    public async Task Coordinator_RunAsync_PassesCharacterOptionsToOrchestrator()
    {
        var coordinator = Coordinator();
        AgentOptions? seen = null;
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.StreamRunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<AgentOptions>(o => seen = o), Arg.Any<CancellationToken>())
            .Returns(_ => Events(new AgentFinishedEvent(new AgentResponse { Content = "ok" })));
        coordinator.RegisterAgent("alex-architect", orchestrator);

        await foreach (var _ in coordinator.RunAsync(new SessionPlan
        {
            SessionId = "s",
            Steps = new() { new SessionStep { AgentId = "alex-architect", Role = "planner", CharacterId = "alex-architect", Prompt = "plan" } }
        })) { }

        Assert.NotNull(seen);
        Assert.Equal("alex-architect", seen!.CharacterId);
        Assert.Equal(new[] { "adr-writer" }, seen.AllowedSkills);
    }

    private static async IAsyncEnumerable<AgentEvent> Events(params AgentEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    // ===================== Pipeline runner =====================

    private SdlcPipelineRunner Runner(AgentSessionCoordinator coordinator) =>
        new(coordinator, _presets, Substitute.For<IAgentOrchestrator>(), NullLogger<SdlcPipelineRunner>.Instance, _characters);

    private static SdlcPipelineDefinition Pipeline(params SdlcStageDefinition[] stages) => new() { Name = "p", Stages = stages.ToList() };

    [Fact]
    public void Pipeline_CastReplacesRoleWithCharacter()
    {
        var coordinator = Coordinator();
        var plan = Runner(coordinator).BuildPlan(
            Pipeline(
                new SdlcStageDefinition { Role = "planner", Name = "Analyze", PromptTemplate = "{task}" },
                new SdlcStageDefinition { Role = "implementer", Name = "Implement", PromptTemplate = "{task}" },
                new SdlcStageDefinition { Role = "reviewer", Name = "Review", PromptTemplate = "{task}" }),
            "do it", "s", "/work", model: "session-model",
            cast: SdlcPipelineRunner.ParseCast("planner=alex-architect, Implement=sam-dev"));

        var analyze = plan.Steps[0];
        Assert.Equal("alex-architect", analyze.AgentId);
        Assert.Equal("alex-architect", analyze.CharacterId);
        Assert.Equal("planner", analyze.Role);
        Assert.Equal(PermissionMode.Plan, analyze.Options.PermissionMode); // base role default in pipelines
        Assert.Equal("session-model", analyze.Options.Model);
        Assert.Equal(new[] { "adr-writer" }, analyze.Options.AllowedSkills);

        var implement = plan.Steps[1];
        Assert.Equal("sam-dev", implement.AgentId);
        Assert.Equal(PermissionMode.FullAuto, implement.Options.PermissionMode); // explicit on character
        Assert.Equal("dev-model", implement.Options.Model); // character model is more specific than --model
        Assert.Contains("git", implement.Options.DisabledTools);
        Assert.DoesNotContain("git", implement.Options.EnabledTools);
        Assert.Contains("SAM-PERSONA", implement.Options.RoleSystemPrompt);

        var review = plan.Steps[2];
        Assert.Equal("reviewer", review.AgentId);
        Assert.Null(review.CharacterId);
        Assert.Null(review.Options.AllowedSkills);

        Assert.True(coordinator.Agents.ContainsKey("alex-architect"));
        Assert.True(coordinator.Agents.ContainsKey("sam-dev"));
    }

    [Fact]
    public void Pipeline_StageCharacterField_IsUsed_AndCastOverridesIt()
    {
        var stage = new SdlcStageDefinition { Role = "implementer", Name = "Implement", Character = "sam-dev", PromptTemplate = "{task}" };

        var plain = Runner(Coordinator()).BuildPlan(Pipeline(stage), "t", "s", "/w");
        Assert.Equal("sam-dev", plain.Steps[0].CharacterId);

        var cast = Runner(Coordinator()).BuildPlan(Pipeline(stage), "t", "s", "/w",
            cast: new Dictionary<string, string> { ["implementer"] = "alex-architect" });
        Assert.Equal("alex-architect", cast.Steps[0].CharacterId);
    }

    [Fact]
    public void Pipeline_UnknownCastCharacter_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Runner(Coordinator()).BuildPlan(
            Pipeline(new SdlcStageDefinition { Role = "planner", Name = "Analyze", PromptTemplate = "{task}" }),
            "t", "s", "/w", cast: new Dictionary<string, string> { ["planner"] = "ghost" }));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Pipeline_WithoutCharacters_KeepsPresetBehavior()
    {
        var runner = new SdlcPipelineRunner(Coordinator(), _presets, Substitute.For<IAgentOrchestrator>(), NullLogger<SdlcPipelineRunner>.Instance);
        var plan = runner.BuildPlan(Pipeline(new SdlcStageDefinition { Role = "planner", Name = "Analyze", PromptTemplate = "Task: {task}" }), "x", "s", "/w");

        var step = Assert.Single(plan.Steps);
        Assert.Equal("planner", step.AgentId);
        Assert.Equal("Task: x", step.Prompt);
        Assert.Equal(_presets.GetPreset("planner")!.SystemPrompt, step.Options.RoleSystemPrompt);
        Assert.Equal(PermissionMode.Plan, step.Options.PermissionMode);
    }

    [Theory]
    [InlineData("planner=alex", 1)]
    [InlineData(" planner = alex , implementer=sam ", 2)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseCast_Valid(string? text, int count) => Assert.Equal(count, SdlcPipelineRunner.ParseCast(text).Count);

    [Theory]
    [InlineData("planner")]
    [InlineData("=alex")]
    [InlineData("planner=")]
    public void ParseCast_Invalid(string text) => Assert.Throws<FormatException>(() => SdlcPipelineRunner.ParseCast(text));

    // ===================== Subagents =====================

    [Fact]
    public void SubagentOptions_CarryCharacterFields_AndAlwaysDisableSpawn()
    {
        var options = SubagentRunner.BuildOptions("sub_1", new SubagentContext
        {
            CharacterId = "sam-dev",
            RoleSystemPrompt = "P",
            AllowedSkills = new() { "tdd" },
            PinnedSkills = new() { "conventions" },
            DisabledTools = new() { "git" }
        });

        Assert.Equal("sam-dev", options.CharacterId);
        Assert.Equal("P", options.RoleSystemPrompt);
        Assert.Equal(new[] { "tdd" }, options.AllowedSkills);
        Assert.Equal(new[] { "conventions" }, options.PinnedSkills);
        Assert.Contains("git", options.DisabledTools);
        Assert.Contains("spawn_subagent", options.DisabledTools);
        Assert.True(options.NonInteractive);
    }
}
