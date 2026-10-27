using AiCodeAgent.App.CommandPalette;
using Xunit;

namespace AiCodeAgent.App.Tests.CommandPalette;

public class FuzzyMatcherRankingTests
{
    private static ICommandPaletteEntry E(string id, string title, string category, params string[] keywords) =>
        new CommandPaletteEntry { Id = id, Title = title, Category = category, Keywords = keywords };

    /// <summary>A slice of the real palette, including the entries that used to rank wrongly.</summary>
    private static CommandPaletteRegistry RealisticRegistry()
    {
        var r = new CommandPaletteRegistry();
        r.Register(new[]
        {
            E("nav.flows", "Flows (draw and run flows of characters)", "Navigation", "flow", "flows", "pipeline", "workflow", "graph", "parallel", "characters", "canvas", "drawio"),
            E("flows.new", "New Flow", "Flows", "flow", "pipeline", "workflow", "create", "new", "canvas"),
            E("characters.new", "New Character", "Characters", "character", "persona", "create", "new", "agent"),
            E("skills.new", "New Skill", "Skills", "skill", "skill.md", "create", "new", "registry"),
            E("editor.openFile", "Open File in Editor...", "Editor", "open", "file", "editor", "picker", "browse"),
            E("editor.save", "Save File", "Editor", "save", "write", "persist"),
            E("editor.saveAs", "Save File As...", "Editor", "save", "saveas", "write", "rename", "persist"),
            E("editor.saveAll", "Save All Files", "Editor", "save", "write", "all", "persist"),
            E("editor.nextTab", "Next Tab", "Editor", "next", "tab", "switch"),
            E("nav.settings", "Settings", "Navigation", "preferences", "options"),
        });
        return r;
    }

    private static List<string> Titles(string query)
    {
        var palette = new CommandPaletteViewModel(RealisticRegistry()) { SearchText = query };
        return palette.FilteredEntries.Select(e => e.Title).ToList();
    }

    [Fact]
    public void Flow_RanksNewFlowAndFlowsFirst_AndDropsScatteredFileMatches()
    {
        var titles = Titles("flow");

        Assert.Equal(2, titles.Count);
        Assert.Contains("New Flow", titles);
        Assert.Contains(titles, t => t.StartsWith("Flows", StringComparison.Ordinal));
        Assert.DoesNotContain("Save File", titles);
        Assert.DoesNotContain("Save All Files", titles);
        Assert.DoesNotContain("Open File in Editor...", titles);
    }

    [Fact]
    public void Save_RanksSaveFileFirst()
    {
        var titles = Titles("save");

        Assert.Equal("Save File", titles[0]);
        Assert.Equal(3, titles.Count);
        Assert.Contains("Save File As...", titles);
        Assert.Contains("Save All Files", titles);
    }

    [Fact]
    public void TierOrder_PrefixThenWordStartThenSubstring()
    {
        var prefix = E("a", "Flow Chart", "X");
        var wordStart = E("b", "New Flow", "X");
        var substring = E("c", "Workflow Setup", "X");

        var p = FuzzyMatcher.ScoreEntry(prefix, "flow");
        var w = FuzzyMatcher.ScoreEntry(wordStart, "flow");
        var s = FuzzyMatcher.ScoreEntry(substring, "flow");

        Assert.True(p > w, "prefix should beat word-start");
        Assert.True(w > s, "word-start should beat substring");
        Assert.True(s >= FuzzyMatcher.StrongMatchThreshold);
    }

    [Fact]
    public void ExactTitle_BeatsPrefix()
    {
        var exact = FuzzyMatcher.ScoreEntry(E("a", "Settings", "X"), "settings");
        var prefix = FuzzyMatcher.ScoreEntry(E("b", "Settings Sync", "X"), "settings");
        Assert.True(exact > prefix);
    }

    [Fact]
    public void TitleMatch_BeatsKeywordMatch()
    {
        var title = FuzzyMatcher.ScoreEntry(E("a", "Open File", "X", "zzz"), "open");
        var keyword = FuzzyMatcher.ScoreEntry(E("b", "Browse", "X", "open"), "open");
        Assert.True(title > keyword);
        Assert.True(keyword >= FuzzyMatcher.StrongMatchThreshold);
    }

    [Fact]
    public void ScatteredMatch_IsKept_WhenNothingMatchesBetter_ButStaysInItsOwnTier()
    {
        // "nxt" only matches as scattered letters of "Next Tab".
        var titles = Titles("nxt");
        Assert.Equal("Next Tab", titles[0]);

        var scattered = FuzzyMatcher.ScoreEntry(E("a", "Next Tab", "Editor"), "nxt");
        Assert.InRange(scattered, 1, FuzzyMatcher.FuzzyCeiling - 1);
    }

    [Fact]
    public void WildlyScatteredLetters_DoNotMatch()
    {
        // f . . . l . . . o . . . w spread over a long string is noise, not a match.
        var score = FuzzyMatcher.ScoreEntry(E("a", "Fetch the latest origin with rebase", "Git"), "flow");
        Assert.Equal(0, score);
    }

    [Fact]
    public void MultiWordQuery_MatchesEachWordAtAWordStart()
    {
        var titles = Titles("new fl");
        Assert.Equal("New Flow", titles[0]);
    }

    [Fact]
    public void EmptyQuery_ListsEverything()
    {
        Assert.Equal(RealisticRegistry().GetAllEntries().Count, Titles("").Count);
        Assert.Equal(RealisticRegistry().GetAllEntries().Count, Titles("   ").Count);
    }

    [Fact]
    public void RecentlyUsed_IsBoostedWithinTheSameTier_ButNotAcrossTiers()
    {
        var palette = new CommandPaletteViewModel(RealisticRegistry()) { SearchText = "save" };
        Assert.Equal("Save File", palette.FilteredEntries[0].Title);

        palette.MarkUsed("editor.saveAll");
        palette.SearchText = string.Empty;
        palette.SearchText = "save";
        Assert.Equal("Save All Files", palette.FilteredEntries[0].Title);

        // Among equal title prefixes the recent one wins.
        palette.MarkUsed("editor.nextTab");
        palette.SearchText = "ne";
        Assert.Equal("Next Tab", palette.FilteredEntries[0].Title);
    }

    [Fact]
    public void EmptyQuery_ShowsRecentlyUsedFirst()
    {
        var palette = new CommandPaletteViewModel(RealisticRegistry());
        palette.MarkUsed("nav.settings");
        palette.Open();
        Assert.Equal("Settings", palette.FilteredEntries[0].Title);
    }
}
