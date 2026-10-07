using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>Orchestrator double: each node answers "&lt;node&gt; done", or what <see cref="Answer"/> says.</summary>
internal sealed class FakeFlowOrchestrator : IAgentOrchestrator
{
    public Func<string, int, string?> Answer { get; init; } = (_, _) => null;
    /// <summary>Nodes that wait (up to 5 s) until all of them are running: proves parallelism without timing luck.</summary>
    public HashSet<string> MeetUp { get; init; } = new();
    private int _arrived;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, int> _calls = new();
    private int _active;
    public int MaxConcurrent;

    public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var node = options.AgentId ?? "?";
        int call;
        lock (_calls) call = _calls[node] = _calls.GetValueOrDefault(node) + 1;
        var now = Interlocked.Increment(ref _active);
        lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            yield return new TextDeltaEvent($"{node} working ");
            if (MeetUp.Contains(node))
            {
                if (Interlocked.Increment(ref _arrived) == MeetUp.Count) _allArrived.TrySetResult();
                await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            await Task.Delay(30, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
        yield return new AgentFinishedEvent(new AgentResponse { Content = Answer(node, call) ?? $"{node} done" });
    }
}

public class FlowEditorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flows-vm-" + Guid.NewGuid().ToString("N"));
    private readonly RolePresetLoader _presets = new(NullLogger<RolePresetLoader>.Instance);
    private readonly SdlcPipelineLoader _loader;
    private readonly CharacterRegistry _characters;

