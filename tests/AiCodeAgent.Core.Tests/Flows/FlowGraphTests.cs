using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Core.Tests.Flows;

public class FlowGraphTests
{
    internal static SdlcStageDefinition Stage(string id, string role = "implementer", string? character = null, params string[] outcomes) => new()
    {
        Id = id,
        Name = id,
        Role = role,
        Character = character,
        PromptTemplate = $"Do the {id} part of: {{task}}",
        Outcomes = outcomes.Length == 0 ? null : outcomes.ToList()
    };

    internal static FlowEdge Edge(string from, string to, string? when = null, int? maxLoops = null) =>
        new() { From = from, To = to, When = when, MaxLoops = maxLoops };

    /// <summary>design → (backend ∥ model) → review, review → backend on "rejected" (2 loops), review → deploy on "approved".</summary>
    internal static SdlcPipelineDefinition FeatureTeam(int maxLoops = 2) => new()
    {
        Name = "feature-team",
        Stages = new()
        {
            Stage("design", "planner"),
            Stage("backend"),
            Stage("model"),
            Stage("review", "reviewer", null, "approved", "rejected"),
            Stage("deploy", "deployer")
        },
        Edges = new()
        {
            Edge("design", "backend"),
            Edge("design", "model"),
            Edge("backend", "review"),
            Edge("model", "review"),
            Edge("review", "backend", "rejected", maxLoops),
            Edge("review", "deploy", "approved")
        }
    };

    [Fact]
    public void LinearPipeline_BecomesImplicitChain()
    {
        var linear = new SdlcPipelineDefinition
        {
            Name = "quick",
            Stages = new() { new() { Role = "implementer", Name = "Implement", PromptTemplate = "x" }, new() { Role = "reviewer", Name = "Review", PromptTemplate = "y" } }
        };
        var g = FlowGraph.Build(linear);

        Assert.True(g.IsImplicitChain);
        Assert.False(linear.IsFlow);
        Assert.True(g.IsValid);
        Assert.Equal(new[] { "implement", "review" }, g.Nodes.Select(n => n.Id));
        var e = Assert.Single(g.Edges);
        Assert.Equal(("implement", "review"), (e.From, e.To));
        Assert.Equal("1: implement · 2: review", g.DescribeWaves());
    }

    [Fact]
    public void FeatureTeam_IsValid_WithParallelWaveAndLoopEdge()
    {
        var g = FlowGraph.Build(FeatureTeam());

        Assert.True(g.IsValid, string.Join("\n", g.Problems));
        Assert.Equal("1: design · 2: backend ∥ model · 3: review · 4: deploy", g.DescribeWaves());
        var loop = Assert.Single(g.Edges, e => e.IsLoop);
        Assert.Equal(("review", "backend", "rejected", 2), (loop.From, loop.To, loop.When, loop.MaxLoops));
        Assert.Equal(new[] { "backend", "model" }, g.NodesWithParallelPeers().OrderBy(x => x));
        Assert.Equal(new[] { "backend", "review" }, g.NodesBetween("backend", "review").OrderBy(x => x));
        Assert.Equal(new[] { "backend", "deploy", "model", "review" }, g.Descendants("design").OrderBy(x => x));
    }

    [Fact]
    public void CycleWithoutMaxLoops_IsError_AndHasNoWaves()
    {
        var p = FeatureTeam() with { Edges = new() { Edge("design", "backend"), Edge("backend", "design") } };
        var g = FlowGraph.Build(p);

        Assert.False(g.IsValid);
        Assert.Contains(g.Problems, x => x.IsError && x.Message.Contains("Cycle") && x.Message.Contains("maxLoops"));
        Assert.Empty(g.Waves);
    }

    [Fact]
    public void SelfLoopWithLimit_IsAllowed()
    {
        var p = new SdlcPipelineDefinition
        {
            Name = "retry",
            Stages = new() { Stage("build", "implementer", null, "ok", "retry") },
            Edges = new() { Edge("build", "build", "retry", 3) }
        };
        var g = FlowGraph.Build(p);
        Assert.True(g.IsValid, string.Join("\n", g.Problems));
        Assert.True(Assert.Single(g.Edges).IsLoop);
    }

    [Fact]
    public void BadEdges_AreReportedWithTheirIndex()
    {
        var p = FeatureTeam() with
        {
            Edges = new()
            {
                Edge("design", "ghost"),
                Edge("design", "backend"),
                Edge("design", "backend"),
                Edge("backend", "model", "maybe"),
                Edge("model", "review", FlowGraph.LoopExhausted)
            }
        };
        var g = FlowGraph.Build(p);

        Assert.Contains(g.Problems, x => x.EdgeIndex == 0 && x.Message.Contains("unknown node 'ghost'"));
        Assert.Contains(g.Problems, x => x.EdgeIndex == 2 && x.Message.Contains("Duplicate edge"));
        Assert.Contains(g.Problems, x => x.EdgeIndex == 3 && x.Message.Contains("declares no outcomes"));
        Assert.Contains(g.Problems, x => x.EdgeIndex == 4 && x.Message.Contains("has no loop edge"));
    }

    [Fact]
    public void ConditionMustBeADeclaredOutcome()
    {
        var p = FeatureTeam() with { Edges = new() { Edge("review", "deploy", "shipit") } };
        var g = FlowGraph.Build(p);
        Assert.Contains(g.Problems, x => x.IsError && x.Message.Contains("'shipit' is not an outcome of review (approved, rejected)"));
    }

