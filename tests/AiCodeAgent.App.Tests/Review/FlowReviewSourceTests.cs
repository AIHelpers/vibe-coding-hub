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

namespace AiCodeAgent.App.Tests.Review;

/// <summary>E10-S3: flow gates and human decisions as the Review Queue sees them.</summary>
public class FlowReviewSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-review-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task Gate_IsListed_AndApprovingItAnswersTheRun()
    {
        var vm = await OpenAsync();
        vm.AddNode("deployer", 40, 40);
        var answer = new TaskCompletionSource<bool>();
        vm.ApplyRunEvent(new AgentTaggedEvent(
            new ApprovalRequestEvent(new ToolCall { Id = "g", Name = "pipeline_stage", Arguments = new() }, answer), "deployer"));

        var source = new FlowReviewSource(vm);
        var item = Assert.Single(source.Pending());
        Assert.Equal(ReviewKind.Gate, item.Kind);

        Assert.True(item.Approve().Done);
        Assert.True(answer.Task.IsCompletedSuccessfully && answer.Task.Result);
        Assert.Empty(source.Pending());
    }

    [Fact]
    public async Task Gate_RequestChanges_ApprovesWithTheNote_AndDeclineDenies()
    {
        var vm = await OpenAsync();
        vm.AddNode("deployer", 40, 40);
        var answer = new TaskCompletionSource<bool>();
        var request = new ApprovalRequestEvent(new ToolCall { Id = "g", Name = "pipeline_stage", Arguments = new() }, answer);
        vm.ApplyRunEvent(new AgentTaggedEvent(request, "deployer"));

        var item = new FlowReviewSource(vm).Pending().Single();
        Assert.True(item.RequestChanges!("Only touch staging").Done);

        Assert.True(answer.Task.Result);
        Assert.Equal("Only touch staging", request.Feedback.Text);
    }

    [Fact]
    public async Task HumanStep_MapsApproveToAnApprovingOutcome_AndRefusesUnknownOnes()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var node = vm.Nodes.Single();
        var choice = new TaskCompletionSource<FlowHumanChoice>();
        vm.ApplyRunEvent(new AgentTaggedEvent(
            new FlowHumanRequestEvent(node.Id, "Ship it?", new[] { "approved", "rejected" }, choice), node.Id));

        var item = new FlowReviewSource(vm).Pending().Single();
        Assert.Equal(ReviewKind.Decision, item.Kind);
        Assert.True(item.Approve().Done);
        Assert.Equal("approved", choice.Task.Result.Outcome);
    }

    [Fact]
    public async Task HumanStep_WithCustomOutcomes_CannotBeApprovedFromTheQueue()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.HumanItemId));
        var node = vm.Nodes.Single();
        node.OutcomesText = "blue, green";
        var choice = new TaskCompletionSource<FlowHumanChoice>();
        vm.ApplyRunEvent(new AgentTaggedEvent(
            new FlowHumanRequestEvent(node.Id, "Which?", new[] { "blue", "green" }, choice), node.Id));

        var item = new FlowReviewSource(vm).Pending().Single();
        var result = item.Approve();
        Assert.False(result.Done);
        Assert.False(choice.Task.IsCompleted);
    }
}
