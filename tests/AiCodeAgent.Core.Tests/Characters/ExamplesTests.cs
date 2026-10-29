using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Flows;
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
        Assert.Equal(new[]
        {
            "alex-architect", "dana-dotnet", "drew-docs", "felix-frontend", "maya-migration", "mia-ml", "pat-performance",
            "quinn-qa", "rex-release", "sam-developer", "sasha-security", "software-developer", "stella-style"
        }, custom.Select(c => c.Id));
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

    private static readonly string[] TemplateNames = { "bug-fix", "feature-team", "fullstack-feature", "pr-review", "release", "migration" };

    [Fact]
    public async Task EveryExamplePipeline_Validates_AndReferencesExistingExampleCharacters()
    {
        var (_, characters, _) = Load();
        var library = ExampleLibrary.FromDirectory(ExamplesDir());
        Assert.Contains("team-sdlc", library.FlowNames);
        Assert.Superset(new HashSet<string>(TemplateNames), new HashSet<string>(library.FlowNames));
        var exampleCharacters = (await characters.ListAsync()).Where(c => !c.IsBuiltIn).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in library.FlowNames)
        {
            var flow = library.GetFlow(name);
            Assert.NotNull(flow);
            Assert.Equal(name, flow!.Name);
            Assert.False(string.IsNullOrWhiteSpace(flow.Description), $"{name}: description");
            foreach (var stage in flow.Stages.Where(s => !string.IsNullOrEmpty(s.Character)))
                Assert.True(exampleCharacters.Contains(stage.Character!), $"{name}: '{stage.Name}' uses unknown character '{stage.Character}'");

            var graph = AiCodeAgent.Core.Flows.FlowGraph.Build(flow, id => characters.GetAsync(id).GetAwaiter().GetResult());
            Assert.True(graph.IsValid, $"{name}: " + string.Join("\n", graph.Problems));
            Assert.Empty(graph.Problems);
        }
    }

    [Fact]
    public void Gallery_ListsTheSixTemplates_WithSampleTasks()
    {
        var templates = ExampleLibrary.FromDirectory(ExamplesDir()).Templates();

        Assert.Equal(TemplateNames.OrderBy(n => n), templates.Select(t => t.Id).OrderBy(n => n));
        Assert.All(templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.SampleTask));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.NotEmpty(t.Characters);
        });
        var bugFix = templates.Single(t => t.Id == "bug-fix");
        Assert.Equal(new[] { "quinn-qa", "sam-developer", "stella-style" }, bugFix.Characters);
        var review = bugFix.Flow.Stages.Single(s => s.Id == "review");
        Assert.Equal(new[] { "approved", "rejected" }, review.Outcomes);
        Assert.Contains(bugFix.Flow.Edges!, e => e.From == "review" && e.To == "fix" && e.When == "rejected" && e.MaxLoops == 2);
        Assert.Equal(new[] { "ok", "changes-requested" }, templates.Single(t => t.Id == "pr-review").Flow.Stages.Single(s => s.Id == "summary").Outcomes);
        Assert.True(templates.Single(t => t.Id == "release").Flow.Stages.Any(s => s.RequireConfirmation));
    }

    [Fact]
    public void EmbeddedExamples_AreTheFilesInTheRepository()
    {
        var folder = ExampleLibrary.FromDirectory(ExamplesDir());
        var embedded = ExampleLibrary.FromEmbedded();

        Assert.Equal(folder.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), embedded.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        foreach (var path in folder.Paths)
            Assert.Equal(folder.ReadFile(path)!.Replace("\r\n", "\n"), embedded.ReadFile(path)!.Replace("\r\n", "\n"));
        Assert.Equal(TemplateNames.OrderBy(n => n), embedded.Templates().Select(t => t.Id).OrderBy(n => n));
    }

    [Fact]
    public async Task UsingATemplate_InABareProject_CreatesTheMissingCharacters_AndTheFlowValidates()
    {
        var library = ExampleLibrary.FromEmbedded();
        foreach (var template in library.Templates())
        {
            var root = Path.Combine(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N"));
            try
            {
                var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
                var charactersDir = Path.Combine(root, "characters");
                var skillsDir = Path.Combine(root, "skills");
                var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, charactersDir);
                var skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, skillsDir);

                var existing = (await characters.ListAsync()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existingSkills = (await skills.ListAllAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plan = library.Plan(template, existing, existingSkills);

                Assert.Empty(plan.Unavailable);
                Assert.Equal(template.Characters.Where(c => !existing.Contains(c)).OrderBy(c => c), plan.CharactersToCreate.Where(c => template.Characters.Contains(c)).OrderBy(c => c));
                // A template a character extends comes before the character.
                var order = plan.CharactersToCreate.ToList();
                var parentAt = order.IndexOf("software-developer");
                if (parentAt >= 0)
                    foreach (var child in new[] { "dana-dotnet", "sam-developer", "felix-frontend", "maya-migration", "drew-docs" })
                        if (order.IndexOf(child) is var at and >= 0)
                            Assert.True(parentAt < at, $"{template.Id}: software-developer must be created before {child}");

                var written = library.Install(plan, charactersDir, skillsDir);
                Assert.NotEmpty(written);
                characters.Refresh();
                skills.Refresh();

                var graph = AiCodeAgent.Core.Flows.FlowGraph.Build(template.Flow, id => characters.GetAsync(id).GetAwaiter().GetResult());
                Assert.True(graph.IsValid, $"{template.Id}: " + string.Join("\n", graph.Problems));
                foreach (var id in template.Characters)
                    Assert.True((await characters.GetAsync(id))!.IsValid, $"{template.Id}: {id}");
                Assert.Empty(await new SkillMaintenance(skills, characters).FindDanglingReferencesAsync());

                // Nothing is missing any more, and a second install writes nothing.
                var again = library.Plan(template, (await characters.ListAsync()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    (await skills.ListAllAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Assert.True(again.NothingToCreate, $"{template.Id}: {string.Join(",", again.CharactersToCreate)} {string.Join(",", again.SkillsToCreate)}");
                Assert.Empty(library.Install(plan, charactersDir, skillsDir));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task EveryTemplate_RunsToTheEnd_OnceItsCharactersAreInstalled()
    {
        var library = ExampleLibrary.FromEmbedded();
        foreach (var template in library.Templates())
        {
            var root = Path.Combine(Path.GetTempPath(), "gallery-run-" + Guid.NewGuid().ToString("N"));
            try
            {
                var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
                var charactersDir = Path.Combine(root, "characters");
                var skillsDir = Path.Combine(root, "skills");
                var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, charactersDir);
                var skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, skillsDir);
                var plan = library.Plan(template, (await characters.ListAsync()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    (await skills.ListAllAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
                library.Install(plan, charactersDir, skillsDir);
                characters.Refresh();

                // Every step answers; a step with outcomes picks its first one (approved, ok, ...).
                var outcomes = template.Flow.Stages.Where(s => s.Outcomes is { Count: > 0 }).ToDictionary(s => s.Id!, s => s.Outcomes![0]);
                var orchestrator = new AiCodeAgent.Core.Tests.Flows.ScriptedOrchestrator((node, _, _) =>
                    outcomes.TryGetValue(node, out var outcome) ? $"Done.\nOUTCOME: {outcome}" : null);
                var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: presets, characters: characters);
                var runner = new SdlcPipelineRunner(coordinator, presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, characters);
                AiCodeAgent.Core.Flows.FlowRunResult? result = null;
                await foreach (var e in runner.RunAsync(template.Flow, template.SampleTask, "s", Path.GetTempPath()))
                {
                    if (e is AgentTaggedEvent { Inner: ApprovalRequestEvent approval }) approval.Approval.TrySetResult(true); // the "ask me" gate
                    if (e is AiCodeAgent.Core.Flows.FlowFinishedEvent f) result = f.Result;
                }

                Assert.NotNull(result);
                Assert.True(result!.Succeeded, $"{template.Id}: {result.Message}");
                Assert.Equal(template.Flow.Stages.Count, result.Nodes.Count(n => n.Runs > 0));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void Diagram_DrawsEveryStep_ArrowsAndLoops()
    {
        var library = ExampleLibrary.FromEmbedded();
        var bugFix = AiCodeAgent.Core.Flows.FlowDiagram.Build(library.GetFlow("bug-fix")!);

        Assert.Equal(4, bugFix.Nodes.Split('M', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.NotEmpty(bugFix.Edges);
        Assert.NotEmpty(bugFix.Loops); // review -> fix (rejected)
        Assert.True(bugFix.Width > 100 && bugFix.Height > 40);

        var prReview = AiCodeAgent.Core.Flows.FlowDiagram.Build(library.GetFlow("pr-review")!);
        Assert.Equal(string.Empty, prReview.Loops);
        Assert.Equal(4, prReview.Nodes.Split('M', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
