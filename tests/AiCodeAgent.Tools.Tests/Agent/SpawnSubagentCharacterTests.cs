using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Tools.Tests.Agent;

public class SpawnSubagentCharacterTests : TestHelpers.TempDirTestBase
{
    private readonly ISubagentRunner _runner = Substitute.For<ISubagentRunner>();
    private readonly SpawnSubagentTool _tool;
    private SubagentContext? _seen;

    public SpawnSubagentCharacterTests()
    {
        WriteFile("characters/qa-quinn.md", """
            ---
            id: qa-quinn
            description: Finds bugs
            base-role: tester
            permission-mode: full-auto
            model: qa-model
            tools: { remove: [git] }
            skills: [exploratory-testing]
            pinned-skills: [conventions]
            ---
            QUINN-PERSONA
            """);
        WriteFile("characters/broken.md", "---\nid: broken\npermission-mode: nope\n---\n");
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(WorkingDir, "characters"));
        _tool = new SpawnSubagentTool(_runner, Substitute.For<ILogger<SpawnSubagentTool>>(), characters, presets);

        _runner.RunAsync(Arg.Any<string>(), Arg.Do<SubagentContext>(c => _seen = c), Arg.Any<CancellationToken>())
            .Returns(new SubagentSummary { SubagentId = "sub_1", Task = "t", Content = "done" });
    }

    private AgentExecutionContext Ctx(PermissionMode mode, IReadOnlyCollection<string>? skills = null) => Context() with
    {
        Permissions = new PermissionSettings { Mode = mode },
        AllowedSkills = skills
    };

    [Fact]
    public async Task Character_SetsPersonaToolsSkillsModel()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["task"] = "test the parser", ["character"] = "qa-quinn" }), Ctx(PermissionMode.FullAuto));

        Assert.False(result.IsError);
        Assert.NotNull(_seen);
        Assert.Equal("qa-quinn", _seen!.CharacterId);
        Assert.Equal("tester", _seen.Role);
        Assert.Contains("QUINN-PERSONA", _seen.RoleSystemPrompt);
        Assert.Contains("TESTER agent", _seen.RoleSystemPrompt);
        Assert.Equal(new[] { "exploratory-testing" }, _seen.AllowedSkills);
        Assert.Equal(new[] { "conventions" }, _seen.PinnedSkills);
        Assert.Contains("git", _seen.DisabledTools);
        Assert.DoesNotContain("git", _seen.EnabledTools!);
        Assert.Equal("qa-model", _seen.Model);
        Assert.Equal(PermissionMode.FullAuto, _seen.PermissionMode);
    }

    [Fact]
    public async Task Character_NeverLoosensParentPermissionMode()
    {
        await _tool.ExecuteAsync(Call(new() { ["task"] = "t", ["character"] = "qa-quinn" }), Ctx(PermissionMode.Plan));
        Assert.Equal(PermissionMode.Plan, _seen!.PermissionMode);
    }

    [Fact]
    public async Task ExplicitModelAndTools_BeatCharacter()
    {
        await _tool.ExecuteAsync(Call(new()
        {
            ["task"] = "t",
            ["character"] = "qa-quinn",
            ["model"] = "explicit-model",
            ["enabledTools"] = new[] { "read_file" }
        }), Ctx(PermissionMode.Ask));

        Assert.Equal("explicit-model", _seen!.Model);
        Assert.Equal(new[] { "read_file" }, _seen.EnabledTools);
    }

    [Fact]
    public async Task WithoutCharacter_InheritsParentSkillSet()
    {
        await _tool.ExecuteAsync(Call(new() { ["task"] = "t" }), Ctx(PermissionMode.Ask, new[] { "parent-skill" }));
        Assert.Equal(new[] { "parent-skill" }, _seen!.AllowedSkills);
        Assert.Null(_seen.CharacterId);
    }

    [Fact]
    public async Task BuiltInCharacter_KeepsParentSkillRestriction()
    {
        await _tool.ExecuteAsync(Call(new() { ["task"] = "t", ["character"] = "reviewer" }), Ctx(PermissionMode.Ask, new[] { "parent-skill" }));
        Assert.Equal("reviewer", _seen!.CharacterId);
        Assert.Equal(new[] { "parent-skill" }, _seen.AllowedSkills);
    }

    [Fact]
    public async Task UnknownOrInvalidCharacter_IsError_AndDoesNotRun()
    {
        var unknown = await _tool.ExecuteAsync(Call(new() { ["task"] = "t", ["character"] = "ghost" }), Ctx(PermissionMode.Ask));
        Assert.True(unknown.IsError);
        Assert.Contains("qa-quinn", unknown.Content);

        var invalid = await _tool.ExecuteAsync(Call(new() { ["task"] = "t", ["character"] = "broken" }), Ctx(PermissionMode.Ask));
        Assert.True(invalid.IsError);

        Assert.Null(_seen);
    }

    [Fact]
    public void Schema_ListsCharacters()
    {
        var description = _tool.Definition.Parameters.Properties["character"].Description;
        Assert.Contains("qa-quinn", description);
        Assert.Contains("planner", description);
    }

    [Theory]
    [InlineData(PermissionMode.FullAuto, PermissionMode.Plan, PermissionMode.Plan)]
    [InlineData(PermissionMode.Ask, PermissionMode.AutoEdit, PermissionMode.Ask)]
    [InlineData(PermissionMode.AutoEdit, PermissionMode.FullAuto, PermissionMode.AutoEdit)]
    public void MostRestrictive_PicksTighterMode(PermissionMode a, PermissionMode b, PermissionMode expected)
    {
        Assert.Equal(expected, SpawnSubagentTool.MostRestrictive(a, b));
        Assert.Equal(expected, SpawnSubagentTool.MostRestrictive(b, a));
    }
}
