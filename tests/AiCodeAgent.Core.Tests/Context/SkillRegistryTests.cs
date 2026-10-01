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
}