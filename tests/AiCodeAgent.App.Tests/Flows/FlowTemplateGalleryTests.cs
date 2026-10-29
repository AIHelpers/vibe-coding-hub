using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S2: ＋ New opens a gallery of ready-made flows; using one copies it into the project and can create the missing characters.</summary>
public class FlowTemplateGalleryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flows-gallery-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly RolePresetLoader _presets = new(NullLogger<RolePresetLoader>.Instance);
    private readonly CharacterRegistry _characters;
    private readonly SkillRegistry _skills;
    private readonly SdlcPipelineLoader _loader;

    public FlowTemplateGalleryTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance,
            Path.Combine(_root, "global-characters"), CharacterRegistry.GetDefaultProjectCharactersDir(_repo));
        _skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, Path.Combine(_root, "global-skills"), SkillRegistry.GetDefaultProjectSkillsDir(_repo));
        _loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "global-pipelines"), _repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private async Task<FlowEditorViewModel> OpenGalleryAsync()
    {
        var vm = new FlowEditorViewModel(_loader, null, _characters, null, _skills);
        await vm.OpenAsync(_repo);
        await vm.OpenGalleryAsync();
        return vm;
    }

    private string ProjectFlow(string name) => Path.Combine(_repo, ".aiagent", "pipelines", name + ".json");
    private string ProjectCharacter(string id) => Path.Combine(_repo, ".aiagent", "characters", id + ".md");

    [Fact]
    public async Task NewOpensTheGallery_WithTheSixTemplates_AndTheirCharacterChecks()
    {
        var vm = await OpenGalleryAsync();

        Assert.True(vm.IsGalleryOpen);
        Assert.Equal(new[] { "bug-fix", "feature-team", "fullstack-feature", "migration", "pr-review", "release" },
            vm.Templates.Select(t => t.Template.Id).OrderBy(n => n));
        var bugFix = vm.Templates.Single(t => t.Template.Id == "bug-fix");
        Assert.Equal(new[] { "quinn-qa", "sam-developer", "stella-style" }, bugFix.Characters.Select(c => c.Id));
        Assert.All(bugFix.Characters, c => { Assert.False(c.Exists); Assert.Equal("✗", c.Mark); });
        Assert.True(bugFix.HasMissing);
        Assert.False(bugFix.HasUnavailable);
        Assert.NotEmpty(bugFix.NodesPath);
        Assert.NotEmpty(bugFix.LoopsPath);
        Assert.Contains("reproduce", bugFix.Steps);
        Assert.False(string.IsNullOrWhiteSpace(bugFix.SampleTask));
    }

    [Fact]
    public async Task UseTemplate_WithMissingCharacters_AsksFirst_AndChangesNothing()
    {
        var vm = await OpenGalleryAsync();
        var card = vm.Templates.Single(t => t.Template.Id == "bug-fix");

        await vm.UseTemplateAsync(card);

        Assert.True(card.IsConfirming);
        Assert.Contains("software-developer", card.ConfirmText); // the template sam-developer extends comes along
        Assert.Contains("quinn-qa", card.ConfirmText);
        Assert.True(vm.IsGalleryOpen);
        Assert.False(File.Exists(ProjectFlow("bug-fix")));
        Assert.False(File.Exists(ProjectCharacter("quinn-qa")));

        vm.CancelTemplate(card);
        Assert.False(card.IsConfirming);
    }

    [Fact]
    public async Task CreateAndUse_InstallsCharactersAndSkills_CopiesTheFlowIntoTheProject_AndItIsValid()
    {
        var vm = await OpenGalleryAsync();
        var card = vm.Templates.Single(t => t.Template.Id == "bug-fix");

        await vm.CreateAndUseTemplateAsync(card);

        foreach (var id in new[] { "quinn-qa", "sam-developer", "stella-style", "software-developer" })
            Assert.True(File.Exists(ProjectCharacter(id)), id);
        Assert.True(File.Exists(Path.Combine(_repo, ".aiagent", "skills", "code-review-checklist", "SKILL.md")));
        Assert.True(File.Exists(ProjectFlow("bug-fix")));
        Assert.Equal(PipelineScope.Project, _loader.GetEntry("bug-fix")!.Scope);

        Assert.False(vm.IsGalleryOpen);
        Assert.Equal("bug-fix", vm.FlowName);
        Assert.Equal("project", vm.ScopeBadge);
        Assert.Equal(4, vm.Nodes.Count);
        Assert.True(vm.IsValid, string.Join("; ", vm.Problems));
        Assert.Equal(card.SampleTask, vm.RunTask);
        Assert.False(vm.IsDirty);
        Assert.All((await _characters.ListAsync()).Where(c => !c.IsBuiltIn), c => Assert.True(c.IsValid));
    }

    [Fact]
    public async Task UseTemplateWithoutCreating_StillCopiesTheFlow_ButFlagsTheMissingCharacters()
    {
        var vm = await OpenGalleryAsync();
        var card = vm.Templates.Single(t => t.Template.Id == "pr-review");
        await vm.UseTemplateAsync(card);

        await vm.UseTemplateAnywayAsync(card);

        Assert.True(File.Exists(ProjectFlow("pr-review")));
        Assert.False(File.Exists(ProjectCharacter("sasha-security")));
        Assert.False(vm.IsValid);
        Assert.Contains(vm.Problems, p => p.Message.Contains("unknown character"));
    }

    [Fact]
    public async Task OnceTheCharactersExist_UseTemplateCopiesRightAway_AndANameClashGetsANumber()
    {
        var vm = await OpenGalleryAsync();
        await vm.CreateAndUseTemplateAsync(vm.Templates.Single(t => t.Template.Id == "bug-fix"));

        await vm.OpenGalleryAsync();
        var card = vm.Templates.Single(t => t.Template.Id == "bug-fix");
        Assert.False(card.HasMissing);
        Assert.All(card.Characters, c => Assert.True(c.Exists));
        Assert.Equal("✓", card.Characters[0].Mark);

        await vm.UseTemplateAsync(card);

        Assert.False(card.IsConfirming);
        Assert.Equal("bug-fix-2", vm.FlowName);
        Assert.True(File.Exists(ProjectFlow("bug-fix-2")));
        Assert.True(File.Exists(ProjectFlow("bug-fix"))); // the first copy is untouched
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task WithoutAProjectFolder_TheTemplateAndItsCharactersGoToTheGlobalFolders()
    {
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "global-pipelines"));
        var characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "global-characters"));
        var vm = new FlowEditorViewModel(loader, null, characters, null, _skills);
        await vm.OpenGalleryAsync();
        var card = vm.Templates.Single(t => t.Template.Id == "migration");

        await vm.CreateAndUseTemplateAsync(card);

        Assert.True(File.Exists(Path.Combine(_root, "global-pipelines", "migration.json")));
        Assert.True(File.Exists(Path.Combine(_root, "global-characters", "maya-migration.md")));
        Assert.Equal("global", vm.ScopeBadge);
        Assert.True(vm.IsValid, string.Join("; ", vm.Problems));
    }

    [Fact]
    public async Task BlankFlow_ClosesTheGallery_AndStartsEmpty()
    {
        var vm = await OpenGalleryAsync();

        vm.StartBlankFlow();

        Assert.False(vm.IsGalleryOpen);
        Assert.Empty(vm.Nodes);
        Assert.Equal("new", vm.ScopeBadge);
    }

    [Fact]
    public async Task CloseGallery_ClearsPendingConfirmations()
    {
        var vm = await OpenGalleryAsync();
        var card = vm.Templates.First(t => t.HasMissing);
        await vm.UseTemplateAsync(card);
        Assert.True(card.IsConfirming);

        vm.CloseGallery();

        Assert.False(vm.IsGalleryOpen);
        Assert.False(card.IsConfirming);
    }
}
