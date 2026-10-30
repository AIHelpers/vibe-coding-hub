using AiCodeAgent.Screenshots;

namespace AiCodeAgent.Screenshots.Tests;

public class OptionsTests
{
    [Fact]
    public void Defaults_MatchTheDocumentedCommand()
    {
        var options = Options.Parse(Array.Empty<string>(), out var error);

        Assert.Null(error);
        Assert.Equal("docs/content-kit/images", options.OutDirectory);
        Assert.Equal(ThemeSelection.Both, options.Theme);
        Assert.Equal(0.5, options.ThresholdPercent);
        Assert.False(options.FastChecks);
        Assert.False(options.Check);
        Assert.Empty(options.Only);
    }

    [Fact]
    public void Parse_ReadsTheStoryCommand()
    {
        var options = Options.Parse(new[] { "--out", "docs/content-kit/images" }, out var error);

        Assert.Null(error);
        Assert.Equal("docs/content-kit/images", options.OutDirectory);
    }

    [Fact]
    public void Parse_ReadsAllOptions()
    {
        var options = Options.Parse(new[]
        {
            "--out", "x", "--theme", "Light", "--only", "5, approval,18", "--fast-checks", "--check",
            "--threshold", "0.25", "--delay-scale", "1.5", "--timeout", "30", "--repo", "r", "--keep-workspace", "--worker"
        }, out var error);

        Assert.Null(error);
        Assert.Equal(ThemeSelection.Light, options.Theme);
        Assert.Equal(new[] { "5", "approval", "18" }, options.Only);
        Assert.True(options.FastChecks);
        Assert.True(options.Check);
        Assert.Equal(0.25, options.ThresholdPercent);
        Assert.Equal(1.5, options.DelayScale);
        Assert.Equal(30, options.SceneTimeoutSeconds);
        Assert.Equal("r", options.RepoRoot);
        Assert.True(options.KeepWorkspace);
        Assert.True(options.Worker);
    }

    [Fact]
    public void Parse_ReadsCompare()
    {
        var options = Options.Parse(new[] { "--compare", "a", "b" }, out var error);

        Assert.Null(error);
        Assert.Equal("a", options.CompareA);
        Assert.Equal("b", options.CompareB);
    }

    [Theory]
    [InlineData("--theme", "blue")]
    [InlineData("--threshold", "101")]
    [InlineData("--threshold", "abc")]
    [InlineData("--timeout", "1")]
    [InlineData("--out")]
    [InlineData("--nonsense")]
    public void Parse_RejectsBadInput(params string[] args)
    {
        Options.Parse(args, out var error);

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Passes_BothThemes_DarkInTheOutputFolder_LightInItsSubfolder()
    {
        var options = new Options { Theme = ThemeSelection.Both };

        var passes = Orchestrator.Passes(options, Path.Combine("out", "images"));

        Assert.Equal(new[] { ("dark", Path.Combine("out", "images")), ("light", Path.Combine("out", "images", "light")) }, passes);
    }

    [Theory]
    [InlineData("Dark", "dark")]
    [InlineData("Light", "light")]
    public void Passes_SingleTheme_RendersOnlyThatTheme(string selection, string theme)
    {
        var passes = Orchestrator.Passes(new Options { Theme = Enum.Parse<ThemeSelection>(selection) }, "out");

        Assert.Equal(theme, Assert.Single(passes).Theme);
    }
}