    public FlowEditorViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private async Task<FlowEditorViewModel> OpenAsync(FakeFlowOrchestrator? orchestrator = null)
    {
        SdlcPipelineRunner? runner = null;
        if (orchestrator != null)
        {
            var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: _presets, characters: _characters);
            runner = new SdlcPipelineRunner(coordinator, _presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, _characters);
        }
        var vm = new FlowEditorViewModel(_loader, runner, _characters);
        await vm.OpenAsync(_root);
        vm.NewFlow();
        return vm;
    }

    /// <summary>planner → implementer + tester (parallel) → reviewer.</summary>
    private static (FlowNodeViewModel Plan, FlowNodeViewModel Impl, FlowNodeViewModel Test, FlowNodeViewModel Review) Diamond(FlowEditorViewModel vm)
    {
        var plan = vm.AddNode("planner", 40, 100);
        var impl = vm.AddNode("implementer", 300, 20);
        var test = vm.AddNode("tester", 300, 200);
        var review = vm.AddNode("reviewer", 560, 100);
        vm.Connect(plan, impl);
        vm.Connect(plan, test);
        vm.Connect(impl, review);
        vm.Connect(test, review);
        return (plan, impl, test, review);
    }

    [Fact]
    public async Task Palette_ListsCharacters_ButNotTemplates()
    {
        await _characters.CreateAsync(new CharacterDraft { Id = "dev-base", Description = "t", IsTemplate = true }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "dana", Description = "dev", Extends = "dev-base" }, SkillScope.Global);
        var vm = await OpenAsync();

        Assert.Contains(vm.Palette, p => p.CharacterId == "dana");
        Assert.Contains(vm.Palette, p => p.CharacterId == "planner");
        Assert.DoesNotContain(vm.Palette, p => p.CharacterId == "dev-base");

        vm.PaletteFilter = "dan";
        Assert.Equal("dana", Assert.Single(vm.Palette).CharacterId);
    }

    [Fact]
    public async Task AddConnectAndDelete_WithUndoRedo()
    {
        var vm = await OpenAsync();
        var (plan, impl, test, review) = Diamond(vm);

        Assert.Equal(4, vm.Nodes.Count);
        Assert.Equal(4, vm.Edges.Count);
        Assert.True(vm.IsValid);
        Assert.Equal("1: planner · 2: implementer ∥ tester · 3: reviewer", vm.WavesText);
        Assert.Equal(new[] { "planner", "implementer", "tester", "reviewer" }, vm.Nodes.Select(n => n.Id));

        vm.SelectedNode = test;
        vm.DeleteSelected();
        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(2, vm.Edges.Count); // its two arrows went with it

        vm.Undo();
        Assert.Equal(4, vm.Nodes.Count);
        Assert.Equal(4, vm.Edges.Count);
        vm.Redo();
        Assert.Equal(3, vm.Nodes.Count);
        Assert.True(vm.IsDirty);

        // Duplicate arrows are refused.
        Assert.Null(vm.Connect(vm.Nodes.First(n => n.Id == "planner"), vm.Nodes.First(n => n.Id == "implementer")));
        Assert.Contains("already exists", vm.StatusText);
    }

    [Fact]
    public async Task PalettePlus_AddsAfterTheSelectedStep_AndConnectsIt()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == "planner"));
        var plan = vm.Nodes.Single();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == "implementer"));
        vm.SelectedNode = plan;
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == "tester"));

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(new[] { ("planner", "implementer"), ("planner", "tester") }, vm.Edges.Select(e => (e.From.Id, e.To.Id)));
        var impl = vm.Nodes.Single(n => n.Id == "implementer");
        var test = vm.Nodes.Single(n => n.Id == "tester");
        Assert.True(impl.X > plan.X);
        Assert.NotEqual(impl.Y, test.Y); // placed below instead of on top of the other card
        Assert.Equal("1: planner · 2: implementer ∥ tester", vm.WavesText);
    }

    [Fact]
    public async Task ConnectingBack_MakesALoop_WithConditionAndLimit()
    {
        var vm = await OpenAsync();
        var (_, impl, _, review) = Diamond(vm);
        review.OutcomesText = "approved, rejected";

        var loop = vm.Connect(review, impl)!;

        Assert.True(loop.IsLoop);
        Assert.Equal("rejected", loop.When);
        Assert.Equal(2, loop.MaxLoops);
        Assert.True(vm.IsValid);
        Assert.Equal(new[] { FlowEditorViewModel.AlwaysCondition, "approved", "rejected" }, vm.ConditionOptions);

        // Without a limit, the cycle is an error that shows on the arrow.
        vm.SetMaxLoops(0);
        Assert.False(vm.IsValid);
        Assert.True(loop.HasError);
        Assert.False(vm.CanRun);
        vm.Undo();
        Assert.True(vm.IsValid);
        Assert.Equal(2, vm.Edges.Single(e => e.IsLoop).MaxLoops);
    }

    [Fact]
    public async Task EdgeCondition_IsSetFromTheInspector()
    {
        var vm = await OpenAsync();
        var review = vm.AddNode("reviewer", 40, 40);
        var deploy = vm.AddNode("deployer", 300, 40);
        review.OutcomesText = "approved, rejected";
        var edge = vm.Connect(review, deploy)!;

        vm.SelectedCondition = "approved";
        Assert.Equal("approved", edge.When);
        Assert.Equal("approved", edge.Label);
        var def = vm.ToDefinition();
        Assert.Equal("approved", def.Edges!.Single().When);

        // A condition that is not one of the source's outcomes is reported.
        review.OutcomesText = "ok";
        Assert.False(vm.IsValid);
        Assert.Contains(vm.Problems, p => p.IsError && p.EdgeIndex == 0);
    }

    [Fact]
    public async Task TypingInAField_IsOneUndoStep()
    {
        var vm = await OpenAsync();
        var node = vm.AddNode("planner", 40, 40);
        var before = node.PromptTemplate;

        node.PromptTemplate = "P";
        node.PromptTemplate = "Pl";
        node.PromptTemplate = "Plan {task}";
        vm.Undo();

        Assert.Equal(before, vm.Nodes.Single().PromptTemplate);
    }

    [Fact]
    public async Task DraggingAnArrow_ConnectsOnDrop_OrCancelsOnEmptyCanvas()
    {
        var vm = await OpenAsync();
        var a = vm.AddNode("planner", 40, 40);
        var b = vm.AddNode("implementer", 400, 40);

        vm.BeginEdge(a, a.OutPortX, a.OutPortY);
        Assert.True(vm.IsDrawingEdge);
        vm.DragEdge(200, 60);
        Assert.Contains("L 200,60", vm.PendingEdgePath);
        Assert.Null(vm.EndEdge(vm.NodeAt(900, 900)));
        Assert.False(vm.IsDrawingEdge);
        Assert.Empty(vm.Edges);

        vm.BeginEdge(a, a.OutPortX, a.OutPortY);
        var edge = vm.EndEdge(vm.NodeAt(b.X + 10, b.Y + 10));
        Assert.NotNull(edge);
        Assert.Same(b, edge!.To);
    }

    [Fact]
    public async Task MovingANode_SnapsToGrid_AndIsOneUndoStep()
    {
        var vm = await OpenAsync();
        var node = vm.AddNode("planner", 40, 40);

        vm.BeginMove(node);
        vm.MoveNode(node, 100, 100);
        vm.MoveNode(node, 123, 77);
        vm.EndMove();
        Assert.Equal(120, node.X);
        Assert.Equal(80, node.Y);

        vm.Undo();
        Assert.Equal(40, vm.Nodes.Single().X);

        // A click without moving leaves no undo entry.
        var undoBefore = vm.CanUndo;
        var n = vm.Nodes.Single();
        vm.BeginMove(n);
        vm.EndMove();
        Assert.Equal(undoBefore, vm.CanUndo);
    }

    [Fact]
    public async Task AutoLayout_PutsWavesInColumns()
    {
        var vm = await OpenAsync();
        var (plan, impl, test, review) = Diamond(vm);
        foreach (var n in vm.Nodes) vm.MoveNode(n, 0, 0);

        vm.AutoLayout();

        Assert.True(plan.X < impl.X);
        Assert.Equal(impl.X, test.X);
        Assert.True(impl.X < review.X);
        Assert.NotEqual(impl.Y, test.Y);
        vm.Undo();
        Assert.All(vm.Nodes, n => Assert.Equal(0, n.X));
    }

    [Fact]
    public async Task Save_WritesAFlowFile_ThatLoadsBack()
    {
        var vm = await OpenAsync();
        var (_, impl, _, review) = Diamond(vm);
        review.OutcomesText = "approved, rejected";
        vm.Connect(review, impl);
        vm.FlowName = "team-flow";
        vm.Description = "A team";
        vm.MaxParallel = 2;

        await vm.SaveAsync();

        Assert.False(vm.IsDirty);
        Assert.True(File.Exists(_loader.GetPipelinePath("team-flow")));
        Assert.Contains("team-flow", vm.Pipelines);
        var saved = _loader.GetPipeline("team-flow")!;
        Assert.True(saved.IsFlow);
        Assert.Equal(2, saved.MaxParallel);
        Assert.Equal(5, saved.Edges!.Count);
        Assert.Equal(2, saved.Edges.Single(e => e.From == "reviewer" && e.To == "implementer").MaxLoops);

        vm.NewFlow();
        Assert.Empty(vm.Nodes);
        vm.Load("team-flow");
        Assert.Equal(4, vm.Nodes.Count);
        Assert.Equal(5, vm.Edges.Count);
        Assert.Equal("A team", vm.Description);
        Assert.True(vm.Edges.Single(e => e.IsLoop).IsLoop);
        Assert.Equal(review.X, vm.Nodes.Single(n => n.Id == "reviewer").X);
    }

    [Fact]
    public async Task InvalidName_IsNotSaved()
    {
        var vm = await OpenAsync();
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "Team Flow";
        await vm.SaveAsync();
        Assert.Contains("kebab-case", vm.StatusText);
        Assert.False(File.Exists(_loader.GetPipelinePath("Team Flow")));
    }

    [Fact]
    public async Task LinearPipeline_ShowsAsChain_AndSavingTurnsItIntoAFlow()
    {
        var vm = await OpenAsync();
        vm.Load("quick-fix");

        Assert.True(vm.WasLinear);
        Assert.Contains("linear pipeline", vm.StatusText);
        Assert.Equal(vm.Nodes.Count - 1, vm.Edges.Count);
        Assert.True(vm.IsValid);

        vm.FlowName = "quick-fix-flow";
        await vm.SaveAsync();
        Assert.False(vm.WasLinear);
        Assert.Contains("now a flow", vm.StatusText);
        Assert.True(_loader.GetPipeline("quick-fix-flow")!.IsFlow);
        // The built-in linear pipeline is untouched.
        Assert.False(_loader.GetPipeline("quick-fix")!.IsFlow);
    }

    [Fact]
    public async Task DeleteFlow_AsksFirst_ThenRemovesTheFile()
    {
        var vm = await OpenAsync();
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "to-delete";
        await vm.SaveAsync();

        vm.RequestDelete();
        Assert.True(vm.IsConfirmingDelete);
        vm.ConfirmDelete();

        Assert.False(File.Exists(_loader.GetPipelinePath("to-delete")));
        Assert.DoesNotContain("to-delete", vm.Pipelines);
        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public async Task RunEvents_UpdateNodesEdgesAndOutput()
    {
        var vm = await OpenAsync();
        var (plan, impl, test, review) = Diamond(vm);
        vm.SelectedNode = impl;

        vm.ApplyRunEvent(new FlowNodeStatusEvent("implementer", FlowNodeState.Running, 1));
        vm.ApplyRunEvent(new AgentTaggedEvent(new TextDeltaEvent("writing code"), "implementer"));
        Assert.True(impl.IsRunning);
        Assert.Equal("▶", impl.StateGlyph);
        Assert.Contains("writing code", vm.SelectedOutput);

        vm.ApplyRunEvent(new AgentTaggedEvent(new AgentFinishedEvent(new AgentResponse { Content = "Implemented." }), "implementer"));
        vm.ApplyRunEvent(new FlowNodeStatusEvent("implementer", FlowNodeState.Done, 1));
        vm.ApplyRunEvent(new FlowEdgeTakenEvent("implementer", "reviewer", null, false));
        Assert.True(impl.IsDone);
        Assert.Equal("Implemented.", vm.SelectedOutput);
        Assert.True(vm.Edges.Single(e => e.From == impl && e.To == review).IsTaken);

        vm.ApplyRunEvent(new FlowNodeStatusEvent("tester", FlowNodeState.Failed, 1, Detail: "crashed"));
        Assert.True(test.IsFailed);
        Assert.Contains("crashed", test.OutputText);

        vm.ApplyRunEvent(new FlowNodeStatusEvent("reviewer", FlowNodeState.Skipped, 0, Detail: "blocked by tester"));
        Assert.True(review.IsSkipped);
        Assert.Equal("–", review.StateGlyph);

        vm.ResetRunState();
        Assert.All(vm.Nodes, n => Assert.False(n.HasRunState));
        Assert.All(vm.Edges, e => Assert.False(e.IsTaken));
    }

    [Fact]
    public async Task ConfirmationGate_IsAnsweredOnTheNode()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var tcs = new TaskCompletionSource<bool>();
        var gate = new ToolCall { Id = "g", Name = "pipeline_stage", Arguments = new() };

        vm.ApplyRunEvent(new AgentTaggedEvent(new ApprovalRequestEvent(gate, tcs), "deployer"));
        Assert.True(deploy.IsAwaitingApproval);
        Assert.Same(deploy, vm.SelectedNode);
        Assert.Contains("approval", vm.StatusText);

        vm.Approve(deploy);
        Assert.True(await tcs.Task);
        Assert.False(deploy.IsAwaitingApproval);
    }

    [Fact]
    public async Task Run_ExecutesTheFlow_WithParallelBranches_AndLiveStates()
    {
        // implementer and tester wait for each other, so they can only finish if they really run together.
        var orchestrator = new FakeFlowOrchestrator { MeetUp = new HashSet<string> { "implementer", "tester" } };
        var vm = await OpenAsync(orchestrator);
        var (plan, impl, test, review) = Diamond(vm);

        vm.RunTask = "add pagination";
        Assert.True(vm.CanRun);

        await vm.RunAsync();

        Assert.False(vm.IsRunning);
        Assert.All(vm.Nodes, n => Assert.True(n.IsDone, $"{n.Id}: {n.StateText}"));
        Assert.All(vm.Edges, e => Assert.True(e.IsTaken));
        Assert.Equal(2, orchestrator.MaxConcurrent); // implementer ∥ tester
        Assert.Contains("reviewer done", review.OutputText);
        Assert.StartsWith("Flow succeeded", vm.StatusText);
    }

    [Fact]
    public async Task Run_WithReviewLoop_ShowsRounds()
    {
        // The reviewer rejects once, then approves.
        var orchestrator = new FakeFlowOrchestrator
        {
            Answer = (node, call) => node == "reviewer" ? (call == 1 ? "Needs work.\nOUTCOME: rejected" : "Good.\nOUTCOME: approved") : null
        };
        var vm = await OpenAsync(orchestrator);
        var plan = vm.AddNode("planner", 40, 40);
        var impl = vm.AddNode("implementer", 300, 40);
        var review = vm.AddNode("reviewer", 560, 40);
        review.OutcomesText = "approved, rejected";
        vm.Connect(plan, impl);
        vm.Connect(impl, review);
        var loop = vm.Connect(review, impl)!;
        vm.RunTask = "fix the bug";

        await vm.RunAsync();

        Assert.Equal(2, impl.RunCount);
        Assert.Equal(2, review.RunCount);
        Assert.Equal("approved", review.RunOutcome);
        Assert.Contains("round 2", review.StateText);
        Assert.Equal(1, loop.LoopsTaken);
        Assert.Equal("rejected ↺1/2", loop.Label);
    }

    [Fact]
    public async Task Run_IsRefused_WhenInvalidOrWithoutTask()
    {
        var vm = await OpenAsync(new FakeFlowOrchestrator());
        var a = vm.AddNode("planner", 40, 40);
        var b = vm.AddNode("implementer", 300, 40);
        vm.Connect(a, b);

        await vm.RunAsync();
        Assert.Contains("task", vm.StatusText);

        vm.RunTask = "x";
        vm.Connect(b, a);
        vm.SetMaxLoops(0);
        await vm.RunAsync();
        Assert.Contains("Fix the problems", vm.StatusText);
        Assert.All(vm.Nodes, n => Assert.False(n.HasRunState));
    }
}

public class ChatFlowSessionTests
{
    [Theory]
    [InlineData("run-1", "run-1", true)]
    [InlineData("run-1/design#1", "run-1", true)]   // a flow step of this run
    [InlineData("run-10/design#1", "run-1", false)] // a different run that only shares a prefix
    [InlineData("other", "run-1", false)]
    public void ChatShowsEventsOfFlowSteps(string eventSession, string runSession, bool expected) =>
        Assert.Equal(expected, ChatViewModel.BelongsToSession(eventSession, runSession));
}
