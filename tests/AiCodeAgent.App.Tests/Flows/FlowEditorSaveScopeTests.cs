using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S1: the Flows editor saves into the scope the user picks and shows where a flow comes from.</summary>
public class FlowEditorSaveScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flows-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _globalDir;
    private readonly string _repo;
    private readonly RolePresetLoader _presets = new(NullLogger<RolePresetLoader>.Instance);
    private readonly CharacterRegistry _characters;

    public FlowEditorSaveScopeTests()
    {
        _globalDir = Path.Combine(_root, "global-pipelines");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string ProjectFile(string name) => Path.Combine(_repo, ".aiagent", "pipelines", name + ".json");
    private string GlobalFile(string name) => Path.Combine(_globalDir, name + ".json");

    private SdlcPipelineLoader NewLoader(string? project) => new(NullLogger<SdlcPipelineLoader>.Instance, _globalDir, project);

    private async Task<FlowEditorViewModel> OpenAsync(SdlcPipelineLoader loader)
    {
        var vm = new FlowEditorViewModel(loader, null, _characters);
        await vm.OpenAsync(_repo);
        vm.NewFlow();
        return vm;
    }

    private static SdlcPipelineDefinition Flow(string name, string description) => new()
    {
        Name = name,
        Description = description,
        Stages = new() { new() { Role = "implementer", Name = "Do", PromptTemplate = "{task}" } }
    };

    private static async Task WriteAsync(string file, SdlcPipelineDefinition def)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(def, SdlcPipelineLoader.FileJsonOptions));
    }

    [Fact]
    public async Task SavingANewFlow_InAProject_AsksWhere_ThenWritesToTheProject()
    {
        var vm = await OpenAsync(NewLoader(_repo));
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "team-flow";
        Assert.Equal("new", vm.ScopeBadge);

        await vm.SaveAsync();

        Assert.True(vm.IsChoosingSaveScope);
        Assert.False(File.Exists(ProjectFile("team-flow")));
        Assert.False(File.Exists(GlobalFile("team-flow")));

        await vm.SaveToProjectAsync();

        Assert.False(vm.IsChoosingSaveScope);
        Assert.True(File.Exists(ProjectFile("team-flow")));
        Assert.False(File.Exists(GlobalFile("team-flow")));
        Assert.Equal("project", vm.ScopeBadge);
        Assert.Equal(PipelineScope.Project, vm.LoadedScope);
        Assert.Equal("team-flow", vm.SelectedPipeline);
        Assert.Equal("project", vm.SelectedPipelineItem!.Scope);
        Assert.Contains("project flow", vm.StatusText);
    }

    [Fact]
    public async Task ChoosingGlobal_WritesToTheGlobalFolder()
    {
        var vm = await OpenAsync(NewLoader(_repo));
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "my-flow";

        await vm.SaveAsync();
        await vm.SaveToGlobalAsync();

        Assert.True(File.Exists(GlobalFile("my-flow")));
        Assert.False(File.Exists(ProjectFile("my-flow")));
        Assert.Equal("global", vm.ScopeBadge);
    }

    [Fact]
    public async Task CancellingTheChoice_SavesNothing()
    {
        var vm = await OpenAsync(NewLoader(_repo));
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "maybe";
        await vm.SaveAsync();

        vm.CancelSaveScope();

        Assert.False(vm.IsChoosingSaveScope);
        Assert.False(File.Exists(ProjectFile("maybe")));
        Assert.False(File.Exists(GlobalFile("maybe")));
        Assert.Equal("new", vm.ScopeBadge);
    }

    [Fact]
    public async Task WithoutAProjectFolder_SaveWritesGlobally_WithoutAsking()
    {
        var vm = await OpenAsync(NewLoader(null));
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "solo";

        await vm.SaveAsync();

        Assert.False(vm.IsChoosingSaveScope);
        Assert.False(vm.CanChooseSaveScope);
        Assert.True(File.Exists(GlobalFile("solo")));
        Assert.Equal("global", vm.ScopeBadge);
    }

    [Fact]
    public async Task SavingToProject_WithoutAProjectFolder_ReportsTheProblem()
    {
        var vm = await OpenAsync(NewLoader(null));
        vm.AddNode("planner", 40, 40);
        vm.FlowName = "nowhere";

        await vm.SaveToProjectAsync();

        Assert.StartsWith("Save failed", vm.StatusText);
        Assert.False(File.Exists(GlobalFile("nowhere")));
    }

    [Fact]
    public async Task ProjectIsTheDefaultChoice_OnlyInAGitRepository()
    {
        if (!SdlcPipelineLoader.IsGitRepository(_repo)) // the temp folder may sit inside a repository
            Assert.False((await OpenAsync(NewLoader(_repo))).IsProjectSaveDefault);

        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var vm = await OpenAsync(NewLoader(_repo));
        Assert.True(vm.IsProjectSaveDefault);
        Assert.Equal(PipelineScope.Project, vm.DefaultSaveScope);
    }

    [Fact]
    public async Task AFlowThatLivesInAFile_IsSavedBackIntoIt_WithoutAsking()
    {
        await WriteAsync(ProjectFile("feature-team"), Flow("feature-team", "v1"));
        await WriteAsync(GlobalFile("mine"), Flow("mine", "v1"));
        var vm = await OpenAsync(NewLoader(_repo));

        vm.Load("feature-team");
        Assert.Equal("project", vm.ScopeBadge);
        vm.Description = "v2";
        await vm.SaveAsync();
        Assert.False(vm.IsChoosingSaveScope);
        Assert.Contains("v2", await File.ReadAllTextAsync(ProjectFile("feature-team")));

        vm.Load("mine");
        Assert.Equal("global", vm.ScopeBadge);
        vm.Description = "v2";
        await vm.SaveAsync();
        Assert.False(vm.IsChoosingSaveScope);
        Assert.Contains("v2", await File.ReadAllTextAsync(GlobalFile("mine")));
        Assert.False(File.Exists(ProjectFile("mine")));
    }

    [Fact]
    public async Task RenamingAFlow_AsksAgain_AndBuiltInsAreNeverEditedInPlace()
    {
        await WriteAsync(ProjectFile("feature-team"), Flow("feature-team", "v1"));
        var vm = await OpenAsync(NewLoader(_repo));
        vm.Load("feature-team");
        vm.FlowName = "feature-team-2";

        await vm.SaveAsync();
        Assert.True(vm.IsChoosingSaveScope);
        vm.CancelSaveScope();

        vm.Load("quick-fix");
        Assert.Equal("builtin", vm.ScopeBadge);
        await vm.SaveAsync();
        Assert.True(vm.IsChoosingSaveScope);
        await vm.SaveToProjectAsync();
        Assert.True(File.Exists(ProjectFile("quick-fix")));
        Assert.Equal("project", vm.ScopeBadge);
        Assert.Equal("overrides built-in", vm.OverridesText);
    }

    [Fact]
    public async Task ProjectFlow_ThatHidesAGlobalOne_ShowsOverridesGlobal()
    {
        await WriteAsync(GlobalFile("feature-team"), Flow("feature-team", "global"));
        await WriteAsync(ProjectFile("feature-team"), Flow("feature-team", "project"));
        var vm = await OpenAsync(NewLoader(_repo));

        vm.Load("feature-team");

        Assert.Equal("project", vm.ScopeBadge);
        Assert.Equal("overrides global", vm.OverridesText);
        Assert.True(vm.HasOverridesText);
        Assert.Equal("project", vm.SelectedPipelineItem!.Scope);
        Assert.Equal("overrides global", vm.SelectedPipelineItem.Overrides);
        Assert.Equal("project", vm.Description);

        vm.Load("full-sdlc");
        Assert.Equal("builtin", vm.ScopeBadge);
        Assert.False(vm.HasOverridesText);
    }

    [Fact]
    public async Task PipelineList_IsGroupedProjectThenGlobalThenBuiltIn()
    {
        await WriteAsync(GlobalFile("zeta-global"), Flow("zeta-global", "g"));
        await WriteAsync(ProjectFile("alpha-project"), Flow("alpha-project", "p"));
        var vm = await OpenAsync(NewLoader(_repo));

        var scopes = vm.PipelineItems.Select(i => i.Scope).ToList();

        Assert.Equal(new[] { "project", "global", "builtin", "builtin" }, scopes);
        Assert.Equal("alpha-project", vm.PipelineItems[0].Name);
        Assert.Equal("zeta-global", vm.PipelineItems[1].Name);
        Assert.Equal(vm.Pipelines.ToList(), vm.PipelineItems.Select(i => i.Name).ToList());
    }

    [Fact]
    public async Task SwitchingTheWorkingDirectory_ListsThatProjectsFlows()
    {
        var other = Path.Combine(_root, "other-repo");
        await WriteAsync(Path.Combine(other, ".aiagent", "pipelines", "other-flow.json"), Flow("other-flow", "o"));
        await WriteAsync(ProjectFile("this-flow"), Flow("this-flow", "t"));
        var vm = await OpenAsync(NewLoader(_repo));
        Assert.Contains("this-flow", vm.Pipelines);
        Assert.DoesNotContain("other-flow", vm.Pipelines);

        vm.SetProjectDirectory(other);

        Assert.Contains("other-flow", vm.Pipelines);
        Assert.DoesNotContain("this-flow", vm.Pipelines);
        Assert.Equal("project", vm.PipelineItems.Single(i => i.Name == "other-flow").Scope);
    }

    [Fact]
    public async Task DeletingAProjectFlow_AsksFirst_AndRevealsTheGlobalOne()
    {
        await WriteAsync(GlobalFile("shared"), Flow("shared", "global"));
        await WriteAsync(ProjectFile("shared"), Flow("shared", "project"));
        var vm = await OpenAsync(NewLoader(_repo));
        vm.Load("shared");

        vm.RequestDelete();
        Assert.True(vm.IsConfirmingDelete);
        Assert.Contains("project flow file", vm.DeletePrompt);
        vm.ConfirmDelete();

        Assert.False(File.Exists(ProjectFile("shared")));
        Assert.True(File.Exists(GlobalFile("shared")));
        Assert.Equal("global", vm.ScopeBadge);
        Assert.Equal("global", vm.Description);
        Assert.Contains("global version is shown again", vm.StatusText);
    }

    [Fact]
    public async Task ABuiltInHasNothingToDelete()
    {
        var vm = await OpenAsync(NewLoader(_repo));
        vm.Load("full-sdlc");

        vm.RequestDelete();

        Assert.False(vm.IsConfirmingDelete);
    }
}
