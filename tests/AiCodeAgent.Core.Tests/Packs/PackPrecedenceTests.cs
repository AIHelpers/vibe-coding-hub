using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Packs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Packs;

/// <summary>On a name clash: project > global > pack > built-in. Packs are read-only sources.</summary>
public class PackPrecedenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pack-prec-" + Guid.NewGuid().ToString("N"));
    private readonly string _globalSkills, _projectSkills, _globalChars, _projectChars, _globalFlows, _projectDir;
    private readonly List<PackSource> _packs = new();

    private static readonly AgentRolePreset[] Presets =
    {
        new() { Role = "planner", Description = "built-in planner", SystemPrompt = "BUILTIN", DefaultPermissionMode = PermissionMode.Plan, AllowedTools = new() { "read_file" } }
    };

    public PackPrecedenceTests()
    {
        _globalSkills = Path.Combine(_root, "g-skills");
        _projectDir = Path.Combine(_root, "project");
        _projectSkills = Path.Combine(_projectDir, ".aiagent", "skills");
        _globalChars = Path.Combine(_root, "g-chars");
        _projectChars = Path.Combine(_projectDir, ".aiagent", "characters");
        _globalFlows = Path.Combine(_root, "g-flows");
        foreach (var d in new[] { _globalSkills, _projectSkills, _globalChars, _projectChars, _globalFlows }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private PackSource AddPack(string name)
    {
        var source = new PackSource(name, Path.Combine(_root, "packs", name + "@v1"), "v1", "1.0.0");
        Directory.CreateDirectory(source.Root);
        _packs.Add(source);
        return source;
    }

    private static void Skill(string dir, string name, string description)
    {
        Directory.CreateDirectory(Path.Combine(dir, name));
        File.WriteAllText(Path.Combine(dir, name, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\nbody of {description}\n");
    }

    private static void Character(string dir, string id, string description)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".md"), $"---\nid: {id}\ndescription: {description}\nbase-role: planner\n---\nYou are {id}.\n");
    }

    private static void Flow(string dir, string name, string description)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"),
            "{ \"name\": \"" + name + "\", \"description\": \"" + description + "\", \"stages\": [ { \"name\": \"Plan\", \"role\": \"planner\", \"promptTemplate\": \"{task}\" } ] }");
    }

    private SkillRegistry Skills() => new(NullLogger<SkillRegistry>.Instance, _globalSkills, _projectSkills, packSources: () => _packs.ToList());
    private CharacterRegistry Characters() => new(NullLogger<CharacterRegistry>.Instance, _globalChars, _projectChars, () => Presets, () => _packs.ToList());
    private SdlcPipelineLoader Flows() => new(NullLogger<SdlcPipelineLoader>.Instance, _globalFlows, _projectDir, () => _packs.ToList());

    // ------------------------------------------------------------------ skills

    [Fact]
    public async Task Skills_PackSkillAppearsWithItsPack()
    {
        Skill(AddPack("team").SkillsDirectory, "api-design", "from pack");

        var skill = Assert.Single(await Skills().ListAllAsync(), s => s.Name == "api-design");

        Assert.Equal(SkillScope.Pack, skill.Scope);
        Assert.Equal("team", skill.PackName);
    }

    [Fact]
    public async Task Skills_GlobalBeatsPack_ProjectBeatsGlobal()
    {
        Skill(AddPack("team").SkillsDirectory, "shared", "pack");
        Skill(AddPack("team2").SkillsDirectory, "only-pack", "pack");
        Skill(_globalSkills, "shared", "global");
        Skill(_globalSkills, "deep", "global");
        Skill(_projectSkills, "deep", "project");
        Skill(AddPack("team3").SkillsDirectory, "deep", "pack");

        var all = await Skills().ListAllAsync();

        var shared = Assert.Single(all, s => s.Name == "shared");
        Assert.Equal(SkillScope.Global, shared.Scope);
        Assert.True(shared.ShadowsPack);
        var deep = Assert.Single(all, s => s.Name == "deep");
        Assert.Equal(SkillScope.Project, deep.Scope);
        Assert.True(deep.ShadowsGlobal);
        Assert.True(deep.ShadowsPack);
        Assert.Equal(SkillScope.Pack, Assert.Single(all, s => s.Name == "only-pack").Scope);
    }

    [Fact]
    public async Task Skills_PackSkillsAreReadOnly()
    {
        Skill(AddPack("team").SkillsDirectory, "locked", "pack");
        var registry = Skills();

        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UpdateAsync("locked", "---\nname: locked\ndescription: x\n---\nnew"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.RenameAsync("locked", "renamed"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.DeleteAsync("locked"));
        Assert.Contains("body of pack", await registry.LoadAsync("locked"));
    }

    [Fact]
    public async Task Skills_RemovingThePackRemovesItsSkills()
    {
        Skill(AddPack("team").SkillsDirectory, "api-design", "pack");
        var registry = Skills();
        Assert.Contains(await registry.ListAllAsync(), s => s.Name == "api-design");

        _packs.Clear();
        registry.Refresh();

        Assert.DoesNotContain(await registry.ListAllAsync(), s => s.Name == "api-design");
    }

    // ------------------------------------------------------------------ characters

    [Fact]
    public async Task Characters_PackCharacterAppearsWithBadgeData()
    {
        Character(AddPack("team").CharactersDirectory, "pack-architect", "from pack");

        var c = Assert.Single(await Characters().ListAsync(), x => x.Id == "pack-architect");

        Assert.Equal(SkillScope.Pack, c.Scope);
        Assert.Equal("team", c.PackName);
        Assert.True(c.IsFromPack);
        Assert.False(c.IsBuiltIn);
    }

    [Fact]
    public async Task Characters_PackBeatsBuiltIn_GlobalBeatsPack_ProjectBeatsGlobal()
    {
        Character(AddPack("team").CharactersDirectory, "planner", "pack planner");
        Character(AddPack("team").CharactersDirectory, "alex", "pack alex");
        Character(_globalChars, "alex", "global alex");
        Character(_globalChars, "bo", "global bo");
        Character(_projectChars, "bo", "project bo");

        var all = await Characters().ListAsync();

        Assert.Equal("pack planner", Assert.Single(all, c => c.Id == "planner").Description);
        Assert.Equal("global alex", Assert.Single(all, c => c.Id == "alex").Description);
        Assert.Equal("project bo", Assert.Single(all, c => c.Id == "bo").Description);
    }

    [Fact]
    public async Task Characters_PackCharactersAreReadOnly_AndSurviveSkillRenames()
    {
        var source = AddPack("team");
        Character(source.CharactersDirectory, "locked", "pack");
        var file = Path.Combine(source.CharactersDirectory, "locked.md");
        var before = File.ReadAllText(file);
        var registry = Characters();

        await Assert.ThrowsAnyAsync<Exception>(() => registry.UpdateAsync("locked", "---\nid: locked\ndescription: x\n---\nnew"));
        await Assert.ThrowsAnyAsync<Exception>(() => registry.DeleteAsync("locked"));
        await registry.RenameSkillReferencesAsync("a", "b");
        await registry.RemoveSkillReferencesAsync("a");

        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public async Task Characters_RemovingThePackRemovesTheCharacters()
    {
        Character(AddPack("team").CharactersDirectory, "pack-architect", "pack");
        var registry = Characters();
        Assert.Contains(await registry.ListAsync(), c => c.Id == "pack-architect");

        _packs.Clear();
        registry.Refresh();

        Assert.DoesNotContain(await registry.ListAsync(), c => c.Id == "pack-architect");
    }

    // ------------------------------------------------------------------ flows

    [Fact]
    public void Flows_PackFlowAppearsWithItsPack()
    {
        Flow(AddPack("team").PipelinesDirectory, "pack-review", "pack");

        var entry = Flows().GetEntry("pack-review");

        Assert.NotNull(entry);
        Assert.Equal(PipelineScope.Pack, entry!.Scope);
        Assert.Equal("team", entry.PackName);
        Assert.Equal("pack:team", entry.ScopeLabel);
    }

    [Fact]
    public void Flows_PackBeatsBuiltIn_GlobalBeatsPack_ProjectBeatsGlobal()
    {
        Flow(AddPack("team").PipelinesDirectory, "full-sdlc", "pack full");
        Flow(AddPack("team").PipelinesDirectory, "clash", "pack clash");
        Flow(_globalFlows, "clash", "global clash");
        Flow(_globalFlows, "deep", "global deep");
        Flow(Path.Combine(_projectDir, ".aiagent", "pipelines"), "deep", "project deep");

        var loader = Flows();

        Assert.Equal(PipelineScope.Pack, loader.GetEntry("full-sdlc")!.Scope);
        Assert.Equal(PipelineScope.BuiltIn, loader.GetEntry("full-sdlc")!.Overrides);
        Assert.Equal("global clash", loader.GetEntry("clash")!.Pipeline.Description);
        Assert.Equal(PipelineScope.Pack, loader.GetEntry("clash")!.Overrides);
        Assert.Equal("project deep", loader.GetEntry("deep")!.Pipeline.Description);
        Assert.Equal(PipelineScope.Project, loader.GetEntry("deep")!.Scope);
        Assert.Equal("pack full", loader.Pipelines["full-sdlc"].Description);
    }

    [Fact]
    public void Flows_PackFlowsCannotBeDeleted_AndRemovingThePackRestoresTheBuiltIn()
    {
        Flow(AddPack("team").PipelinesDirectory, "full-sdlc", "pack full");
        var loader = Flows();

        Assert.Throws<InvalidOperationException>(() => loader.DeletePipeline("full-sdlc"));

        _packs.Clear();
        loader.Reload();
        Assert.Equal(PipelineScope.BuiltIn, loader.GetEntry("full-sdlc")!.Scope);
    }

    // ------------------------------------------------------------------ between packs

    [Fact]
    public async Task TwoPacks_TheLaterNameWins()
    {
        Skill(AddPack("alpha-pack").SkillsDirectory, "dup", "alpha");
        Skill(AddPack("zeta-pack").SkillsDirectory, "dup", "zeta");

        var skill = Assert.Single(await Skills().ListAllAsync(), s => s.Name == "dup");

        Assert.Equal("zeta-pack", skill.PackName);
    }

    [Fact]
    public void Sources_AreSortedByName_AndOnlyExistingFoldersAreUsed()
    {
        var store = new PackStore(Path.Combine(_root, "store"));
        foreach (var (name, make) in new[] { ("zeta-pack", true), ("alpha-pack", true), ("gone-pack", false) })
        {
            var dir = $"{name}@v1";
            if (make) Directory.CreateDirectory(Path.Combine(store.Root, dir));
            store.Add(new InstalledPack { Name = name, Url = "u", Ref = "v1", Commit = "abc123abc123abc1", Pinned = true, Directory = dir, InstalledAt = DateTimeOffset.UtcNow }, makeDefault: true);
        }

        Assert.Equal(new[] { "alpha-pack", "zeta-pack" }, store.Sources().Select(s => s.Name).ToArray());
    }
}
