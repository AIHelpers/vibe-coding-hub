using AiCodeAgent.Screenshots;

namespace AiCodeAgent.Screenshots.Tests;

/// <summary>E8-S2: the image set the articles link to.</summary>
public class SceneCatalogTests
{
    [Fact]
    public void Catalog_HasTwentyTwoImagesNumberedOneToTwentyTwo()
    {
        Assert.Equal(22, SceneCatalog.All.Count);
        Assert.Equal(Enumerable.Range(1, 22), SceneCatalog.All.Select(s => s.Number));
    }

    [Fact]
    public void FileNames_AreUniqueNumberedKebabCasePngs()
    {
        var names = SceneCatalog.All.Select(s => s.FileName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.Matches("^[0-9]{2}-[a-z]+(-[a-z]+)*\\.png$", name));
    }

    [Theory]
    [InlineData(5, "05-approval.png")]
    [InlineData(6, "06-result.png")]
    [InlineData(18, "18-settings.png")]
    [InlineData(22, "22-light-theme.png")]
    public void KnownImages_KeepTheNamesTheArticlesUse(int number, string fileName)
    {
        // 18-settings.png is named in the launch checklist (E8-S4); the others are anchored by the roadmap's screenshot numbers.
        Assert.Equal(fileName, SceneCatalog.All.Single(s => s.Number == number).FileName);
    }

    [Fact]
    public void OnlyTheLightThemeSceneSwitchesTheTheme()
    {
        Assert.Equal(new[] { 22 }, SceneCatalog.All.Where(s => s.ForcesLightTheme).Select(s => s.Number));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("05")]
    [InlineData("approval")]
    [InlineData("APPROVAL")]
    [InlineData("05-approval")]
    [InlineData("05-approval.png")]
    public void Select_FindsASceneByNumberSlugOrFileName(string filter)
    {
        var scenes = SceneCatalog.Select(new[] { filter }, out var unknown);

        Assert.Empty(unknown);
        Assert.Equal(5, Assert.Single(scenes).Number);
    }

    [Fact]
    public void Select_ReportsUnknownNames_AndKeepsFileOrder()
    {
        var scenes = SceneCatalog.Select(new[] { "settings", "nonsense", "1" }, out var unknown);

        Assert.Equal(new[] { "nonsense" }, unknown);
        Assert.Equal(new[] { 1, 18 }, scenes.Select(s => s.Number));
    }

    [Fact]
    public void Select_WithoutFilters_ReturnsEverything()
    {
        Assert.Equal(22, SceneCatalog.Select(Array.Empty<string>(), out var unknown).Count);
        Assert.Empty(unknown);
    }

    [Fact]
    public void EverySceneHasADriver_ExactlyOnce_AndRunsInAStableOrder()
    {
        var plan = Scenes.Plan(SceneCatalog.All);

        Assert.Equal(22, plan.Count);
        Assert.Equal(Enumerable.Range(1, 22), plan.Select(p => p.Info.Number).OrderBy(n => n));
        // the agent run (5, 6) comes before the scenes that look at its result; the flow scenes follow each other
        var order = plan.Select(p => p.Info.Number).ToList();
        Assert.True(order.IndexOf(5) < order.IndexOf(6));
        Assert.True(order.IndexOf(6) < order.IndexOf(7));
        Assert.Equal(new[] { 15, 16, 17 }, order.SkipWhile(n => n != 15).Take(3));
    }

    [Fact]
    public void Plan_OfASubset_KeepsOnlyThoseScenes()
    {
        var subset = SceneCatalog.Select(new[] { "17", "5" }, out _);

        Assert.Equal(new[] { 5, 17 }, Scenes.Plan(subset).Select(p => p.Info.Number));
    }
}
