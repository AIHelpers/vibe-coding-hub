using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

/// <summary>
/// Unique, precise skillsets: every character has its own skills (added with "Add skill"),
/// optionally on top of a template it extends, minus <c>remove-skills</c>.
/// </summary>
public class CharacterCompositionTests : IDisposable
{
    private readonly string _root;
    private readonly string _chars;

    public CharacterCompositionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "char-comp-" + Guid.NewGuid().ToString("N"));
        _chars = Path.Combine(_root, "global", "characters");
        Directory.CreateDirectory(_chars);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private CharacterRegistry Registry() =>
        new(NullLogger<CharacterRegistry>.Instance, _chars, presets: () => CharacterRegistryTests.Presets);

    private SkillRegistry Skills() =>
        new(NullLogger<SkillRegistry>.Instance, Path.Combine(_root, "skills"), Path.Combine(_root, "project-skills"));

    private void Char(string id, string frontmatter, string persona = "P") =>
        File.WriteAllText(Path.Combine(_chars, id + ".md"), $"---\nid: {id}\n{frontmatter}\n---\n{persona}");

    private void Team()
    {
        Char("software-developer", "description: Writes production code\ntemplate: true\nbase-role: implementer\nskills: [code-review-checklist, git-workflow]\npinned-skills: [team-conventions]",
            "You are a professional software developer.");
        Char("dana-dotnet", "description: .NET developer\nextends: software-developer\nskills: [csharp, clean-architecture, azure-functions]\nremove-skills: [git-workflow]",
            "You specialise in .NET backends.");
        Char("mia-ml", "description: ML engineer\nextends: software-developer\nskills: [python, machine-learning]\npinned-skills: [ml-experiment-log]",
            "You specialise in machine learning.");
    }

    [Fact]
    public async Task TwoDevelopers_ShareTemplate_ButHaveTheirOwnSkills()
    {
        Team();
        var r = Registry();

        var dana = (await r.GetAsync("dana-dotnet"))!;
        Assert.True(dana.IsValid, string.Join(" ", dana.ValidationErrors));
        Assert.Equal(new[] { "code-review-checklist", "csharp", "clean-architecture", "azure-functions" }, dana.Skills);
        Assert.Equal(new[] { "team-conventions" }, dana.PinnedSkills);
        Assert.Equal("template:software-developer", dana.SkillSources["code-review-checklist"]);
        Assert.Equal("own", dana.SkillSources["csharp"]);
        Assert.Equal("template:software-developer", dana.SkillSources["team-conventions"]);
        Assert.Equal("implementer", dana.BaseRole);
        Assert.Equal(new[] { "software-developer" }, dana.InheritanceChain);
        Assert.Equal("You are a professional software developer.\n\nYou specialise in .NET backends.", dana.Persona);
        Assert.Equal("You specialise in .NET backends.", dana.OwnPersona);
        Assert.False(dana.IsTemplate);

        var mia = (await r.GetAsync("mia-ml"))!;
        Assert.Equal(new[] { "code-review-checklist", "git-workflow", "python", "machine-learning" }, mia.Skills);
        Assert.Equal(new[] { "team-conventions", "ml-experiment-log" }, mia.PinnedSkills);
        Assert.Equal("own", mia.SkillSources["ml-experiment-log"]);

        Assert.True((await r.GetAsync("software-developer"))!.IsTemplate);
    }

    [Fact]
    public async Task MultiLevelInheritance_KeepsOriginOfSkills()
    {
        Team();
        Char("senior-dana", "description: Senior\nextends: dana-dotnet\nskills: [mentoring]");
        var c = (await Registry().GetAsync("senior-dana"))!;

        Assert.Equal(new[] { "dana-dotnet", "software-developer" }, c.InheritanceChain);
        Assert.Equal("template:software-developer", c.SkillSources["code-review-checklist"]);
        Assert.Equal("template:dana-dotnet", c.SkillSources["csharp"]);
        Assert.Equal("own", c.SkillSources["mentoring"]);
        Assert.DoesNotContain("git-workflow", c.Skills!); // removed one level up stays removed
    }

    [Fact]
    public async Task ChildOverridesInheritedSettings_ToolsUnionAlongChain()
    {
        Char("base", "description: b\ntemplate: true\nbase-role: implementer\nmodel: base-model\npermission-mode: ask\ntools: { add: [web_fetch], remove: [git] }");
        Char("child", "description: c\nextends: base\nmodel: child-model\ntools: { add: [browser_check] }");
        var c = (await Registry().GetAsync("child"))!;

        Assert.Equal("child-model", c.Model);
        Assert.Equal(PermissionMode.Ask, c.PermissionMode);
        Assert.Equal("implementer", c.BaseRole);
        Assert.Equal(new[] { "web_fetch", "browser_check" }, c.ToolsAdd);
        Assert.Equal(new[] { "git" }, c.ToolsRemove);
    }

    [Fact]
    public async Task ResolverUsesEffectiveSkillset()
    {
        Team();
        var dana = (await Registry().GetAsync("dana-dotnet"))!;
        var options = CharacterResolver.Apply(new AgentOptions(), dana, CharacterRegistryTests.Presets[1]);

        Assert.Equal(new[] { "code-review-checklist", "csharp", "clean-architecture", "azure-functions" }, options.AllowedSkills);
        Assert.Equal(new[] { "team-conventions" }, options.PinnedSkills);
        Assert.Contains("You are a professional software developer.", options.RoleSystemPrompt);
        Assert.Contains("You specialise in .NET backends.", options.RoleSystemPrompt);
        Assert.EndsWith("IMPLEMENTER-DUTIES", options.RoleSystemPrompt);
    }

    [Fact]
    public async Task ExtendingBuiltInRole_InheritsRoleButNotAllSkills()
    {
        Char("qa", "description: QA\nextends: planner\nskills: [exploratory-testing]");
        var c = (await Registry().GetAsync("qa"))!;
        Assert.Equal("planner", c.BaseRole);
        Assert.Equal(new[] { "exploratory-testing" }, c.Skills);
    }

    [Fact]
    public async Task InheritanceCycle_IsValidationError()
    {
        Char("a", "description: a\nextends: b");
        Char("b", "description: b\nextends: a");
        var r = Registry();
        var a = (await r.GetAsync("a"))!;
        Assert.False(a.IsValid);
        Assert.Contains(a.ValidationErrors, e => e.Contains("cycle") && e.Contains("a → b → a"));
        Assert.False((await r.GetAsync("b"))!.IsValid);
    }

    [Fact]
    public async Task UnknownTemplate_AndStrayRemoveSkills_AreWarnings()
    {
        Char("x", "description: x\nextends: ghost\nskills: [own]\nremove-skills: [never-inherited]");
        var c = (await Registry().GetAsync("x"))!;
        Assert.True(c.IsValid);
        Assert.Contains(c.Warnings, w => w.Contains("ghost"));
        Assert.Contains(c.Warnings, w => w.Contains("never-inherited"));
        Assert.Equal(new[] { "own" }, c.Skills);
    }

    [Fact]
    public async Task LegacySkillSetsField_IsIgnoredWithWarning()
    {
        Char("old", "description: old file\nskillsets: [dotnet-stack]\nskills: [csharp]");
        var c = (await Registry().GetAsync("old"))!;
        Assert.True(c.IsValid);
        Assert.Equal(new[] { "csharp" }, c.Skills);
        Assert.Contains(c.Warnings, w => w.Contains("'skillsets' is no longer supported"));
    }

    [Fact]
    public async Task CreateCharacterFromTemplate_WithOwnSkills()
    {
        Team();
        var r = Registry();
        var c = await r.CreateAsync(new CharacterDraft
        {
            Id = "leo-dotnet",
            Description = "Another .NET dev",
            Extends = "software-developer",
            Skills = new[] { "csharp", "efcore" }
        }, SkillScope.Global);

        Assert.Equal(new[] { "code-review-checklist", "git-workflow", "csharp", "efcore" }, c.Skills);
        Assert.Contains("software-developer", c.OwnPersona);
        Assert.DoesNotContain("skillsets", await r.LoadAsync("leo-dotnet"));

        await Assert.ThrowsAsync<SkillValidationException>(() => r.CreateAsync(new CharacterDraft { Id = "bad", Extends = "ghost" }, SkillScope.Global));
    }

    [Fact]
    public async Task UnassignInheritedSkill_AddsRemoveSkills_AndAssignRestoresIt()
    {
        Team();
        var r = Registry();

        var c = await r.UnassignSkillsAsync("mia-ml", new[] { "git-workflow", "python" });
        Assert.DoesNotContain("git-workflow", c.Skills!);
        Assert.DoesNotContain("python", c.Skills!);
        // python was her own skill: deleted from her list, not added to remove-skills.
        Assert.Equal(new[] { "git-workflow" }, c.RemoveSkills);

        c = await r.AssignSkillsAsync("mia-ml", new[] { "git-workflow" });
        Assert.Contains("git-workflow", c.Skills!);
        Assert.Empty(c.RemoveSkills);
        // Restored, not copied: it keeps coming from the template.
        Assert.Equal("template:software-developer", c.SkillSources["git-workflow"]);
        Assert.DoesNotContain("git-workflow", c.OwnSkills!);
    }

    [Fact]
    public async Task SetExtendsAndTemplate_EditFile_AndRejectCycles()
    {
        Team();
        var r = Registry();
        Char("solo", "description: s\nskills: [a]");

        var c = await r.SetExtendsAsync("solo", "software-developer");
        Assert.Equal("software-developer", c.Extends);
        Assert.Contains("code-review-checklist", c.Skills!);

        c = await r.SetExtendsAsync("solo", null);
        Assert.Null(c.Extends);
        Assert.Equal(new[] { "a" }, c.Skills);

        await Assert.ThrowsAsync<SkillValidationException>(() => r.SetExtendsAsync("software-developer", "dana-dotnet"));
        await Assert.ThrowsAsync<SkillValidationException>(() => r.SetExtendsAsync("solo", "solo"));
        await Assert.ThrowsAsync<SkillValidationException>(() => r.SetExtendsAsync("solo", "ghost"));

        c = await r.SetTemplateAsync("solo", true);
        Assert.True(c.IsTemplate);
        c = await r.SetTemplateAsync("solo", false);
        Assert.False(c.IsTemplate);
        Assert.DoesNotContain("template:", await r.LoadAsync("solo"));
    }

    // ===================== Add skill =====================

    [Fact]
    public async Task AddSkill_PicksExistingLibrarySkill()
    {
        Team();
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "docker", Description = "Containers" }, SkillScope.Global);
        var r = Registry();

        var result = await new SkillMaintenance(skills, r).AddSkillToCharacterAsync("dana-dotnet", "docker");

        Assert.False(result.CreatedSkill);
        Assert.False(result.WasAllSkills);
        Assert.Equal("docker", result.Skill.Name);
        Assert.Contains("docker", result.Character.Skills!);
        Assert.Equal("own", result.Character.SkillSources["docker"]);
        // Only Dana got it.
        Assert.DoesNotContain("docker", (await r.GetAsync("mia-ml"))!.Skills!);
    }

    [Fact]
    public async Task AddSkill_CreatesMissingSkill_ThenAssignsIt_Pinned()
    {
        Team();
        var skills = Skills();
        var r = Registry();

        var result = await new SkillMaintenance(skills, r).AddSkillToCharacterAsync("mia-ml", "pytorch", pinned: true,
            createIfMissing: new SkillDraft { Description = "Train models with PyTorch", Body = "Use torch.compile when it helps.\n" },
            createScope: SkillScope.Project);

        Assert.True(result.CreatedSkill);
        Assert.Equal(SkillScope.Project, result.Skill.Scope);
        Assert.Contains("Use torch.compile", await skills.LoadAsync("pytorch"));
        Assert.Contains("pytorch", result.Character.PinnedSkills);
        Assert.DoesNotContain("pytorch", result.Character.Skills!);
    }

    [Fact]
    public async Task AddSkill_Rejects_MissingSkillWithoutDraft_UnknownCharacter_AndBadName()
    {
        Team();
        var skills = Skills();
        var maintenance = new SkillMaintenance(skills, Registry());

        var ex = await Assert.ThrowsAsync<SkillValidationException>(() => maintenance.AddSkillToCharacterAsync("dana-dotnet", "ghost"));
        Assert.Contains("does not exist", ex.Message);
        await Assert.ThrowsAsync<SkillValidationException>(() =>
            maintenance.AddSkillToCharacterAsync("nobody", "x", createIfMissing: new SkillDraft { Description = "d" }));
        await Assert.ThrowsAsync<SkillValidationException>(() =>
            maintenance.AddSkillToCharacterAsync("dana-dotnet", "Not Kebab", createIfMissing: new SkillDraft { Description = "d" }));
        // Nothing was created for the unknown character.
        Assert.Null(await skills.GetAsync("x"));
    }

    [Fact]
    public async Task AddSkill_ToAllSkillsCharacter_SwitchesToExplicitList()
    {
        Char("wild", "description: w\nskills: [\"*\"]");
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "sql", Description = "SQL" }, SkillScope.Global);
        var r = Registry();
        Assert.Null((await r.GetAsync("wild"))!.Skills);

        var result = await new SkillMaintenance(skills, r).AddSkillToCharacterAsync("wild", "sql");

        Assert.True(result.WasAllSkills);
        Assert.Equal(new[] { "sql" }, result.Character.Skills);
    }

    [Fact]
    public async Task AddSkill_ToBuiltIn_CreatesOverrideKeepingItsRole()
    {
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "adr", Description = "ADRs" }, SkillScope.Global);
        var r = Registry();

        var result = await new SkillMaintenance(skills, r).AddSkillToCharacterAsync("planner", "adr");

        Assert.True(result.WasAllSkills);
        Assert.False(result.Character.IsBuiltIn);
        Assert.Equal("planner", result.Character.BaseRole);
        Assert.Equal(new[] { "adr" }, result.Character.Skills);
        Assert.True(File.Exists(Path.Combine(_chars, "planner.md")));
    }

    // ===================== Rename / delete / doctor =====================

    [Fact]
    public async Task SkillRenameAndDelete_RewriteOwnListsAndRemoveSkills()
    {
        Team();
        var r = Registry();

        var refs = await r.FindSkillReferencesAsync("csharp");
        var only = Assert.Single(refs);
        Assert.Equal("dana-dotnet", only.Label);

        // Two files change: the template's own list and dana's remove-skills.
        Assert.Equal(2, await r.RenameSkillReferencesAsync("git-workflow", "git-flow"));
        Assert.Contains("git-flow", (await r.GetAsync("software-developer"))!.Skills!);
        Assert.Equal(new[] { "git-flow" }, (await r.GetAsync("dana-dotnet"))!.RemoveSkills);

        Assert.Equal(1, await r.RemoveSkillReferencesAsync("csharp"));
        Assert.DoesNotContain("csharp", (await r.GetAsync("dana-dotnet"))!.Skills!);
    }

    [Fact]
    public async Task DanglingReferences_ReportedOncePerFile()
    {
        Team();
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "csharp", Description = "C#" }, SkillScope.Global);
        var dangling = await new SkillMaintenance(skills, Registry()).FindDanglingReferencesAsync();

        // code-review-checklist is missing: reported for the template only, not for every child.
        Assert.Single(dangling, d => d.SkillName == "code-review-checklist");
        Assert.Contains(dangling, d => d.CharacterId == "dana-dotnet" && d.SkillName == "clean-architecture");
        Assert.DoesNotContain(dangling, d => d.SkillName == "csharp");
    }

    [Fact]
    public async Task Doctor_CountsTemplates_AndReportsMissingSkills()
    {
        Team();
        var checks = await SkillDoctor.DiagnoseAsync(Skills(), Registry());
        Assert.Contains(checks, c => c.Name == "Templates" && c.Detail.Contains("1 template"));
        Assert.Contains(checks, c => c.Name == "Character 'dana-dotnet'" && c.Detail.Contains("csharp") && c.Status == DoctorStatus.Warning);
        Assert.DoesNotContain(checks, c => c.Name.Contains("Skill set"));
    }
}
