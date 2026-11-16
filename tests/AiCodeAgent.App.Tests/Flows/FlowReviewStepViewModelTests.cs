using System;
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

/// <summary>E10-S4b in the editor: the "Independent review" step can be added, configured and saved.</summary>
public class FlowReviewStepViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-review-vm-" + Guid.NewGuid().ToString("N"));

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
    public async Task Palette_OffersAnIndependentReview_ThatBecomesAReviewStep()
    {
        var vm = await OpenAsync();

        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.ReviewItemId));

        var node = Assert.Single(vm.Nodes);
        Assert.True(node.IsReview);
        Assert.False(node.IsAgent);
        Assert.False(node.IsHuman);
        Assert.True(node.UsesModel); // model and provider pick the reviewer
        Assert.Null(node.CharacterId);
        Assert.Equal(new[] { "approved", "rejected" }, node.Outcomes);
        var stage = Assert.Single(vm.ToDefinition().Stages);
        Assert.Equal("review", stage.Kind);
        Assert.Null(stage.Character);
        Assert.True(stage.IsReview);
    }

    [Fact]
    public async Task ReviewStep_KeepsItsReviewerChoice_WhenSavedAndReopened()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.ReviewItemId));
        vm.Nodes[0].Provider = "openai";
        vm.Nodes[0].Model = "gpt-x";
        var definition = vm.ToDefinition();

        vm.NewFlow();
        vm.LoadDefinition(definition);

        var node = Assert.Single(vm.Nodes);
        Assert.True(node.IsReview);
        Assert.Equal("openai", node.Provider);
        Assert.Equal("gpt-x", node.Model);
    }

    [Fact]
    public async Task ReviewStep_HasNoProblem_WithoutACharacter()
    {
        var vm = await OpenAsync();
        vm.AddFromPalette(vm.Palette.First(p => p.CharacterId == FlowEditorViewModel.ReviewItemId));

        vm.Revalidate();

        Assert.False(vm.Nodes[0].HasError);
    }
}
