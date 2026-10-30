using System.Text.Json;
using AiCodeAgent.Screenshots;

namespace AiCodeAgent.Screenshots.Tests;

/// <summary>
/// The flow and skill scenes drive the example team the workspace copies into the sample project. If an example is
/// renamed or restructured, this fails here instead of during a long screenshot run.
/// </summary>
public class ExampleTeamTests
{
    private static string Examples()
    {
        var root = Workspace.FindRepoRoot(AppContext.BaseDirectory)
                   ?? throw new InvalidOperationException("AiCodeAgent.slnx not found above the test folder.");
        return Path.Combine(root, "examples");
    }

    [Fact]
    public void FeatureTeamFlow_HasTheFiveStepsTheFlowScenesWatch()
    {
        var path = Path.Combine(Examples(), "pipelines", SceneContext.FeatureTeam + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var stages = document.RootElement.GetProperty("stages").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();

        Assert.Equal(SceneContext.FeatureTeam, document.RootElement.GetProperty("name").GetString());
        Assert.Equal(new[] { "design", "backend", "model", "review", "release-notes" }, stages);
    }

    [Fact]
    public void TheCharactersAndSkillsTheScenesSelect_Exist()
    {
        var examples = Examples();

        Assert.True(File.Exists(Path.Combine(examples, "characters", "dana-dotnet.md")));
        Assert.True(Directory.Exists(Path.Combine(examples, "skills", "csharp")));
    }

    [Fact]
    public void ThePipelineFolder_HoldsTheGalleryIndexTheWorkspaceLeavesOut()
    {
        Assert.True(File.Exists(Path.Combine(Examples(), "pipelines", "templates.json")));
    }

    [Fact]
    public void ScratchPaths_AreFixedPerTheme_SoTwoRunsShowTheSameText()
    {
        Assert.Equal(Workspace.ScratchPath("dark"), Workspace.ScratchPath("dark"));
        Assert.NotEqual(Workspace.ScratchPath("dark"), Workspace.ScratchPath("light"));
    }
}
