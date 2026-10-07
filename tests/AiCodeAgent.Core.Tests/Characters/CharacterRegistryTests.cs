using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

public class CharacterRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly string _projectDir;

    internal static readonly AgentRolePreset[] Presets =
    {
        new() { Role = "planner", Description = "Plans", SystemPrompt = "PLANNER-DUTIES", DefaultPermissionMode = PermissionMode.Plan, AllowedTools = new() { "read_file", "grep" } },
        new() { Role = "implementer", Description = "Builds", SystemPrompt = "IMPLEMENTER-DUTIES", DefaultPermissionMode = PermissionMode.AutoEdit, AllowedTools = new() { "read_file", "write_file", "execute_command" } }
    };

    public CharacterRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "char-tests-" + Guid.NewGuid().ToString("N"));
        _globalDir = Path.Combine(_root, "global");
        _projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private CharacterRegistry Registry() => new(NullLogger<CharacterRegistry>.Instance, _globalDir, _projectDir, () => Presets);

    private void Write(string dir, string id, string content) => File.WriteAllText(Path.Combine(dir, id + ".md"), content);

    [Fact]
    public async Task BuiltInPresets_AppearAsReadOnlyCharacters()
    {
        var all = await Registry().ListAsync();
        var planner = Assert.Single(all, c => c.Id == "planner");
        Assert.True(planner.IsBuiltIn);
        Assert.Equal("planner", planner.BaseRole);
        Assert.Null(planner.Skills); // all skills, as before
        Assert.Equal("Planner", planner.DisplayName);
    }

    [Fact]
    public async Task ParsesFullCharacterFile()
    {
        Write(_globalDir, "alex-architect", """
            ---
            id: alex-architect
            display-name: Alex — Architect
            avatar: 🧭
            description: Designs; never edits code
            base-role: planner
            model: claude-sonnet
            permission-mode: plan
            tools: { add: [web_fetch], remove: [grep] }
            skills: [architecture-review, adr-writer]
            pinned-skills:
              - team-conventions
            ---
            You are Alex, a pragmatic architect.
            """);

        var c = await Registry().GetAsync("alex-architect");

        Assert.NotNull(c);
        Assert.True(c!.IsValid);
        Assert.Equal("Alex — Architect", c.DisplayName);
        Assert.Equal("🧭", c.Avatar);
        Assert.Equal("planner", c.BaseRole);
        Assert.Equal("claude-sonnet", c.Model);
        Assert.Equal(PermissionMode.Plan, c.PermissionMode);
        Assert.Equal(new[] { "web_fetch" }, c.ToolsAdd);
        Assert.Equal(new[] { "grep" }, c.ToolsRemove);
        Assert.Null(c.Tools);
        Assert.Equal(new[] { "architecture-review", "adr-writer" }, c.Skills);
        Assert.Equal(new[] { "team-conventions" }, c.PinnedSkills);
        Assert.Equal("You are Alex, a pragmatic architect.", c.Persona);
        Assert.Equal("🧭 Alex — Architect", c.Label);
    }

    [Fact]
    public async Task ExplicitToolList_AndAllSkillsToken()
    {
        Write(_globalDir, "c", "---\nid: c\ndescription: d\ntools: [read_file, git]\nskills: [\"*\"]\n---\nP");
        var c = (await Registry().GetAsync("c"))!;
        Assert.Equal(new[] { "read_file", "git" }, c.Tools);
        Assert.Null(c.Skills);
    }

    [Fact]
    public async Task MissingSkillsField_MeansNoSkills()
    {
        Write(_globalDir, "c", "---\nid: c\ndescription: d\n---\nP");
        var c = (await Registry().GetAsync("c"))!;
        Assert.NotNull(c.Skills);
        Assert.Empty(c.Skills!);
    }

    [Fact]
    public async Task InvalidPermissionMode_IsValidationError_UnknownRoleIsWarning()
    {
        Write(_globalDir, "c", "---\nid: c\ndescription: d\nbase-role: wizard\npermission-mode: yolo\n---\nP");
        var c = (await Registry().GetAsync("c"))!;
        Assert.False(c.IsValid);
        Assert.Contains(c.ValidationErrors, e => e.Contains("yolo"));
        Assert.Contains(c.Warnings, w => w.Contains("wizard"));
    }

    [Fact]
    public async Task ProjectOverridesGlobal_AndUserFileOverridesBuiltIn()
    {
        Write(_globalDir, "sam", "---\nid: sam\ndescription: global\n---\nG");
        Write(_projectDir, "sam", "---\nid: sam\ndescription: project\n---\nP");
        Write(_globalDir, "planner", "---\nid: planner\ndescription: custom planner\nbase-role: planner\nskills: [x]\n---\nP");

        var registry = Registry();
        var sam = (await registry.GetAsync("sam"))!;
        Assert.Equal("project", sam.Description);
        Assert.Equal(SkillScope.Project, sam.Scope);
        Assert.True(sam.Overrides);

        var planner = (await registry.GetAsync("planner"))!;
        Assert.False(planner.IsBuiltIn);
        Assert.True(planner.Overrides);
        Assert.Equal(new[] { "x" }, planner.Skills);
    }

    [Fact]
    public async Task CreateAsync_WritesParsableFile()
    {
        var registry = Registry();
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        var c = await registry.CreateAsync(new CharacterDraft
        {
            Id = "sam-dev",
            DisplayName = "Sam: Developer",
            Avatar = "🛠",
            Description = "Implements features",
            BaseRole = "implementer",
            PermissionMode = PermissionMode.AutoEdit,
            Skills = new[] { "tdd" },
            PinnedSkills = new[] { "conventions" },
            Persona = "You are Sam."
        }, SkillScope.Project);

        Assert.Equal("Sam: Developer", c.DisplayName);
        Assert.Equal(PermissionMode.AutoEdit, c.PermissionMode);
        Assert.Equal(new[] { "tdd" }, c.Skills);
        Assert.Equal(new[] { "conventions" }, c.PinnedSkills);
        Assert.Equal("You are Sam.", c.Persona);
        Assert.True(File.Exists(Path.Combine(_projectDir, "sam-dev.md")));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task CreateAsync_Rejects_BadId_UnknownRole_Duplicate_BuiltInScope()
    {
        var registry = Registry();
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.CreateAsync(new CharacterDraft { Id = "Bad Id" }, SkillScope.Global));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.CreateAsync(new CharacterDraft { Id = "x", BaseRole = "wizard" }, SkillScope.Global));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.CreateAsync(new CharacterDraft { Id = "x" }, SkillScope.BuiltIn));
        await registry.CreateAsync(new CharacterDraft { Id = "x" }, SkillScope.Global);
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.CreateAsync(new CharacterDraft { Id = "x" }, SkillScope.Global));
    }

    [Fact]
    public async Task UpdateAsync_ValidatesAndWrites()
    {
        var registry = Registry();
        await registry.CreateAsync(new CharacterDraft { Id = "x", Description = "old" }, SkillScope.Global);

        var updated = await registry.UpdateAsync("x", "---\nid: x\ndescription: new\n---\nNew persona");
        Assert.Equal("new", updated.Description);
        Assert.Equal("New persona", updated.Persona);

        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UpdateAsync("x", "---\nid: y\n---\n"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UpdateAsync("x", "---\nid: x\npermission-mode: nope\n---\n"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UpdateAsync("planner", "---\nid: planner\n---\n"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_RevealsBuiltIn_AndRefusesBuiltIns()
    {
        var registry = Registry();
        Write(_globalDir, "planner", "---\nid: planner\ndescription: custom\nbase-role: planner\n---\nP");

        Assert.True(await registry.DeleteAsync("planner"));
        Assert.True((await registry.GetAsync("planner"))!.IsBuiltIn);
        Assert.False(await registry.DeleteAsync("planner"));
        Assert.False(await registry.DeleteAsync("ghost"));
    }

    [Fact]
    public async Task AssignSkills_AddsToList_MovesBetweenPinnedAndListed_PreservesPersona()
    {
        var registry = Registry();
        await registry.CreateAsync(new CharacterDraft { Id = "x", Skills = new[] { "a" }, Persona = "Persona text\nline 2\n" }, SkillScope.Global);

        var c = await registry.AssignSkillsAsync("x", new[] { "b", "a" });
        Assert.Equal(new[] { "a", "b" }, c.Skills);

        c = await registry.AssignSkillsAsync("x", new[] { "a" }, pinned: true);
        Assert.Equal(new[] { "b" }, c.Skills);
        Assert.Equal(new[] { "a" }, c.PinnedSkills);
        Assert.Equal("Persona text\nline 2", c.Persona);

        c = await registry.UnassignSkillsAsync("x", new[] { "a", "b" });
        Assert.Empty(c.Skills!);
        Assert.Empty(c.PinnedSkills);
    }

    [Fact]
    public async Task AssignSkills_ToBuiltIn_CreatesGlobalOverrideKeepingRole()
    {
        var registry = Registry();
        var c = await registry.AssignSkillsAsync("planner", new[] { "adr-writer" });

        Assert.False(c.IsBuiltIn);
        Assert.Equal(SkillScope.Global, c.Scope);
        Assert.Equal("planner", c.BaseRole);
        Assert.Equal(new[] { "adr-writer" }, c.Skills);
        Assert.True(File.Exists(Path.Combine(_globalDir, "planner.md")));
    }

    [Fact]
    public async Task AssignOrUnassign_UnknownCharacter_Throws()
    {
        var registry = Registry();
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.AssignSkillsAsync("ghost", new[] { "a" }));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UnassignSkillsAsync("ghost", new[] { "a" }));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UnassignSkillsAsync("planner", new[] { "a" }));
    }

    [Fact]
    public async Task SkillReferences_FindRenameRemove_AcrossScopes()
    {
        Write(_globalDir, "one", "---\nid: one\ndescription: d\nskills: [shared, other]\n---\nP1");
        Write(_projectDir, "two", "---\nid: two\ndescription: d\nskills: [x]\npinned-skills: [shared]\n---\nP2");
        var registry = Registry();

        var refs = await registry.FindSkillReferencesAsync("shared");
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.CharacterId == "two" && r.Pinned && r.Scope == SkillScope.Project);

        Assert.Equal(2, await registry.RenameSkillReferencesAsync("shared", "renamed"));
        Assert.Equal(new[] { "renamed", "other" }, (await registry.GetAsync("one"))!.Skills);
        Assert.Equal(new[] { "renamed" }, (await registry.GetAsync("two"))!.PinnedSkills);
        Assert.Equal("P2", (await registry.GetAsync("two"))!.Persona);

        Assert.Equal(2, await registry.RemoveSkillReferencesAsync("renamed"));
        Assert.Empty(await registry.FindSkillReferencesAsync("renamed"));
        Assert.Equal(new[] { "other" }, (await registry.GetAsync("one"))!.Skills);
    }

    [Theory]
    [InlineData("ask", PermissionMode.Ask)]
    [InlineData("auto-edit", PermissionMode.AutoEdit)]
    [InlineData("Full_Auto", PermissionMode.FullAuto)]
    [InlineData("plan", PermissionMode.Plan)]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void ParsePermissionMode_Works(string text, PermissionMode? expected)
        => Assert.Equal(expected, CharacterRegistry.ParsePermissionMode(text));

    [Fact]
    public void FormatPermissionMode_RoundTrips()
    {
        foreach (var mode in Enum.GetValues<PermissionMode>())
            Assert.Equal(mode, CharacterRegistry.ParsePermissionMode(CharacterRegistry.FormatPermissionMode(mode)));
    }

    // ===================== SkillMaintenance =====================

    private SkillRegistry Skills() => new(NullLogger<SkillRegistry>.Instance, Path.Combine(_root, "skills-global"), Path.Combine(_root, "skills-project"));

    [Fact]
    public async Task Maintenance_RenameSkill_UpdatesCharacters()
    {
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "old", Description = "d" }, SkillScope.Global);
        Write(_globalDir, "c", "---\nid: c\ndescription: d\nskills: [old]\n---\nP");
        var characters = Registry();

        var result = await new SkillMaintenance(skills, characters).RenameAsync("old", "new");

        Assert.Equal("new", result.Skill.Name);
        Assert.Equal(1, result.CharactersUpdated);
        Assert.Equal(new[] { "new" }, (await characters.GetAsync("c"))!.Skills);
    }

    [Fact]
    public async Task Maintenance_DeleteSkill_RemovesReferences_UnlessShadowedCopyRemains()
    {
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "s", Description = "global" }, SkillScope.Global);
        await skills.CreateAsync(new SkillDraft { Name = "s", Description = "project" }, SkillScope.Project);
        Write(_globalDir, "c", "---\nid: c\ndescription: d\nskills: [s]\n---\nP");
        var characters = Registry();
        var maintenance = new SkillMaintenance(skills, characters);

        var first = await maintenance.DeleteAsync("s"); // project copy; global still exists
        Assert.True(first.Deleted);
        Assert.Single(first.ReferencedBy);
        Assert.Equal(0, first.CharactersUpdated);
        Assert.Equal(new[] { "s" }, (await characters.GetAsync("c"))!.Skills);

        var second = await maintenance.DeleteAsync("s");
        Assert.Equal(1, second.CharactersUpdated);
        Assert.Empty((await characters.GetAsync("c"))!.Skills!);

        Assert.False((await maintenance.DeleteAsync("s")).Deleted);
    }

    [Fact]
    public async Task Maintenance_DeleteSkill_KeepReferences()
    {
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "s", Description = "d" }, SkillScope.Global);
        Write(_globalDir, "c", "---\nid: c\ndescription: d\nskills: [s]\n---\nP");
        var characters = Registry();
        var maintenance = new SkillMaintenance(skills, characters);

        await maintenance.DeleteAsync("s", removeReferences: false);

        var dangling = Assert.Single(await maintenance.FindDanglingReferencesAsync());
        Assert.Equal(new DanglingSkillReference("c", "s"), dangling);
    }
}
