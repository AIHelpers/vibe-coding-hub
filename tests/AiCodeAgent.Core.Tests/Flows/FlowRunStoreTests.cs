using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

public sealed class FlowRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flowruns-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    private static SdlcPipelineDefinition Flow() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("backend"), Stage("model"), Stage("review", "reviewer") },
        Edges = new() { Edge("design", "backend"), Edge("design", "model"), Edge("backend", "review"), Edge("model", "review") }
    };

    private static FlowStepRecord Step(string node, string file, FlowNodeState state = FlowNodeState.Done, string? outcome = null) => new()
    {
        NodeId = node,
        State = state,
        Outcome = outcome,
        OutputFile = file,
        Prompt = $"prompt of {node}",
        ToolCalls = { new FlowToolCallRecord("read_file", "a.cs", false, 12) },
        Diffs = { new FlowDiffRecord("a.cs", "@@ -1 +1 @@") },
        Usage = new FlowStepRunUsage(node, 1, "claude-sonnet-test", null, 1000, 500, 0, 0, 0.0105m, false, TimeSpan.FromSeconds(2))
    };

    private static FlowRunRecord Record(string id, DateTimeOffset at, FlowRunStatus status = FlowRunStatus.Succeeded, string task = "ship it",
        params FlowStepRecord[] steps) => new()
    {
        Id = id,
        FlowName = "diamond",
        Task = task,
        StartedAt = at,
        FinishedAt = at.AddSeconds(42),
        Status = status,
        Message = "ok",
        CostUsd = 0.021m,
        Tokens = 3000,
        Definition = Flow(),
        Steps = steps.ToList()
    };

    private static Dictionary<string, string> Outputs(params string[] nodes) => nodes.ToDictionary(n => n + ".md", n => $"{n} output");

    private static FlowRunRecord Full(string id, DateTimeOffset at) => Record(id, at, FlowRunStatus.Succeeded, "ship it",
        Step("design", "design.md"), Step("backend", "backend.md"), Step("model", "model.md"), Step("review", "review.md"));

    [Fact]
    public void WriteThenRead_RoundTripsTheRunAndItsOutputs()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design", "backend", "model", "review"));

        var loaded = store.Load("diamond", id);
        Assert.NotNull(loaded);
        Assert.Equal("ship it", loaded!.Task);
        Assert.Equal(FlowRunStatus.Succeeded, loaded.Status);
        Assert.Equal(4, loaded.Steps.Count);
        Assert.Equal(4, loaded.Definition.Stages.Count);
        var review = loaded.Steps.Single(s => s.NodeId == "review");
        Assert.Equal("prompt of review", review.Prompt);
        Assert.Equal("read_file", Assert.Single(review.ToolCalls).Name);
        Assert.Equal("a.cs", Assert.Single(review.Diffs).FilePath);
        Assert.Equal(1500, review.Usage!.Tokens);
        Assert.Equal("review output", store.LoadOutput("diamond", id, review));
    }

    [Fact]
    public void RunsAreStoredUnderFlowAndRunId()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design", "backend", "model", "review"));

        Assert.True(File.Exists(Path.Combine(_root, "diamond", id, "run.json")));
        Assert.True(File.Exists(Path.Combine(_root, "diamond", id, "design.md")));
    }

    [Fact]
    public void List_ShowsNewestFirstWithTimeTaskStatusCostAndDuration()
    {
        var store = new FlowRunStore(_root);
        var older = FlowRunStore.NewRunId(T0);
        var newer = FlowRunStore.NewRunId(T0.AddMinutes(5));
        store.Save(Full(older, T0), Outputs("design", "backend", "model", "review"));
        store.Save(Record(newer, T0.AddMinutes(5), FlowRunStatus.Failed, "second task", Step("design", "design.md")), Outputs("design"));

        var list = store.List("diamond");

        Assert.Equal(new[] { newer, older }, list.Select(r => r.RunId));
        Assert.Equal("second task", list[0].Task);
        Assert.Equal(FlowRunStatus.Failed, list[0].Status);
        Assert.Equal(T0.AddMinutes(5), list[0].StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(42), list[1].Duration);
        Assert.Equal(0.021m, list[1].CostUsd);
    }

    [Fact]
    public void Retention_KeepsOnlyTheNewestRunsOfAFlow()
    {
        var store = new FlowRunStore(_root, () => 3);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var id = FlowRunStore.NewRunId(T0.AddMinutes(i));
            ids.Add(id);
            store.Save(Full(id, T0.AddMinutes(i)), Outputs("design", "backend", "model", "review"));
        }

        var kept = store.List("diamond").Select(r => r.RunId).ToList();

        Assert.Equal(new[] { ids[4], ids[3], ids[2] }, kept);
        Assert.False(Directory.Exists(Path.Combine(_root, "diamond", ids[0])));
    }

    [Fact]
    public void Retention_DefaultsToFiftyRuns()
    {
        Assert.Equal(50, FlowRunStore.DefaultRetention);
        Assert.Equal(50, new AiCodeAgent.Core.Configuration.AgentConfiguration().FlowRunHistoryLimit);
    }

    [Fact]
    public void Retention_DoesNotTouchOtherFlows()
    {
        var store = new FlowRunStore(_root, () => 1);
        var other = Full(FlowRunStore.NewRunId(T0), T0) with { FlowName = "other" };
        store.Save(other, Outputs("design"));
        for (var i = 0; i < 3; i++)
            store.Save(Full(FlowRunStore.NewRunId(T0.AddMinutes(i + 1)), T0.AddMinutes(i + 1)), Outputs("design"));

        Assert.Single(store.List("other"));
        Assert.Single(store.List("diamond"));
    }

    [Fact]
    public void FoldersWithoutAReadableRun_AreSkipped()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design"));
        Directory.CreateDirectory(Path.Combine(_root, "diamond", "20260101-000000-dead"));
        var broken = Path.Combine(_root, "diamond", "20260102-000000-beef");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "run.json"), "{ not json");

        Assert.Equal(id, Assert.Single(store.List("diamond")).RunId);
        Assert.Null(store.Load("diamond", "20260102-000000-beef"));
    }

    [Fact]
    public void ListAndLoad_OfAnUnknownFlow_AreEmpty()
    {
        var store = new FlowRunStore(_root);
        Assert.Empty(store.List("nothing"));
        Assert.Null(store.Load("nothing", "x"));
    }

    [Fact]
    public void Delete_RemovesOneRun()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design"));

        Assert.True(store.Delete("diamond", id));
        Assert.Empty(store.List("diamond"));
        Assert.False(store.Delete("diamond", id));
    }

    [Fact]
    public void FlowNamesWithPathCharacters_StayInsideTheStore()
    {
        var store = new FlowRunStore(_root);
        var dir = Path.GetFullPath(store.RunDirectory("../../evil", "x"));
        Assert.StartsWith(Path.GetFullPath(_root), dir);
    }

    [Fact]
    public void BuildReplay_ReusesEverythingUpstreamOfTheStep()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design", "backend", "model", "review"));
        var record = store.Load("diamond", id)!;

        var plan = store.BuildReplay(record, "review", out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(new[] { "backend", "design", "model" }, plan!.Reused.Keys.OrderBy(k => k));
        Assert.Equal("design output", plan.Reused["design"].Text);
        Assert.Equal(id, plan.SourceRunId);
        Assert.Equal("review", plan.FromNodeId);
    }

    [Fact]
    public void BuildReplay_FromTheFirstStep_ReusesNothing()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design", "backend", "model", "review"));

        var plan = store.BuildReplay(store.Load("diamond", id)!, "design", out _);

        Assert.Empty(plan!.Reused);
    }

    [Fact]
    public void BuildReplay_WhenAnUpstreamStepFailed_ExplainsWhy()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        var record = Record(id, T0, FlowRunStatus.Failed, "t",
            Step("design", "design.md"), Step("backend", "backend.md", FlowNodeState.Failed), Step("model", "model.md"));
        store.Save(record, Outputs("design", "backend", "model"));

        var plan = store.BuildReplay(store.Load("diamond", id)!, "review", out var error);

        Assert.Null(plan);
        Assert.Contains("backend", error);
    }

    [Fact]
    public void BuildReplay_ForUnknownStep_ReturnsAnError()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design"));

        Assert.Null(store.BuildReplay(store.Load("diamond", id)!, "nope", out var error));
        Assert.Contains("nope", error);
    }

    [Fact]
    public void StepMarkdown_ShowsPromptToolCallsAndOutput()
    {
        var store = new FlowRunStore(_root);
        var id = FlowRunStore.NewRunId(T0);
        store.Save(Full(id, T0), Outputs("design", "backend", "model", "review"));
        var record = store.Load("diamond", id)!;

        var md = FlowRunMarkdown.Step(record, "review", s => store.LoadOutput("diamond", id, s));

        Assert.Contains("prompt of review", md);
        Assert.Contains("read_file", md);
        Assert.Contains("review output", md);
        Assert.Contains("claude-sonnet-test", md);
        Assert.Contains("@@ -1 +1 @@", md);
    }
}
