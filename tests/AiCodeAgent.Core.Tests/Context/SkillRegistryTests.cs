using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Context;

public class SkillRegistryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalDir;
    private readonly string _projectDir;

    public SkillRegistryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skill-tests-" + Guid.NewGuid().ToString("N"));
        _globalDir = Path.Combine(_tempRoot, "global", "skills");
        _projectDir = Path.Combine(_tempRoot, "project", ".aiagent", "skills");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private void WriteSkill(string dir, string name, string content)
    {
        var skillDir = Path.Combine(dir, name);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), content);
    }

    [Fact]
    public async Task ListAsync_ReturnsVisibleSkills()
    {
        WriteSkill(_globalDir, "commit-helper", """
            ---
            name: commit-helper
            description: Generates conventional commit messages
            ---
            # Commit Helper
            Follow conventions.
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir,
            projectSkillsDir: null);

        var skills = await registry.ListAsync();

        var skill = Assert.Single(skills);
        Assert.Equal("commit-helper", skill.Name);
        Assert.Equal("Generates conventional commit messages", skill.Description);
        Assert.False(skill.DisableModelInvocation);
    }

    [Fact]
    public async Task ListAsync_ExcludesManualOnlySkills()
    {
        WriteSkill(_globalDir, "secret-skill", """
            ---
            name: secret-skill
            description: Should not appear in list
            disable-model-invocation: true
            ---
            Body.
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var skills = await registry.ListAsync();
        Assert.Empty(skills);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsFullContent()
    {
        WriteSkill(_globalDir, "reviewer", """
            ---
            name: reviewer
            description: Code review checklist
            ---
            # Review
            1. Check naming
            2. Check tests
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var invocation = await registry.InvokeAsync("reviewer", args: "file.cs");
        Assert.NotNull(invocation);
        Assert.Equal("reviewer", invocation!.Name);
        Assert.Contains("# Review", invocation.Content);
        Assert.Contains("1. Check naming", invocation.Content);
        Assert.Equal("file.cs", invocation.Args);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsNullForMissingSkill()
    {
        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var invocation = await registry.InvokeAsync("nonexistent");
        Assert.Null(invocation);
    }

    [Fact]
    public async Task ProjectSkill_OverridesGlobalSkill()
    {
        WriteSkill(_globalDir, "builder", """
            ---
            name: builder
            description: Global builder
            ---
            Global content.
            """);
        WriteSkill(_projectDir, "builder", """
            ---
            name: builder
            description: Project builder
            ---
            Project content.
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir,
            projectSkillsDir: _projectDir);

        var skills = await registry.ListAsync();
        var skill = Assert.Single(skills);
        Assert.Equal("Project builder", skill.Description);

        var content = await registry.LoadAsync("builder");
        Assert.Contains("Project content.", content);
    }

    [Fact]
    public async Task Overrides_ShowHiddenSkill()
    {
        WriteSkill(_globalDir, "hidden-skill", """
            ---
            name: hidden-skill
            description: Hidden by default
            disable-model-invocation: true
            ---
            Body.
            """);

        var overrides = new Dictionary<string, string>
        {
            ["hidden-skill"] = "visible"
        };

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir,
            overrides: overrides);

        var skills = await registry.ListAsync();
        var skill = Assert.Single(skills);
        Assert.Equal("hidden-skill", skill.Name);
    }

    [Fact]
    public async Task Overrides_HideVisibleSkill()
    {
        WriteSkill(_globalDir, "normal-skill", """
            ---
            name: normal-skill
            description: Visible by default
            ---
            Body.
            """);

        var overrides = new Dictionary<string, string>
        {
            ["normal-skill"] = "hidden"
        };

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir,
            overrides: overrides);

        var skills = await registry.ListAsync();
        Assert.Empty(skills);
    }

    [Fact]
    public async Task GetSkillPath_ReturnsFilePath()
    {
        WriteSkill(_globalDir, "finder", """
            ---
            name: finder
            description: Find things
            ---
            Body.
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var path = registry.GetSkillPath("finder");
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("SKILL.md", path);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyForMissingSkill()
    {
        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var content = await registry.LoadAsync("missing");
        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void SplitFrontmatter_ParsesYamlHeader()
    {
        var content = """
            ---
            name: test-skill
            description: A test
            ---
            # Body content here
            """;
        var (frontmatter, body) = SkillRegistry.SplitFrontmatter(content);
        Assert.NotNull(frontmatter);
        Assert.Contains("name: test-skill", frontmatter);
        Assert.Contains("description: A test", frontmatter);
        Assert.Contains("# Body content here", body);
    }

    [Fact]
    public void SplitFrontmatter_ReturnsNullWhenNoFrontmatter()
    {
        var content = "# Just markdown\nNo frontmatter.";
        var (frontmatter, body) = SkillRegistry.SplitFrontmatter(content);
        Assert.Null(frontmatter);
        Assert.Equal(content, body);
    }

    [Fact]
    public void GetFrontmatterValue_ExtractsValue()
    {
        var frontmatter = "name: my-skill\ndescription: \"A description\"\ndisable-model-invocation: true";
        Assert.Equal("my-skill", SkillRegistry.GetFrontmatterValue(frontmatter, "name"));
        Assert.Equal("A description", SkillRegistry.GetFrontmatterValue(frontmatter, "description"));
        Assert.Equal("true", SkillRegistry.GetFrontmatterValue(frontmatter, "disable-model-invocation"));
    }

    [Fact]
    public void GetFrontmatterValue_ReturnsNullForMissingKey()
    {
        var frontmatter = "name: my-skill";
        Assert.Null(SkillRegistry.GetFrontmatterValue(frontmatter, "description"));
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyWhenNoSkillsDir()
    {
        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: Path.Combine(_tempRoot, "nonexistent"));

        var skills = await registry.ListAsync();
        Assert.Empty(skills);
    }

    [Fact]
    public async Task SkillsAreSortedByName()
    {
        WriteSkill(_globalDir, "zebra", """
            ---
            name: zebra
            description: Z skill
            ---
            Body.
            """);
        WriteSkill(_globalDir, "alpha", """
            ---
            name: alpha
            description: A skill
            ---
            Body.
            """);
        WriteSkill(_globalDir, "mid", """
            ---
            name: mid
            description: M skill
            ---
            Body.
            """);

        var registry = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            globalSkillsDir: _globalDir);

        var skills = await registry.ListAsync();
        Assert.Equal(3, skills.Count);
        Assert.Equal("alpha", skills[0].Name);
        Assert.Equal("mid", skills[1].Name);
        Assert.Equal("zebra", skills[2].Name);
    }

    // ===================== Name/scope behavior =====================

    private SkillRegistry Registry(Dictionary<string, string>? overrides = null) => new(
        NullLogger<SkillRegistry>.Instance,
        globalSkillsDir: _globalDir,
        projectSkillsDir: _projectDir,
        overrides: overrides);

    [Fact]
    public async Task FrontmatterNameMismatch_UsesFolderName_AndWarns()
    {
        WriteSkill(_globalDir, "folder-name", """
            ---
            name: other-name
            description: Mismatched
            ---
            Body.
            """);

        var registry = Registry();
        var skill = Assert.Single(await registry.ListAsync());

        Assert.Equal("folder-name", skill.Name);
        Assert.Contains(skill.Warnings, w => w.Contains("other-name"));
        // Lookups by the listed name must work (they silently failed before).
        Assert.NotNull(await registry.InvokeAsync("folder-name"));
        Assert.NotNull(registry.GetSkillPath("folder-name"));
    }

    [Fact]
    public async Task ParsesTagsVersionAndAllowedTools()
    {
        WriteSkill(_globalDir, "tagged", """
            ---
            name: tagged
            description: d
            tags: [docs, git]
            version: 3
            allowed-tools:
              - git
              - read_file
            ---
            Body.
            """);

        var skill = Assert.Single(await Registry().ListAsync());
        Assert.Equal(new[] { "docs", "git" }, skill.Tags);
        Assert.Equal(3, skill.Version);
        Assert.Equal(new[] { "git", "read_file" }, skill.AllowedTools);
        Assert.Equal(SkillScope.Global, skill.Scope);
    }

    [Fact]
    public async Task ProjectOverridesGlobal_AndIsMarkedAsShadowing()
    {
        WriteSkill(_globalDir, "shared", "---\nname: shared\ndescription: global\n---\nG");
        WriteSkill(_projectDir, "shared", "---\nname: shared\ndescription: project\n---\nP");

        var skill = Assert.Single(await Registry().ListAsync());
        Assert.Equal("project", skill.Description);
        Assert.Equal(SkillScope.Project, skill.Scope);
        Assert.True(skill.ShadowsGlobal);
    }

    [Fact]
    public async Task MissingFrontmatter_IsListedInAllButHiddenFromModel()
    {
        WriteSkill(_globalDir, "broken", "# no frontmatter");

        var registry = Registry();
        Assert.Empty(await registry.ListAsync());
        var all = Assert.Single(await registry.ListAllAsync());
        Assert.False(all.IsValid);
    }

    [Fact]
    public async Task ListAll_IncludesManualOnlySkills()
    {
        WriteSkill(_globalDir, "manual", "---\nname: manual\ndescription: d\ndisable-model-invocation: true\n---\nB");
        var registry = Registry();
        Assert.Empty(await registry.ListAsync());
        Assert.Single(await registry.ListAllAsync());
    }

    [Fact]
    public async Task InvokeAsync_ReturnsBodyWithoutFrontmatter()
    {
        WriteSkill(_globalDir, "s", "---\nname: s\ndescription: d\n---\n# Steps\nDo it.");
        var inv = await Registry().InvokeAsync("s", "arg");
        Assert.NotNull(inv);
        Assert.DoesNotContain("description:", inv!.Content);
        Assert.Contains("Do it.", inv.Content);
        Assert.Contains("<skill name=\"s\"", inv.ToPromptBlock());
        Assert.Contains("Arguments: arg", inv.ToPromptBlock());
    }

    [Fact]
    public void ComposeUserMessage_PrefixesSkills()
    {
        var text = SkillInvocation.ComposeUserMessage(
            new[] { new SkillInvocation { Name = "a", Content = "Do A" } }, "my request");
        Assert.Contains("<skill name=\"a\">", text);
        Assert.EndsWith("my request", text);
        Assert.Equal("plain", SkillInvocation.ComposeUserMessage(Array.Empty<SkillInvocation>(), "plain"));
    }

    // ===================== CRUD =====================

    [Fact]
    public async Task CreateAsync_WritesSkillFile_AndRaisesChanged()
    {
        var registry = Registry();
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        var info = await registry.CreateAsync(new SkillDraft
        {
            Name = "release-notes",
            Description = "Write release notes: from git log",
            Tags = new[] { "docs" }
        }, SkillScope.Project);

        Assert.Equal("release-notes", info.Name);
        Assert.Equal(SkillScope.Project, info.Scope);
        Assert.Equal("Write release notes: from git log", info.Description);
        Assert.Equal(new[] { "docs" }, info.Tags);
        Assert.True(File.Exists(Path.Combine(_projectDir, "release-notes", "SKILL.md")));
        Assert.Equal(1, changed);
        Assert.Single(await registry.ListAsync());
    }

    [Theory]
    [InlineData("Bad Name", "desc")]
    [InlineData("good-name", "")]
    public async Task CreateAsync_RejectsInvalidDrafts(string name, string description)
    {
        var ex = await Assert.ThrowsAsync<SkillValidationException>(() =>
            Registry().CreateAsync(new SkillDraft { Name = name, Description = description }, SkillScope.Global));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateInSameScope_ButAllowsOtherScope()
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "dup", Description = "d" }, SkillScope.Global);
        await Assert.ThrowsAsync<SkillValidationException>(() =>
            registry.CreateAsync(new SkillDraft { Name = "dup", Description = "d" }, SkillScope.Global));

        var project = await registry.CreateAsync(new SkillDraft { Name = "dup", Description = "p" }, SkillScope.Project);
        Assert.True(project.ShadowsGlobal);
    }

    [Fact]
    public async Task CreateAsync_ProjectScopeWithoutProjectDir_Throws()
    {
        var registry = new SkillRegistry(NullLogger<SkillRegistry>.Instance, globalSkillsDir: _globalDir);
        await Assert.ThrowsAsync<SkillValidationException>(() =>
            registry.CreateAsync(new SkillDraft { Name = "x", Description = "d" }, SkillScope.Project));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesContent()
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "s", Description = "old" }, SkillScope.Global);

        var updated = await registry.UpdateAsync("s", "---\nname: s\ndescription: new\n---\nNew body");

        Assert.Equal("new", updated.Description);
        Assert.Contains("New body", (await registry.InvokeAsync("s"))!.Content);
    }

    [Theory]
    [InlineData("no frontmatter")]
    [InlineData("---\nname: other\ndescription: d\n---\nB")]
    [InlineData("---\nname: s\n---\nB")]
    public async Task UpdateAsync_RejectsInvalidContent(string content)
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "s", Description = "d" }, SkillScope.Global);
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.UpdateAsync("s", content));
    }

    [Fact]
    public async Task UpdateAsync_UnknownSkill_Throws()
        => await Assert.ThrowsAsync<SkillValidationException>(() => Registry().UpdateAsync("missing", "---\nname: missing\ndescription: d\n---\n"));

    [Fact]
    public async Task RenameAsync_MovesFolderAndUpdatesFrontmatter()
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "old-name", Description = "d", Body = "Body text" }, SkillScope.Global);

        var renamed = await registry.RenameAsync("old-name", "new-name");

        Assert.Equal("new-name", renamed.Name);
        Assert.False(Directory.Exists(Path.Combine(_globalDir, "old-name")));
        var text = File.ReadAllText(Path.Combine(_globalDir, "new-name", "SKILL.md"));
        Assert.Contains("name: new-name", text);
        Assert.Contains("Body text", text);
        Assert.Null(await registry.GetAsync("old-name"));
        Assert.Empty(renamed.Warnings);
    }

    [Fact]
    public async Task RenameAsync_RejectsTakenOrInvalidName()
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "a", Description = "d" }, SkillScope.Global);
        await registry.CreateAsync(new SkillDraft { Name = "b", Description = "d" }, SkillScope.Global);

        await Assert.ThrowsAsync<SkillValidationException>(() => registry.RenameAsync("a", "b"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.RenameAsync("a", "Not Valid"));
        await Assert.ThrowsAsync<SkillValidationException>(() => registry.RenameAsync("missing", "c"));
    }

    [Fact]
    public async Task DeleteAsync_EffectiveCopy_RevealsShadowedGlobal()
    {
        var registry = Registry();
        await registry.CreateAsync(new SkillDraft { Name = "s", Description = "global" }, SkillScope.Global);
        await registry.CreateAsync(new SkillDraft { Name = "s", Description = "project" }, SkillScope.Project);

        Assert.True(await registry.DeleteAsync("s"));
        var remaining = Assert.Single(await registry.ListAsync());
        Assert.Equal("global", remaining.Description);

        Assert.True(await registry.DeleteAsync("s", SkillScope.Global));
        Assert.Empty(await registry.ListAllAsync());
        Assert.False(await registry.DeleteAsync("s"));
    }

    [Fact]
    public async Task ExternalEdits_AreSeenAfterRefresh()
    {
        var registry = Registry();
        Assert.Empty(await registry.ListAsync());
        WriteSkill(_globalDir, "late", "---\nname: late\ndescription: d\n---\nB");
        Assert.Empty(await registry.ListAsync()); // cached
        registry.Refresh();
        Assert.Single(await registry.ListAsync());
    }

    [Fact]
    public async Task EnableWatching_PicksUpExternalChanges()
    {
        using var registry = Registry();
        registry.EnableWatching();
        var signal = new TaskCompletionSource();
        registry.Changed += (_, _) => signal.TrySetResult();

        WriteSkill(_globalDir, "watched", "---\nname: watched\ndescription: d\n---\nB");

        var finished = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(signal.Task, finished);
        Assert.Single(await registry.ListAsync());
    }

    [Fact]
    public async Task LargeSkill_GetsSizeWarning()
    {
        WriteSkill(_globalDir, "big", "---\nname: big\ndescription: d\n---\n" + new string('x', 40_000));
        var skill = Assert.Single(await Registry().ListAsync());
        Assert.True(skill.EstimatedTokens > SkillRegistry.LargeSkillTokenThreshold);
        Assert.Contains(skill.Warnings, w => w.Contains("Large skill"));
    }
}
