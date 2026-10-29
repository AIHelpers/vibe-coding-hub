using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S3: the inspector's Model / Provider choices and the model shown on the node.</summary>
public class FlowNodeModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-model-vm-" + Guid.NewGuid().ToString("N"));

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
    public void Subtitle_ShowsTheModel_OnlyWhenTheStepSetsOne()
    {
        var node = new FlowNodeViewModel { CharacterLabel = "Sam — Developer" };
        Assert.Equal("Sam — Developer", node.Subtitle);

        node.Model = "fast";
        Assert.Equal("Sam — Developer · fast", node.Subtitle);

        node.Provider = "openai";
        Assert.Equal("Sam — Developer · openai/fast", node.Subtitle);

        node.RunState = AiCodeAgent.Core.Flows.FlowNodeState.Running;
        Assert.Equal("Sam — Developer · openai/fast · running", node.Subtitle);
    }

    [Fact]
    public void Selection_MapsEmptyToTheInheritEntry_AndBack()
    {
        var node = new FlowNodeViewModel();
        Assert.Equal(FlowNodeViewModel.InheritModelLabel, node.ModelSelection);
        Assert.Equal(FlowNodeViewModel.InheritProviderLabel, node.ProviderSelection);

        node.ModelSelection = "frontier";
        Assert.Equal("frontier", node.Model);

        node.ModelSelection = FlowNodeViewModel.InheritModelLabel;
        Assert.Equal(string.Empty, node.Model);

        node.ProviderSelection = "anthropic";
        Assert.Equal("anthropic", node.Provider);
        node.ProviderSelection = FlowNodeViewModel.InheritProviderLabel;
        Assert.Equal(string.Empty, node.Provider);
    }

    [Fact]
    public async Task ModelAndProvider_RoundTripThroughTheDefinition_AndOldFlowsStayEmpty()
    {
        var vm = await OpenAsync();
        var a = vm.AddNode("planner", 40, 40);
        var b = vm.AddNode("implementer", 300, 40);
        vm.Connect(a, b);
        b.Model = "fast";
        b.Provider = "openai";

        var def = vm.ToDefinition();
        Assert.Null(def.Stages.Single(s => s.Id == a.Id).Model);
        Assert.Null(def.Stages.Single(s => s.Id == a.Id).Provider);
        Assert.Equal("fast", def.Stages.Single(s => s.Id == b.Id).Model);
        Assert.Equal("openai", def.Stages.Single(s => s.Id == b.Id).Provider);

        vm.LoadDefinition(def);
        var reloaded = vm.Nodes.Single(n => n.Id == b.Id);
        Assert.Equal("fast", reloaded.Model);
        Assert.Equal("openai", reloaded.Provider);
        Assert.Equal(string.Empty, vm.Nodes.Single(n => n.Id == a.Id).Model);
    }

    [Fact]
    public async Task ChangingTheModel_IsOneUndoStep()
    {
        var vm = await OpenAsync();
        var node = vm.AddNode("planner", 40, 40);

        node.ModelSelection = "frontier";
        Assert.True(vm.CanUndo);
        vm.Undo();

        Assert.Equal(string.Empty, vm.Nodes.Single().Model);
    }

    [Fact]
    public async Task Combos_OfferInheritAndTiers_AndKeepAModelTypedIntoTheFile()
    {
        var vm = await OpenAsync();
        Assert.Equal(FlowNodeViewModel.InheritModelLabel, vm.ModelOptions[0]);
        Assert.Equal(new[] { "fast", "balanced", "frontier" }, vm.ModelOptions.Skip(1).Take(3));
        Assert.Equal(FlowNodeViewModel.InheritProviderLabel, vm.ProviderOptions[0]);

        vm.LoadDefinition(new SdlcPipelineDefinition
        {
            Name = "custom",
            Stages = new() { new SdlcStageDefinition { Id = "a", Role = "planner", Name = "a", PromptTemplate = "{task}", Model = "my-local-model", Provider = "lmstudio" } }
        });

        Assert.Contains("my-local-model", vm.ModelOptions);
        Assert.Contains("lmstudio", vm.ProviderOptions);
    }
}