    [Fact]
    public void MaxLoopsOnForwardEdge_IsWarning_AndTreatedAsNormal()
    {
        var p = FeatureTeam() with { Edges = new() { Edge("design", "backend", null, 2) } };
        var g = FlowGraph.Build(p);
        Assert.True(g.IsValid);
        Assert.Contains(g.Problems, x => !x.IsError && x.Message.Contains("does not go back"));
        Assert.False(Assert.Single(g.Edges).IsLoop);
    }

    [Fact]
    public void Nodes_NeedCharacterOrRole_AndValidOutcomesAndPlaceholders()
    {
        var p = new SdlcPipelineDefinition
        {
            Name = "bad",
            Stages = new()
            {
                new() { Id = "a", Name = "A", PromptTemplate = "x" },
                new() { Id = "b", Name = "B", Role = "reviewer", PromptTemplate = "{input:ghost} {input:a}", Outcomes = new() { "ok", "ok", "two words" } },
                new() { Id = "b", Name = "Dup", Role = "tester", PromptTemplate = "z" }
            },
            Edges = new() { Edge("a", "b") }
        };
        var g = FlowGraph.Build(p);

        Assert.Contains(g.Problems, x => x.NodeId == "a" && x.Message.Contains("no character or role"));
        Assert.Contains(g.Problems, x => x.NodeId == "b" && x.Message.Contains("duplicate outcomes"));
        Assert.Contains(g.Problems, x => x.NodeId == "b" && x.Message.Contains("single words"));
        Assert.Contains(g.Problems, x => x.NodeId == "b" && x.Message.Contains("{input:ghost}"));
        Assert.DoesNotContain(g.Problems, x => x.Message.Contains("{input:a}"));
        Assert.Contains(g.Problems, x => x.Message.Contains("Duplicate node id 'b'"));
        Assert.Equal("dup", g.Nodes[2].Id); // re-derived from the name
    }

    [Fact]
    public void Characters_AreCheckedThroughLookup_AndCastOverrides()
    {
        var known = new Dictionary<string, CharacterInfo>
        {
            ["dana"] = new() { Id = "dana", Scope = SkillScope.Global },
            ["dev-template"] = new() { Id = "dev-template", IsTemplate = true, Scope = SkillScope.Global }
        };
        CharacterInfo? Lookup(string id) => known.TryGetValue(id, out var c) ? c : null;
        var p = new SdlcPipelineDefinition
        {
            Name = "c",
            Stages = new() { Stage("x", "", "dana"), Stage("y", "", "ghost"), Stage("z", "", "dev-template") },
            Edges = new() { Edge("x", "y"), Edge("y", "z") }
        };

        var g = FlowGraph.Build(p, Lookup);
        Assert.Contains(g.Problems, x => x.NodeId == "y" && x.Message.Contains("unknown character 'ghost'"));
        Assert.Contains(g.Problems, x => x.NodeId == "z" && x.Message.Contains("is a template"));
        Assert.DoesNotContain(g.Problems, x => x.NodeId == "x");

        var cast = new Dictionary<string, string> { ["y"] = "dana", ["z"] = "dana" };
        Assert.True(FlowGraph.Build(p, Lookup, cast).IsValid);
    }

    [Fact]
    public void Mermaid_ShowsNodesConditionsAndLoops()
    {
        var mermaid = FlowGraph.Build(FeatureTeam()).ToMermaid();
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains("n_design --> n_backend", mermaid);
        Assert.Contains("n_review -->|approved| n_deploy", mermaid);
        Assert.Contains("n_review -.->|rejected ↺2| n_backend", mermaid);
    }

    [Fact]
    public async Task Loader_RoundTripsFlow_AndKeepsLinearFilesShort()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flows-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, dir);
            await loader.SavePipelineAsync(FeatureTeam() with { MaxParallel = 2 });
            await loader.SavePipelineAsync(new SdlcPipelineDefinition { Name = "linear", Stages = new() { new() { Role = "implementer", Name = "Do", PromptTemplate = "t" } } });

            var json = await File.ReadAllTextAsync(loader.GetPipelinePath("feature-team"));
            Assert.Contains("\"edges\"", json);
            Assert.Contains("\"maxLoops\": 2", json);
            var linearJson = await File.ReadAllTextAsync(loader.GetPipelinePath("linear"));
            Assert.DoesNotContain("edges", linearJson);
            Assert.DoesNotContain("outcomes", linearJson);
            Assert.DoesNotContain("isFlow", json);

            var reloaded = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, dir).GetPipeline("feature-team")!;
            Assert.True(reloaded.IsFlow);
            Assert.Equal(2, reloaded.MaxParallel);
            Assert.Equal(6, reloaded.Edges!.Count);
            Assert.Equal(new[] { "approved", "rejected" }, reloaded.Stages[3].Outcomes);
            Assert.True(FlowGraph.Build(reloaded).IsValid);

            Assert.False(loader.IsBuiltIn("feature-team"));
            Assert.True(loader.IsBuiltIn("full-sdlc"));
            Assert.True(loader.DeletePipeline("linear"));
            Assert.Null(loader.GetPipeline("linear"));
            await Assert.ThrowsAsync<ArgumentException>(() => loader.SavePipelineAsync(new SdlcPipelineDefinition { Name = "Bad Name" }));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ExistingCamelOrPascalJson_StillLoads()
    {
        var json = """{ "Name": "old", "Stages": [ { "Role": "planner", "Name": "Plan", "PromptTemplate": "{task}" } ] }""";
        var p = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;
        Assert.Equal("planner", p.Stages[0].Role);
        Assert.False(p.IsFlow);
    }
}
