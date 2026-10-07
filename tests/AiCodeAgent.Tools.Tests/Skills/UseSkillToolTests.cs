using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Skills;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Tools.Tests.Skills;

public class UseSkillToolTests : TempDirTestBase
{
    private readonly SkillRegistry _registry;
    private readonly UseSkillTool _tool;

    public UseSkillToolTests()
    {
        var skillsDir = Path.Combine(WorkingDir, "skills");
        WriteFile("skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Notes\n---\n# Release\nRun git log.");
        WriteFile("skills/review/SKILL.md", "---\nname: review\ndescription: Review\n---\nReview carefully.");
        WriteFile("skills/manual/SKILL.md", "---\nname: manual\ndescription: Manual\ndisable-model-invocation: true\n---\nManual only.");
        _registry = new SkillRegistry(NullLogger<SkillRegistry>.Instance, globalSkillsDir: skillsDir);
        _tool = new UseSkillTool(_registry, NullLogger<UseSkillTool>.Instance);
    }

    private AgentExecutionContext Ctx(IReadOnlyCollection<string>? allowed) => Context() with { AllowedSkills = allowed };

    [Fact]
    public async Task LoadsSkillInstructions_WhenNoRestriction()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "release-notes", ["args"] = "v1.2" }), Ctx(null));

        Assert.False(result.IsError);
        Assert.Contains("Run git log.", result.Content);
        Assert.Contains("Arguments: v1.2", result.Content);
        Assert.DoesNotContain("description:", result.Content);
    }

    [Fact]
    public async Task NameIsCaseInsensitive()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "Release-Notes" }), Ctx(null));
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task DeniesSkillNotAssignedToAgent()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "review" }), Ctx(new[] { "release-notes" }));

        Assert.True(result.IsError);
        Assert.Contains("not available to this agent", result.Content);
        Assert.Contains("release-notes", result.Content);
    }

    [Fact]
    public async Task UnknownSkill_ReportsDoesNotExist()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "nope" }), Ctx(null));
        Assert.True(result.IsError);
        Assert.Contains("does not exist", result.Content);
    }

    [Fact]
    public async Task ManualOnlySkill_CannotBeLoadedByModel()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "manual" }), Ctx(null));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task EmptySkillSet_ListsNone()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["name"] = "review" }), Ctx(Array.Empty<string>()));
        Assert.True(result.IsError);
        Assert.Contains("Available skills: none", result.Content);
    }

    [Fact]
    public async Task MissingName_IsError()
    {
        var result = await _tool.ExecuteAsync(Call(new()), Ctx(null));
        Assert.True(result.IsError);
    }

    [Fact]
    public void Definition_IsReadOnlyAndRequiresName()
    {
        Assert.Equal("use_skill", _tool.Name);
        Assert.Equal(RiskLevel.Read, _tool.Risk);
        Assert.Contains("name", _tool.Definition.Parameters.Required);
    }
}
