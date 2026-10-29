using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S5 in the editor: the History dropdown and the read-only replay mode.</summary>
public class FlowReplayViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-replay-vm-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 2, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static SdlcStageDefinition Stage(string id, string role) => new() { Id = id, Role = role };

    private static SdlcPipelineDefinition Definition() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("build", "implementer"), Stage("review", "reviewer") },
        Edges = new()
        {
            new FlowEdge { From = "design", To = "build" },
            new FlowEdge { From = "build", To = "review" }
        }
    };

    private static FlowStepRecord Step(string node, FlowNodeState state, bool reused = false) => new()
    {
        NodeId = node,
        State = state,
        Reused = reused,
        Detail = reused ? "reused" : null,
        OutputFile = node + ".md",
        Prompt = "prompt of " + node,
        Usage = reused ? null : new FlowStepRunUsage(node, 1, "claude-sonnet-x", null, 1000, 200, 0, 0, 0.004m, false, TimeSpan.FromSeconds(2))
    };

    private (FlowRunStore Store, string RunId) SaveRun(FlowRunStatus status = FlowRunStatus.Failed)
    {
        var store = new FlowRunStore(Path.Combine(_root, "runs"));
        var id = FlowRunStore.NewRunId(T0);
        store.Save(new FlowRunRecord
        {
            Id = id,
            FlowName = "diamond",
            Task = "ship it",
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(42),
            Status = status,
            Message = "review failed",
            CostUsd = 0.008m,
            Tokens = 2400,
            Definition = Definition(),
            Steps = new List<FlowStepRecord> { Step("design", FlowNodeState.Done), Step("build", FlowNodeState.Done), Step("review", FlowNodeState.Failed) }
        }, new Dictionary<string, string> { ["design.md"] = "the design", ["build.md"] = "the build", ["review.md"] = "the review" });
        return (store, id);
    }

    private async Task<FlowEditorViewModel> OpenAsync(FlowRunStore store)
    {
        Directory.CreateDirectory(_root);
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters, null, null, store);
        await vm.OpenAsync(_root);
        vm.NewFlow();
        vm.FlowName = "diamond";
        vm.RefreshHistory();
        return vm;
    }

    [Fact]
    public async Task History_ListsTheSavedRuns_WithTimeStatusCostDurationAndTask()
    {
        var (store, id) = SaveRun();
        var vm = await OpenAsync(store);

        var item = Assert.Single(vm.History);
        Assert.True(vm.HasHistory);
        Assert.Equal(id, item.RunId);
        Assert.Contains("failed", item.Label);
        Assert.Contains("$0.008", item.Label);
        Assert.Contains("42s", item.Label);
        Assert.Contains("ship it", item.Label);
    }

    [Fact]
    public async Task OpeningARun_SwitchesToAReadOnlyReplay_ColouredByTheFinalStatus()
    {
        var (store, id) = SaveRun();
        var vm = await OpenAsync(store);
        vm.AddNode("planner", 40, 40); // the flow that was open before

        vm.SelectedRun = vm.History[0];

        Assert.True(vm.IsReplay);
        Assert.True(vm.IsLocked);
        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(FlowNodeState.Done, vm.Nodes.Single(n => n.Id == "design").RunState);
        Assert.Equal(FlowNodeState.Failed, vm.Nodes.Single(n => n.Id == "review").RunState);
        Assert.Contains("failed", vm.ReplayTitle);
        Assert.Equal("ship it", vm.RunTask);
        Assert.True(vm.HasUsageRows);
    }

    [Fact]
    public async Task InReplay_TheCanvasCannotBeEdited()
    {
        var (store, _) = SaveRun();
        var vm = await OpenAsync(store);
        vm.OpenRun(vm.History[0].RunId);

        vm.AddFromPalette(new FlowPaletteItem { CharacterId = "planner", Label = "Planner" });
        vm.SelectedNode = vm.Nodes[0];
        vm.DeleteSelected();
        vm.MoveNode(vm.Nodes[0], 500, 500);

        Assert.Equal(3, vm.Nodes.Count);
        Assert.NotEqual(500, vm.Nodes[0].X);
    }

    [Fact]
    public async Task InReplay_ASelectedStepShowsItsPromptOutputAndModel()
    {
        var (store, _) = SaveRun();
        var vm = await OpenAsync(store);
        vm.OpenRun(vm.History[0].RunId);

        vm.SelectedNode = vm.Nodes.Single(n => n.Id == "build");

        Assert.Contains("prompt of build", vm.SelectedOutput);
        Assert.Contains("the build", vm.SelectedOutput);
        Assert.Contains("claude-sonnet-x", vm.SelectedOutput);
    }

    [Fact]
    public async Task ExitReplay_BringsBackTheFlowThatWasOpen()
    {
        var (store, _) = SaveRun();
        var vm = await OpenAsync(store);
        vm.AddNode("planner", 40, 40);
        vm.OpenRun(vm.History[0].RunId);

        vm.ExitReplayCommand.Execute(null);

        Assert.False(vm.IsReplay);
        Assert.False(vm.IsLocked);
        var node = Assert.Single(vm.Nodes);
        Assert.Null(node.RunState);
        Assert.Null(vm.SelectedRun);
    }

    [Fact]
    public async Task RerunFromHere_NeedsAReplayAndASelectedStep()
    {
        var (store, _) = SaveRun();
        var vm = await OpenAsync(store);
        Assert.False(vm.RerunFromHereCommand.CanExecute(null));

        vm.OpenRun(vm.History[0].RunId);

        Assert.False(vm.RerunFromHereCommand.CanExecute(null)); // no runner in this test
    }

    [Fact]
    public void ReusedStep_ShowsAsReused()
    {
        var node = new FlowNodeViewModel { Id = "design" };
        node.RunDetail = "reused";
        node.RunState = FlowNodeState.Done;

        Assert.Equal("reused", node.StateText);
    }

    [Fact]
    public async Task AReusedStep_IsMarkedInTheReplayOfARerun()
    {
        var store = new FlowRunStore(Path.Combine(_root, "runs"));
        var id = FlowRunStore.NewRunId(T0);
        store.Save(new FlowRunRecord
        {
            Id = id, FlowName = "diamond", Task = "t", StartedAt = T0, FinishedAt = T0.AddSeconds(5),
            Status = FlowRunStatus.Succeeded, Message = "ok", Definition = Definition(),
            ReplayOfRunId = "old", ReplayFromNodeId = "review",
            Steps = new List<FlowStepRecord> { Step("design", FlowNodeState.Done, reused: true), Step("build", FlowNodeState.Done, reused: true), Step("review", FlowNodeState.Done) }
        }, new Dictionary<string, string> { ["design.md"] = "d", ["build.md"] = "b", ["review.md"] = "r" });
        var vm = await OpenAsync(store);

        vm.OpenRun(id);

        Assert.Equal("reused", vm.Nodes.Single(n => n.Id == "design").StateText);
        Assert.Equal("done", vm.Nodes.Single(n => n.Id == "review").StateText);
        Assert.Contains("from review", vm.History[0].Label);
    }
}
