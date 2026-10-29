using System.Security.Cryptography;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Import;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Import;

public class ImportersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "import-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo, _globalSkills, _userClaude, _projectSkills;

    public ImportersTests()
    {
        _repo = Path.Combine(_root, "repo");
        _globalSkills = Path.Combine(_root, "global-skills");
        _userClaude = Path.Combine(_root, "home", ".claude", "skills");
        _projectSkills = SkillRegistry.GetDefaultProjectSkillsDir(_repo);
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_globalSkills);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private ProjectImporter Importer(bool withUser = true) => new(_repo, _projectSkills, _globalSkills, withUser ? _userClaude : null);

    private SkillRegistry Registry() => new(NullLogger<SkillRegistry>.Instance, _globalSkills, _projectSkills);

    private void Write(string relative, string content, string? root = null)
    {
        var path = Path.Combine(root ?? _repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repo with all five sources (and a user-level Claude skill).</summary>
    private void AllSources()
    {
        // Claude Code / Agent Skills
        Write(".claude/skills/api-design/SKILL.md", "---\nname: api-design\ndescription: Design REST APIs\nallowed-tools: [read_file]\ncustom-key: keep-me\n---\n# API design\nUse nouns.\n");
        Write(".claude/skills/api-design/templates/openapi.yaml", "openapi: 3.0.0\n");
        Write("../home/.claude/skills/personal-notes/SKILL.md", "---\nname: personal-notes\ndescription: My own notes style\n---\nWrite short notes.\n");
        // CLAUDE.md
        Write("CLAUDE.md", "# Project\nBuild with dotnet build.\n");
        // Cursor
        Write(".cursor/rules/style-guide.mdc", "---\ndescription: C# style\nglobs: [\"**/*.cs\"]\nalwaysApply: false\n---\nUse var when the type is obvious.\n");
        Write(".cursor/rules/always-on.mdc", "---\ndescription: Always on\nalwaysApply: true\n---\nNever commit secrets.\n");
        Write(".cursorrules", "Prefer small functions.\n");
        // Kiro
        Write(".kiro/steering/tech.md", "---\ninclusion: always\n---\n# Tech stack\nWe use .NET 10.\n");
        Write(".kiro/steering/testing.md", "---\ninclusion: fileMatch\nfileMatchPattern: \"tests/**\"\n---\nWrite xunit tests.\n");
        Write(".kiro/steering/runbook.md", "---\ninclusion: manual\n---\nHow to deploy.\n");
        // Copilot
        Write(".github/copilot-instructions.md", "Answer in English.\nPrefer async APIs.\n");
    }

    private Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Plan_FindsEverySource_AndWritesNothing()
    {
        AllSources();
        var before = Hashes(_root);

        var plan = Importer().Plan();

        Assert.Equal(Enum.GetValues<ImportSource>().ToHashSet(), plan.Items.Select(i => i.Source).ToHashSet());
        Assert.All(plan.Items, i => Assert.True(i.IsNew));
        Assert.Equal(before, Hashes(_root));
        Assert.False(Directory.Exists(_projectSkills));
        Assert.Contains("claude-skills:api-design", plan.Items.Select(i => i.Id));
        Assert.Contains("claude-user-skills:personal-notes", plan.Items.Select(i => i.Id));
        Assert.Contains("claude-md", plan.Items.Select(i => i.Id));
        Assert.Contains("copilot-instructions", plan.Items.Select(i => i.Id));
        Assert.Contains("cursorrules", plan.Items.Select(i => i.Id));
        Assert.Contains("kiro:tech.md", plan.Items.Select(i => i.Id));
        Assert.Contains("api-design", plan.Render());
    }

    [Fact]
    public void Plan_WithNothingToImport_IsEmpty()
    {
        var plan = Importer().Plan();

        Assert.True(plan.IsEmpty);
        Assert.Contains("Nothing to import", plan.Render());
    }

    [Fact]
    public void Plan_CanBeLimitedToOneSource()
    {
        AllSources();

        var plan = Importer().Plan(new[] { ImportSource.KiroSteering });

        Assert.All(plan.Items, i => Assert.Equal(ImportSource.KiroSteering, i.Source));
        Assert.Equal(3, plan.Items.Count);
    }

    // ------------------------------------------------------------------ acceptance

    [Fact]
    public async Task AllFiveSources_ImportIntoValidSkillsAndAgentsMd_AndDoctorIsClean()
    {
        AllSources();
        Write("AGENTS.md", "# AGENTS.md\nExisting memory.\n");
        var importer = Importer();

        var outcomes = await importer.ApplyAsync(importer.Plan());

        Assert.DoesNotContain(outcomes, o => o.Kind == ImportResultKind.Failed);
        var skills = Registry();
        var all = await skills.ListAllAsync();
        Assert.All(all, s => Assert.True(s.IsValid, string.Join(",", s.ValidationErrors)));
        Assert.All(all, s => Assert.Empty(s.Warnings));
        Assert.Equal(new[] { "api-design", "personal-notes", "style-guide", "team-conventions", "testing", "runbook" }.OrderBy(x => x),
            all.Select(s => s.Name).OrderBy(x => x));

        var agents = File.ReadAllText(Path.Combine(_repo, "AGENTS.md"));
        Assert.StartsWith("# AGENTS.md\nExisting memory.", agents);
        Assert.Contains("## Imported from CLAUDE.md", agents);
        Assert.Contains("Build with dotnet build.", agents);
        Assert.Contains("## Imported from copilot-instructions.md", agents);
        Assert.Contains("Prefer async APIs.", agents);

        var checks = await SkillDoctor.DiagnoseAsync(skills);
        Assert.DoesNotContain(checks, c => c.Status != DoctorStatus.Ok);
    }

    [Fact]
    public async Task RunningTheImportTwice_IsANoOp()
    {
        AllSources();
        var importer = Importer();
        await importer.ApplyAsync(importer.Plan());
        var afterFirst = Hashes(_root);

        var second = importer.Plan();
        var outcomes = await importer.ApplyAsync(second);

        Assert.All(second.Items, i => Assert.Equal(ImportStatus.AlreadyImported, i.Status));
        Assert.Empty(second.Pending);
        Assert.All(outcomes, o => Assert.Equal(ImportResultKind.AlreadyImported, o.Kind));
        Assert.Equal(afterFirst, Hashes(_root));
        Assert.Contains("already imported", second.Render());
    }

    [Fact]
    public async Task SourceFiles_AreNeverChangedOrDeleted()
    {
        AllSources();
        var sources = new[] { ".claude", ".cursor", ".kiro", ".github" }.Select(d => Path.Combine(_repo, d)).ToList();
        var before = sources.SelectMany(d => Hashes(d)).ToDictionary(p => p.Key, p => p.Value);
        var claudeMd = File.ReadAllText(Path.Combine(_repo, "CLAUDE.md"));
        var userBefore = Hashes(Path.Combine(_root, "home"));
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var after = sources.SelectMany(d => Hashes(d)).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(before, after);
        Assert.Equal(claudeMd, File.ReadAllText(Path.Combine(_repo, "CLAUDE.md")));
        Assert.Equal(userBefore, Hashes(Path.Combine(_root, "home")));
    }

    // ------------------------------------------------------------------ Claude Code skills

    [Fact]
    public async Task ClaudeSkills_AreCopiedWithTheirFrontMatterAndResources()
    {
        AllSources();
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan(new[] { ImportSource.ClaudeSkills }));

        var copy = File.ReadAllText(Path.Combine(_projectSkills, "api-design", "SKILL.md"));
        var doc = Frontmatter.Parse(copy);
        Assert.Equal("api-design", doc.GetString("name"));
        Assert.Equal("Design REST APIs", doc.GetString("description"));
        Assert.Equal("keep-me", doc.GetString("custom-key"));
        Assert.Equal(new[] { "read_file" }, doc.GetList("allowed-tools"));
        Assert.Equal("claude-skills:api-design", doc.GetString("imported-from"));
        Assert.Contains("Use nouns.", doc.Body);
        Assert.True(File.Exists(Path.Combine(_projectSkills, "api-design", "templates", "openapi.yaml")));
    }

    [Fact]
    public async Task UserClaudeSkills_GoToTheGlobalSkillsFolder()
    {
        AllSources();
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan(new[] { ImportSource.ClaudeSkills }));

        Assert.True(File.Exists(Path.Combine(_globalSkills, "personal-notes", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(_projectSkills, "personal-notes")));
    }

    [Fact]
    public void UserClaudeSkills_AreIgnoredWhenNoUserFolderIsGiven()
    {
        AllSources();

        var plan = Importer(withUser: false).Plan(new[] { ImportSource.ClaudeSkills });

        Assert.DoesNotContain(plan.Items, i => i.Target == ImportTarget.GlobalSkill);
    }

    [Fact]
    public async Task ClaudeSkill_WithoutDescription_GetsOneFromItsText()
    {
        Write(".claude/skills/bare/SKILL.md", "Always run the linter before committing.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var skill = Assert.Single(await Registry().ListAllAsync());
        Assert.True(skill.IsValid);
        Assert.Contains("linter", skill.Description);
        Assert.Empty(skill.Warnings);
    }

    // ------------------------------------------------------------------ Cursor

    [Fact]
    public async Task Cursor_RuleBecomesASkill_WithDescriptionAndGlobsFromTheFrontMatter()
    {
        Write(".cursor/rules/style-guide.mdc", "---\ndescription: C# style\nglobs: [\"**/*.cs\"]\nalwaysApply: false\n---\nUse var when the type is obvious.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var skills = await Registry().ListAllAsync();
        var skill = Assert.Single(skills);
        Assert.Equal("style-guide", skill.Name);
        Assert.Equal("C# style", skill.Description);
        Assert.False(skill.DisableModelInvocation);
        var text = File.ReadAllText(skill.FilePath!);
        Assert.Contains("Applies to files matching: **/*.cs", text);
        Assert.Contains("Use var when the type is obvious.", text);
    }

    [Fact]
    public async Task Cursor_AlwaysApplyRules_AreMergedIntoTeamConventions()
    {
        Write(".cursor/rules/always-on.mdc", "---\ndescription: Always on\nalwaysApply: true\n---\nNever commit secrets.\n");
        Write(".cursor/rules/second.mdc", "---\nalwaysApply: true\n---\nKeep PRs small.\n");
        Write(".cursorrules", "Prefer small functions.\n");
        var importer = Importer();

        var outcomes = await importer.ApplyAsync(importer.Plan());

        Assert.All(outcomes, o => Assert.Equal(ImportResultKind.Imported, o.Kind));
        var skill = Assert.Single(await Registry().ListAllAsync());
        Assert.Equal("team-conventions", skill.Name);
        Assert.True(skill.IsValid);
        var text = File.ReadAllText(skill.FilePath!);
        Assert.Contains("Never commit secrets.", text);
        Assert.Contains("Keep PRs small.", text);
        Assert.Contains("Prefer small functions.", text);
        Assert.Contains("imported-from: cursor:always-on.mdc -->", text);
    }

    [Fact]
    public async Task Cursor_RuleWithNoDescriptionGlobsOrAlwaysApply_IsManualOnly()
    {
        Write(".cursor/rules/manual-rule.mdc", "---\nalwaysApply: false\n---\nOnly when asked.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var skill = Assert.Single(await Registry().ListAllAsync());
        Assert.True(skill.DisableModelInvocation);
        Assert.True(skill.IsValid);
        Assert.NotEmpty(skill.Description);
    }

    [Fact]
    public async Task Cursor_AppendsToAnExistingTeamConventionsSkill_WithoutLosingIt()
    {
        Write(".aiagent/skills/team-conventions/SKILL.md", "---\nname: team-conventions\ndescription: Ours\n---\nOriginal rule.\n");
        Write(".cursor/rules/always-on.mdc", "---\nalwaysApply: true\n---\nNew rule.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var text = File.ReadAllText(Path.Combine(_projectSkills, "team-conventions", "SKILL.md"));
        Assert.Contains("Original rule.", text);
        Assert.Contains("New rule.", text);
        Assert.Contains("description: Ours", text);
    }

    // ------------------------------------------------------------------ Kiro

    [Fact]
    public async Task Kiro_InclusionAlwaysIsPinnedStyle_FileMatchAndManualBecomeSkills()
    {
        AllSources();
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan(new[] { ImportSource.KiroSteering }));

        var skills = (await Registry().ListAllAsync()).ToDictionary(s => s.Name);
        Assert.Equal(new[] { "runbook", "team-conventions", "testing" }, skills.Keys.OrderBy(x => x));
        Assert.Contains("We use .NET 10.", File.ReadAllText(skills["team-conventions"].FilePath!));
        Assert.Contains("tests/**", skills["testing"].Description);
        Assert.False(skills["testing"].DisableModelInvocation);
        Assert.True(skills["runbook"].DisableModelInvocation);
        Assert.All(skills.Values, s => Assert.True(s.IsValid));
    }

    // ------------------------------------------------------------------ AGENTS.md sections

    [Fact]
    public async Task ClaudeMdAndCopilot_AppendSectionsToAgentsMd_CreatingItWhenMissing()
    {
        Write("CLAUDE.md", "Use dotnet build.\n");
        Write(".github/copilot-instructions.md", "Be concise.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        var agents = File.ReadAllText(Path.Combine(_repo, "AGENTS.md"));
        Assert.StartsWith("# AGENTS.md", agents);
        Assert.Contains("<!-- imported-from: claude-md -->", agents);
        Assert.Contains("<!-- imported-from: copilot-instructions -->", agents);
        Assert.Contains("Use dotnet build.", agents);
        Assert.Contains("Be concise.", agents);
    }

    [Fact]
    public async Task AgentsMdSections_UseTheExistingMemoryFile()
    {
        Write("AGENT.md", "# Memory\n");
        Write("CLAUDE.md", "Hello.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        Assert.Contains("Hello.", File.ReadAllText(Path.Combine(_repo, "AGENT.md")));
        Assert.False(File.Exists(Path.Combine(_repo, "AGENTS.md")));
    }

    // ------------------------------------------------------------------ names and clashes

    [Fact]
    public async Task Names_AreNormalisedToKebabCase()
    {
        Write(".cursor/rules/My_Rule File.mdc", "---\ndescription: d\n---\nBody.\n");
        var importer = Importer();

        await importer.ApplyAsync(importer.Plan());

        Assert.Equal("my-rule-file", Assert.Single(await Registry().ListAllAsync()).Name);
    }

    [Fact]
    public async Task ClashWithAnExistingSkill_IsResolvedWithASuffix_AndTheExistingOneIsKept()
    {
        Write(".aiagent/skills/style-guide/SKILL.md", "---\nname: style-guide\ndescription: Mine\n---\nMy own.\n");
        Write(".cursor/rules/style-guide.mdc", "---\ndescription: Theirs\n---\nTheir rule.\n");
        Write(".claude/skills/style-guide/SKILL.md", "---\nname: style-guide\ndescription: Claude's\n---\nClaude rule.\n");
        var importer = Importer();
        var plan = importer.Plan();

        Assert.Equal(new[] { "style-guide-2", "style-guide-3" }, plan.Items.Select(i => i.TargetName).OrderBy(x => x));
        await importer.ApplyAsync(plan);

        Assert.Contains("My own.", File.ReadAllText(Path.Combine(_projectSkills, "style-guide", "SKILL.md")));
        var skills = await Registry().ListAllAsync();
        Assert.Equal(3, skills.Count);
        Assert.All(skills, s => Assert.True(s.IsValid));
        Assert.All(skills, s => Assert.Empty(s.Warnings)); // the name in the front matter matches the folder
    }

    [Fact]
    public async Task ClashSuffix_IsStable_OnTheSecondRun()
    {
        Write(".aiagent/skills/style-guide/SKILL.md", "---\nname: style-guide\ndescription: Mine\n---\nMy own.\n");
        Write(".cursor/rules/style-guide.mdc", "---\ndescription: Theirs\n---\nTheir rule.\n");
        var importer = Importer();
        await importer.ApplyAsync(importer.Plan());

        var second = importer.Plan();

        var item = Assert.Single(second.Items);
        Assert.Equal(ImportStatus.AlreadyImported, item.Status);
        Assert.Equal("style-guide-2", item.TargetName);
    }

    // ------------------------------------------------------------------ selection and odd input

    [Fact]
    public async Task UnselectedItems_AreNotImported()
    {
        AllSources();
        var importer = Importer();
        var plan = importer.Plan();
        foreach (var item in plan.Items.Where(i => i.Source == ImportSource.CursorRules)) item.Selected = false;

        var outcomes = await importer.ApplyAsync(plan);

        Assert.DoesNotContain(await Registry().ListAllAsync(), s => s.Name == "style-guide");
        Assert.All(outcomes.Where(o => o.Item.Source == ImportSource.CursorRules), o => Assert.Equal(ImportResultKind.Skipped, o.Kind));
        Assert.Contains(await Registry().ListAllAsync(), s => s.Name == "api-design");
        // they can still be imported later
        Assert.Contains(importer.Plan().Items, i => i.Source == ImportSource.CursorRules && i.IsNew);
    }

    [Fact]
    public void EmptyFiles_AreSkippedWithAWarning()
    {
        Write("CLAUDE.md", "  \n");
        Write(".cursorrules", "");
        Write(".cursor/rules/empty.mdc", "---\ndescription: d\n---\n");

        var plan = Importer().Plan();

        Assert.Empty(plan.Items);
        Assert.Equal(3, plan.Warnings.Count);
    }

    [Fact]
    public async Task Cursor_FromTheMdExtensionAndSubfolders_IsFoundToo()
    {
        Write(".cursor/rules/backend/db.md", "---\ndescription: Database rules\n---\nUse migrations.\n");
        var importer = Importer();

        var plan = importer.Plan();
        await importer.ApplyAsync(plan);

        Assert.Equal("db", Assert.Single(await Registry().ListAllAsync()).Name);
    }

    [Theory]
    [InlineData("all", 5)]
    [InlineData("claude", 1)]
    [InlineData("claude-md", 1)]
    [InlineData("cursor", 1)]
    [InlineData("kiro", 1)]
    [InlineData("copilot", 1)]
    public void SourceNames_ParseForTheCommandLine(string text, int count)
    {
        Assert.True(ProjectImporter.TryParseSources(text, out var sources));
        Assert.Equal(count, sources!.Count);
    }
}
