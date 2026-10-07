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

/// <summary>Project Knowledge panel: Skills tab CRUD and the Characters tab.</summary>
public class SkillsAndCharactersPanelTests : IDisposable
{
    private readonly string _root;
    private readonly SkillRegistry _skills;
    private readonly CharacterRegistry _characters;
    private readonly RolePresetLoader _presets;

    public SkillsAndCharactersPanelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pk-panel-" + Guid.NewGuid().ToString("N"));
        _skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance,
            Path.Combine(_root, "global", "skills"), Path.Combine(_root, "project", "skills"));
        _presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        _characters = new CharacterRegistry(_presets, NullLogger<CharacterRegistry>.Instance,
            Path.Combine(_root, "global", "characters"), Path.Combine(_root, "project", "characters"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private ProjectKnowledgeViewModel Panel() => new(
        skillRegistry: _skills,
        characterRegistry: _characters,
        characters: new CharactersViewModel(_characters, _skills, _presets));

    // ===================== Skills tab =====================

    [Fact]
    public async Task CreateSkill_ThroughRegistry_SelectsIt()
    {
        var vm = Panel();
        vm.BeginCreateSkill();
        vm.NewSkillName = "Release Notes";
        vm.NewSkillDescription = "Write release notes";
        vm.NewSkillIsProject = true;

        await vm.CreateSkillAsync();

        Assert.False(vm.IsCreatingSkill);
        var item = Assert.Single(vm.Skills);
        Assert.Equal("release-notes", item.Name);
        Assert.True(item.IsProjectSkill);
        Assert.Equal("release-notes", vm.SelectedSkill?.Name);
        Assert.Contains("description: Write release notes", vm.SelectedSkillContent);
        Assert.False(vm.SelectedSkillIsDirty);
    }

    [Fact]
    public async Task SaveSkill_InvalidContent_IsRejectedWithMessage()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "s", Description = "d" }, SkillScope.Global);
        var vm = Panel();
        await vm.RefreshSkillsAsync();
        await vm.SelectSkillAsync(vm.Skills[0]);

        vm.SelectedSkillContent = "no frontmatter at all";
        await vm.SaveSelectedSkillAsync();

        Assert.StartsWith("Not saved:", vm.SkillsStatusText);
        Assert.True(vm.SelectedSkillIsDirty);
        Assert.Contains("description: d", File.ReadAllText(_skills.GetSkillPath("s")!));
    }

    [Fact]
    public async Task SaveSkill_ValidContent_Writes()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "s", Description = "d" }, SkillScope.Global);
        var vm = Panel();
        await vm.RefreshSkillsAsync();
        await vm.SelectSkillAsync(vm.Skills[0]);

        vm.SelectedSkillContent = "---\nname: s\ndescription: updated\n---\nBody";
        await vm.SaveSelectedSkillAsync();

        Assert.False(vm.SelectedSkillIsDirty);
        Assert.Equal("updated", vm.Skills[0].Description);
    }

    [Fact]
    public async Task Filter_MatchesNameDescriptionAndTag()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "alpha", Description = "first", Tags = new[] { "docs" } }, SkillScope.Global);
        await _skills.CreateAsync(new SkillDraft { Name = "beta", Description = "second" }, SkillScope.Global);
        var vm = Panel();
        await vm.RefreshSkillsAsync();

        vm.SkillFilter = "#docs";
        Assert.Equal(new[] { "alpha" }, vm.Skills.Select(s => s.Name));
        vm.SkillFilter = "second";
        Assert.Equal(new[] { "beta" }, vm.Skills.Select(s => s.Name));
        vm.SkillFilter = "";
        Assert.Equal(2, vm.Skills.Count);
    }

    [Fact]
    public async Task RenameSkill_UpdatesCharacters()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "old", Description = "d" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "alex", Skills = new[] { "old" } }, SkillScope.Global);
        var vm = Panel();
        await vm.RefreshSkillsAsync();
        await vm.SelectSkillAsync(vm.Skills[0]);
        Assert.Contains("alex", vm.SelectedSkillDetails);

        vm.BeginRenameSkill();
        vm.RenameSkillName = "new-name";
        await vm.ConfirmRenameSkillAsync();

        Assert.False(vm.IsRenamingSkill);
        Assert.Equal("new-name", vm.SelectedSkill?.Name);
        Assert.Contains("updated 1 character", vm.SkillsStatusText);
        Assert.Equal(new[] { "new-name" }, (await _characters.GetAsync("alex"))!.Skills);
    }

    [Fact]
    public async Task DeleteSkill_RequiresConfirmation_AndCanKeepReferences()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "s", Description = "d" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "alex", Skills = new[] { "s" } }, SkillScope.Global);
        var vm = Panel();
        await vm.RefreshSkillsAsync();
        await vm.SelectSkillAsync(vm.Skills[0]);

        vm.RequestDeleteSkill();
        Assert.True(vm.IsConfirmingSkillDelete);
        Assert.Contains("alex", vm.SkillDeleteConfirmText);
        vm.CancelDeleteSkill();
        Assert.NotNull(await _skills.GetAsync("s"));

        vm.RequestDeleteSkill();
        vm.RemoveSkillFromCharacters = false;
        await vm.ConfirmDeleteSkillAsync();

        Assert.Null(await _skills.GetAsync("s"));
        Assert.Empty(vm.Skills);
        Assert.Null(vm.SelectedSkill);
        Assert.Equal(new[] { "s" }, (await _characters.GetAsync("alex"))!.Skills); // kept as a dangling reference
    }

    [Fact]
    public async Task SkillList_ShowsUsageAndProblems()
    {
        Directory.CreateDirectory(Path.Combine(_root, "global", "skills", "broken"));
        File.WriteAllText(Path.Combine(_root, "global", "skills", "broken", "SKILL.md"), "no frontmatter");
        var vm = Panel();
        await vm.RefreshSkillsAsync();

        var item = Assert.Single(vm.Skills);
        Assert.False(item.IsValid);
        Assert.Contains("invalid", item.ScopeLabel);
        Assert.Contains(item.Problems, p => p.StartsWith("Error:"));
    }

    [Theory]
    [InlineData("Release Notes", "release-notes")]
    [InlineData("  my__skill--x ", "my-skill-x")]
    [InlineData("!!!", "")]
    public void NormalizeSkillName_Works(string input, string expected)
        => Assert.Equal(expected, ProjectKnowledgeViewModel.NormalizeSkillName(input));

    // ===================== Characters tab =====================

    [Fact]
    public async Task Characters_ListIncludesBuiltIns()
    {
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();

        Assert.Contains(vm.Characters, c => c.Id == "planner" && c.Info.IsBuiltIn);
        Assert.Contains("", vm.BaseRoles);
        Assert.Contains("implementer", vm.BaseRoles);
    }

    [Fact]
    public async Task CreateCharacter_SuggestsIdFromDisplayName_AndSelectsIt()
    {
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        vm.BeginCreate();
        vm.NewDisplayName = "Alex Architect";
        Assert.Equal("alex-architect", vm.NewId);
        vm.NewBaseRole = "planner";
        vm.NewDescription = "Designs";

        await vm.CreateAsync();

        Assert.False(vm.IsCreating);
        Assert.Equal("alex-architect", vm.SelectedCharacter?.Id);
        Assert.Contains("base-role: planner", vm.SelectedContent);
        Assert.Contains("permission: plan", vm.SelectedSummary);
        Assert.True(File.Exists(Path.Combine(_root, "project", "characters", "alex-architect.md")));
    }

    [Fact]
    public async Task CreateCharacter_InvalidId_ShowsError()
    {
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        vm.BeginCreate();
        vm.NewId = "Bad Id";
        await vm.CreateAsync();
        Assert.True(vm.IsCreating);
        Assert.Contains("kebab-case", vm.StatusText);
    }

    [Fact]
    public async Task AddSkill_FromLibrary_PinAndRemove_PersistToFile()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "tdd", Description = "Test first" }, SkillScope.Global);
        await _skills.CreateAsync(new SkillDraft { Name = "conventions", Description = "Team rules" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "sam" }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "sam"));
        Assert.Empty(vm.SkillChoices);
        Assert.False(vm.HasSkillChoices);
        Assert.Equal("Skills of sam", vm.SkillsHeader);

        await vm.BeginAddSkillAsync();
        Assert.True(vm.IsAddingSkill);
        Assert.True(vm.AddSkillFromLibrary);
        Assert.Equal(new[] { "conventions", "tdd" }, vm.AvailableSkills.Select(s => s.Name));
        vm.SelectedLibrarySkill = vm.AvailableSkills.First(s => s.Name == "tdd");
        await vm.ConfirmAddSkillAsync();

        Assert.False(vm.IsAddingSkill);
        Assert.Equal(new[] { "tdd" }, (await _characters.GetAsync("sam"))!.Skills);
        var tdd = Assert.Single(vm.SkillChoices);
        Assert.Equal("Test first", tdd.Hint);
        Assert.True(tdd.CanTogglePin);
        // Assigned skills are no longer offered.
        Assert.Equal(new[] { "conventions" }, vm.AvailableSkills.Select(s => s.Name));

        await vm.BeginAddSkillAsync();
        vm.SelectedLibrarySkill = vm.AvailableSkills.Single();
        vm.AddSkillPinned = true;
        await vm.ConfirmAddSkillAsync();
        Assert.Equal(new[] { "conventions" }, (await _characters.GetAsync("sam"))!.PinnedSkills);
        Assert.True(vm.SkillChoices.First(c => c.Name == "conventions").IsPinned);

        // Unpin from the list: it stays, loaded on demand.
        vm.SkillChoices.First(c => c.Name == "conventions").IsPinned = false;
        await vm.LastChoiceTask;
        var sam = (await _characters.GetAsync("sam"))!;
        Assert.Empty(sam.PinnedSkills);
        Assert.Equal(new[] { "tdd", "conventions" }, sam.Skills);

        await vm.RemoveSkillAsync(vm.SkillChoices.First(c => c.Name == "tdd"));
        Assert.Equal(new[] { "conventions" }, (await _characters.GetAsync("sam"))!.Skills);
        Assert.DoesNotContain(vm.SkillChoices, c => c.Name == "tdd");
    }

    [Fact]
    public async Task AddSkill_CreateNew_AddsToLibraryAndCharacter()
    {
        await _characters.CreateAsync(new CharacterDraft { Id = "dana" }, SkillScope.Project);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "dana"));

        await vm.BeginAddSkillAsync();
        Assert.True(vm.AddSkillCreatesNew); // empty library: straight to "Create new"
        Assert.True(vm.NewSkillIsProject);  // project character → project library by default
        vm.NewSkillName = "Clean Architecture";
        await vm.ConfirmAddSkillAsync();
        Assert.True(vm.IsAddingSkill);
        Assert.Contains("Describe", vm.StatusText);

        vm.NewSkillDescription = "Layer .NET services the clean way";
        vm.NewSkillInstructions = "Keep the Domain free of infrastructure.";
        await vm.ConfirmAddSkillAsync();

        Assert.False(vm.IsAddingSkill);
        var skill = (await _skills.GetAsync("clean-architecture"))!;
        Assert.Equal(SkillScope.Project, skill.Scope);
        Assert.Contains("Keep the Domain free", await _skills.LoadAsync("clean-architecture"));
        Assert.Equal(new[] { "clean-architecture" }, (await _characters.GetAsync("dana"))!.Skills);
        Assert.Contains("Created it in the project skill library", vm.StatusText);
    }

    [Fact]
    public async Task AddSkill_FromLibrary_RequiresAPick()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "tdd", Description = "d" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "sam" }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "sam"));

        await vm.BeginAddSkillAsync();
        vm.LibraryFilter = "nothing-matches";
        Assert.False(vm.HasAvailableSkills);
        vm.LibraryFilter = "";
        await vm.ConfirmAddSkillAsync();
        Assert.True(vm.IsAddingSkill);
        Assert.Contains("Pick a skill", vm.StatusText);
        Assert.Empty((await _characters.GetAsync("sam"))!.Skills!);
    }

    [Fact]
    public async Task AddSkill_OnBuiltIn_CreatesOverride()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "adr", Description = "ADRs" }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "planner"));
        Assert.True(vm.SelectedIsBuiltIn);
        Assert.True(vm.SelectedUsesAllSkills);
        Assert.Empty(vm.SkillChoices);
        Assert.Contains("every skill", vm.AddSkillNote);

        await vm.BeginAddSkillAsync();
        vm.SelectedLibrarySkill = vm.AvailableSkills.Single();
        await vm.ConfirmAddSkillAsync();

        var planner = (await _characters.GetAsync("planner"))!;
        Assert.False(planner.IsBuiltIn);
        Assert.Equal(new[] { "adr" }, planner.Skills);
        Assert.False(vm.SelectedIsBuiltIn);
        Assert.Contains("now uses only the skills added to it", vm.StatusText);
    }

    [Fact]
    public async Task SkillList_ShowsMissingSkillReferences()
    {
        await _characters.CreateAsync(new CharacterDraft { Id = "x", Skills = new[] { "ghost" } }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "x"));

        var ghost = Assert.Single(vm.SkillChoices);
        Assert.True(ghost.IsMissing);
        Assert.True(ghost.IsAssigned);
        Assert.Contains("ghost", vm.SelectedProblems);

        await vm.RemoveSkillAsync(ghost);
        Assert.Empty((await _characters.GetAsync("x"))!.Skills!);
    }

    [Fact]
    public async Task AddSkill_IsBlockedWhileEditorHasUnsavedChanges()
    {
        await _skills.CreateAsync(new SkillDraft { Name = "tdd", Description = "d" }, SkillScope.Global);
        await _characters.CreateAsync(new CharacterDraft { Id = "sam" }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "sam"));
        vm.SelectedContent += "\nunsaved";

        await vm.BeginAddSkillAsync();
        vm.SelectedLibrarySkill = vm.AvailableSkills.Single();
        await vm.ConfirmAddSkillAsync();

        Assert.Empty((await _characters.GetAsync("sam"))!.Skills!);
        Assert.Contains("Save", vm.StatusText);
        Assert.True(vm.IsAddingSkill);
    }

    [Fact]
    public async Task SaveAndDeleteCharacter()
    {
        await _characters.CreateAsync(new CharacterDraft { Id = "sam", Description = "old" }, SkillScope.Global);
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "sam"));

        vm.SelectedContent = "---\nid: sam\ndescription: new\n---\nPersona";
        await vm.SaveAsync();
        Assert.Equal("new", (await _characters.GetAsync("sam"))!.Description);

        vm.SelectedContent = "---\nid: other\n---\n";
        await vm.SaveAsync();
        Assert.StartsWith("Not saved:", vm.StatusText);

        vm.RequestDelete();
        Assert.True(vm.IsConfirmingDelete);
        await vm.ConfirmDeleteAsync();
        Assert.Null(await _characters.GetAsync("sam"));
        Assert.Null(vm.SelectedCharacter);
    }

    [Fact]
    public async Task BuiltIn_CannotBeDeletedOrSaved()
    {
        var vm = new CharactersViewModel(_characters, _skills, _presets);
        await vm.RefreshAsync();
        await vm.SelectCharacterAsync(vm.Characters.First(c => c.Id == "reviewer"));

        Assert.False(vm.CanEditSelected);
        vm.RequestDelete();
        Assert.False(vm.IsConfirmingDelete);
    }

    [Fact]
    public async Task Panel_CharactersTab_SwitchesAndRefreshes()
    {
        var vm = Panel();
        await vm.ShowCharactersTabCommand.ExecuteAsync(null);
        Assert.True(vm.IsCharactersTabActive);
        Assert.False(vm.IsSkillsTabActive);
        Assert.NotEmpty(vm.Characters.Characters);
    }
}
