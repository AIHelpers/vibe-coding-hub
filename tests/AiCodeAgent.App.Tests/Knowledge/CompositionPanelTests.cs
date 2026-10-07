using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Knowledge;

/// <summary>Desktop panel: templates (extends) plus each character's own skills ("＋ Add skill").</summary>
public class CompositionPanelTests : IDisposable
{
    private readonly string _root;
    private readonly SkillRegistry _skills;
    private readonly CharacterRegistry _characters;
    private readonly RolePresetLoader _presets;

    public CompositionPanelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pk-comp-" + Guid.NewGuid().ToString("N"));
        _skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, Path.Combine(_root, "skills"));
        _presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance,
            Path.Combine(_root, "global", "characters"), Path.Combine(_root, "project", "characters"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private async Task SeedAsync()
    {
        foreach (var s in new[] { "code-review", "git-workflow", "csharp", "clean-architecture", "python", "machine-learning" })
            await _skills.CreateAsync(new SkillDraft { Name = s, Description = s + " skill" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft
        {
            Id = "software-developer",
            Description = "Dev",
            IsTemplate = true,
            BaseRole = "implementer",
            Skills = new[] { "code-review", "git-workflow" }
        }, SkillScope.Global);
    }

    private CharactersViewModel Characters() => new(_characters, _skills, _presets);

    [Fact]
    public async Task CreateFromTemplate_ThenAddOwnSkills_GivesUniqueSkillset()
    {
        await SeedAsync();
        var vm = Characters();
        await vm.RefreshAsync();
        Assert.Equal("software-developer", vm.ExtendsOptions[1]); // templates first

        vm.BeginCreate();
        vm.NewDisplayName = "Dana Dotnet";
        vm.NewDescription = ".NET dev";
        vm.NewExtends = "software-developer";
        await vm.CreateAsync();

        Assert.Equal("dana-dotnet", vm.SelectedCharacter?.Id);
        Assert.Equal("software-developer", vm.SelectedExtends);
        var inherited = vm.SkillChoices.First(c => c.Name == "code-review");
        Assert.True(inherited.IsInherited);
        Assert.False(inherited.CanTogglePin);
        Assert.StartsWith("from template software-developer", inherited.Hint);
        Assert.Contains("Add skill", vm.StatusText);

        foreach (var name in new[] { "csharp", "clean-architecture" })
        {
            await vm.BeginAddSkillAsync();
            Assert.DoesNotContain(vm.AvailableSkills, s => s.Name == "code-review"); // inherited: not offered again
            vm.SelectedLibrarySkill = vm.AvailableSkills.First(s => s.Name == name);
            await vm.ConfirmAddSkillAsync();
        }

        var dana = (await _characters.GetAsync("dana-dotnet"))!;
        Assert.Equal(new[] { "code-review", "git-workflow", "csharp", "clean-architecture" }, dana.Skills);
        Assert.Equal(new[] { "csharp", "clean-architecture" }, dana.OwnSkills);
        // Own skills are listed first, then the template's.
        Assert.Equal(new[] { "clean-architecture", "csharp", "code-review", "git-workflow" }, vm.SkillChoices.Select(c => c.Name));
        Assert.Equal("own", vm.SkillChoices.First(c => c.Name == "csharp").Source);
        // Another developer from the same template keeps its own, different skills.
        await _characters.CreateAsync(new CharacterDraft { Id = "mia", Extends = "software-developer", Skills = new[] { "python" } }, SkillScope.Global);
        Assert.DoesNotContain("csharp", (await _characters.GetAsync("mia"))!.Skills!);
    }

    [Fact]
    public async Task RemovingInheritedSkill_RemovesItForThisCharacterOnly_AndCanBeRestored()
    {
        await SeedAsync();
        await _characters.CreateAsync(new CharacterDraft { Id = "mia", Extends = "software-developer", Skills = new[] { "python", "machine-learning" } }, SkillScope.Global);
        var vm = Characters();
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "mia"));

        await vm.RemoveSkillAsync(vm.SkillChoices.First(c => c.Name == "git-workflow"));
        var mia = (await _characters.GetAsync("mia"))!;
        Assert.Equal(new[] { "git-workflow" }, mia.RemoveSkills);
        Assert.Contains("git-workflow", (await _characters.GetAsync("software-developer"))!.Skills!);
        var removedRow = vm.SkillChoices.First(c => c.Name == "git-workflow");
        Assert.True(removedRow.IsRemoved);
        Assert.False(removedRow.IsAssigned);
        Assert.Contains("removed", removedRow.Hint);
        Assert.Same(removedRow, vm.SkillChoices.Last()); // removed rows go to the bottom

        await vm.RestoreSkillAsync(removedRow);
        mia = (await _characters.GetAsync("mia"))!;
        Assert.Empty(mia.RemoveSkills);
        Assert.Equal("template:software-developer", mia.SkillSources["git-workflow"]);
        Assert.False(vm.SkillChoices.First(c => c.Name == "git-workflow").IsRemoved);
    }

    [Fact]
    public async Task ChangeExtendsAndTemplateFlag_FromHeader()
    {
        await SeedAsync();
        await _characters.CreateAsync(new CharacterDraft { Id = "solo", Skills = new[] { "python" } }, SkillScope.Global);
        var vm = Characters();
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "solo"));
        Assert.Equal(CharactersViewModel.NoTemplate, vm.SelectedExtends);

        vm.SelectedExtends = "software-developer";
        await vm.LastChoiceTask;
        Assert.Equal("software-developer", (await _characters.GetAsync("solo"))!.Extends);
        Assert.True(vm.SkillChoices.First(c => c.Name == "code-review").IsInherited);

        vm.SelectedIsTemplate = true;
        await vm.LastChoiceTask;
        Assert.True((await _characters.GetAsync("solo"))!.IsTemplate);

        vm.SelectedExtends = CharactersViewModel.NoTemplate;
        await vm.LastChoiceTask;
        Assert.Null((await _characters.GetAsync("solo"))!.Extends);
    }

    [Fact]
    public async Task CycleFromHeader_IsRejectedWithMessage()
    {
        await SeedAsync();
        await _characters.CreateAsync(new CharacterDraft { Id = "child", Extends = "software-developer" }, SkillScope.Global);
        var vm = Characters();
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "software-developer"));

        vm.SelectedExtends = "child";
        await vm.LastChoiceTask;

        Assert.Contains("cycle", vm.StatusText);
        Assert.Null((await _characters.GetAsync("software-developer"))!.Extends);
        Assert.Equal(CharactersViewModel.NoTemplate, vm.SelectedExtends);
    }

    [Fact]
    public async Task DeletingTemplate_WarnsAboutCharactersThatExtendIt()
    {
        await SeedAsync();
        await _characters.CreateAsync(new CharacterDraft { Id = "child", Extends = "software-developer" }, SkillScope.Global);
        var vm = Characters();
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "software-developer"));

        vm.RequestDelete();
        Assert.Contains("child extend it", vm.DeleteConfirmText);
    }
}
