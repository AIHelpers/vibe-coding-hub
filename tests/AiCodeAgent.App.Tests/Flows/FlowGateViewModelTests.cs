using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S6 in the editor: the gate buttons (Approve / with note / Skip / Stop) and human steps.</summary>
public class FlowGateViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-gate-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private async Task<FlowEditorViewModel> OpenAsync()
    {
        Directory.CreateDirectory(_root);
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters);
        await vm.OpenAsync(_root);
        vm.NewFlow();
        return vm;
    }

    private static (ApprovalRequestEvent Request, TaskCompletionSource<bool> Answer) Gate()
    {
        var tcs = new TaskCompletionSource<bool>();
        var call = new ToolCall { Id = "g", Name = "pipeline_stage", Arguments = new() };
        return (new ApprovalRequestEvent(call, tcs), tcs);
    }

    [Fact]
    public async Task Gate_ShowsTheGateButtons_ForTheStep()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var (request, _) = Gate();

        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        Assert.True(deploy.IsAwaitingGate);
        Assert.True(deploy.IsAwaitingApproval);
        Assert.True(deploy.NeedsAnswer);
        Assert.Same(deploy, vm.SelectedNode);
    }

    [Fact]
    public async Task ApproveWithNote_PassesTheNoteBackAndApproves()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var (request, answer) = Gate();
        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        vm.BeginGateNoteCommand.Execute(null);
        Assert.True(vm.IsEnteringGateNote);
        vm.GateNote = "  Approve, but also handle null due dates.  ";
        vm.ApproveWithNoteCommand.Execute(deploy);

        Assert.True(await answer.Task);
        Assert.Equal("Approve, but also handle null due dates.", request.Feedback.Text);
        Assert.False(request.Feedback.Skip);
        Assert.False(deploy.IsAwaitingApproval);
        Assert.False(deploy.IsAwaitingGate);
        Assert.False(vm.IsEnteringGateNote);
        Assert.Equal(string.Empty, vm.GateNote);
    }

    [Fact]
    public async Task PlainApprove_HasNoNote()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var (request, answer) = Gate();
        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        vm.Approve(deploy);

        Assert.True(await answer.Task);
        Assert.Null(request.Feedback.Text);
    }

    [Fact]
    public async Task SkipStep_AnswersNoAndMarksTheSkip()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var (request, answer) = Gate();
        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        vm.SkipStepCommand.Execute(deploy);

        Assert.False(await answer.Task);
        Assert.True(request.Feedback.Skip);
        Assert.False(deploy.IsAwaitingGate);
    }

    [Fact]
    public async Task StopFlow_AnswersNoWithoutTheSkipMark()
    {
        var vm = await OpenAsync();
        var deploy = vm.AddNode("deployer", 40, 40);
        var (request, answer) = Gate();
        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        vm.StopAtGateCommand.Execute(deploy);

        Assert.False(await answer.Task);
        Assert.False(request.Feedback.Skip);
    }

    [Fact]
    public async Task ToolApproval_IsNotAGate()
    {
        var vm = await OpenAsync();
        var dev = vm.AddNode("implementer", 40, 40);
        var tcs = new TaskCompletionSource<bool>();
        var call = new ToolCall { Id = "t", Name = "run_command", Arguments = new() };

        vm.ApplyRunEvent(new AgentTaggedEvent(new ApprovalRequestEvent(call, tcs), "implementer"));

        Assert.True(dev.IsAwaitingApproval);
        Assert.False(dev.IsAwaitingGate);
    }

    [Fact]
    public async Task Palette_OffersAHumanDecision_ThatBecomesAHumanStep()
    {
        var vm = await OpenAsync();
        var item = vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId);

        vm.AddFromPalette(item);

        var node = Assert.Single(vm.Nodes);
        Assert.True(node.IsHuman);
        Assert.False(node.IsAgent);
        Assert.Null(node.CharacterId);
        Assert.Equal(new[] { "approved", "rejected" }, node.Outcomes);
        Assert.Equal("You decide", node.CharacterLabel);
        var stage = Assert.Single(vm.ToDefinition().Stages);
        Assert.Equal("human", stage.Kind);
        Assert.Null(stage.Character);
        Assert.True(stage.IsHuman);
    }

    [Fact]
    public async Task HumanStep_SurvivesSavingAndReopening()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var definition = vm.ToDefinition();

        vm.NewFlow();
        vm.LoadDefinition(definition);

        Assert.True(Assert.Single(vm.Nodes).IsHuman);
    }

    [Fact]
    public async Task HumanStepHasNoProblem_WithoutACharacter()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var node = vm.Nodes[0];

        vm.Revalidate();

        Assert.False(node.HasError);
    }

    [Fact]
    public async Task HumanRequest_WaitsOnTheNode_AndChoosingAnswersIt()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var node = vm.Nodes[0];
        var choice = new TaskCompletionSource<FlowHumanChoice>();

        vm.ApplyRunEvent(new AgentTaggedEvent(new FlowHumanRequestEvent(node.Id, "Please review the design.", node.Outcomes, choice), node.Id));

        Assert.True(node.IsAwaitingChoice);
        Assert.True(node.NeedsAnswer);
        Assert.Same(node, vm.SelectedNode);
        Assert.Contains("Please review the design.", vm.SelectedOutput);

        vm.GateNote = "ship it";
        vm.ChooseCommand.Execute("approved");

        var picked = await choice.Task;
        Assert.Equal("approved", picked.Outcome);
        Assert.Equal("ship it", picked.Note);
        Assert.False(picked.Stop);
        Assert.False(node.IsAwaitingChoice);
        Assert.Contains("approved", vm.SelectedOutput);
    }

    [Fact]
    public async Task HumanRequest_CanStopTheFlow()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var node = vm.Nodes[0];
        var choice = new TaskCompletionSource<FlowHumanChoice>();
        vm.ApplyRunEvent(new AgentTaggedEvent(new FlowHumanRequestEvent(node.Id, "?", node.Outcomes, choice), node.Id));

        vm.StopHumanCommand.Execute(null);

        Assert.True((await choice.Task).Stop);
        Assert.False(node.IsAwaitingChoice);
    }
}
