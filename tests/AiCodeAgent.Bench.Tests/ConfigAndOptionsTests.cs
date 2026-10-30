namespace AiCodeAgent.Bench.Tests;

public class ConfigAndOptionsTests
{
    [Theory]
    [InlineData("single", null)]
    [InlineData("bug-fix-flow", "bug-fix")]
    [InlineData("feature-team-flow", "feature-team")]
    [InlineData("flow:migration", "migration")]
    public void Configurations_AreParsed(string text, string? pipeline)
    {
        Assert.True(BenchConfig.TryParse(text, out var config, out var error), error);
        Assert.Equal(pipeline, config!.Pipeline);
        Assert.Equal(pipeline == null, config.IsSingle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bug-fix")]
    [InlineData("-flow")]
    [InlineData("a b")]
    public void BadConfigurations_AreRejected(string text)
    {
        Assert.False(BenchConfig.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ParseList_RejectsDuplicates()
    {
        Assert.Throws<BenchException>(() => BenchConfig.ParseList(new[] { "single", "SINGLE" }));
    }

    [Fact]
    public void TheDocumentedCommandLine_Parses()
    {
        var r = OptionsParser.Parse(new[] { "--tasks", "bench/tasks.json", "--configs", "single,bug-fix-flow", "--model", "some-model" });
        Assert.Null(r.Error);
        Assert.Equal("some-model", r.Options!.Model);
        Assert.Equal(new[] { "single", "bug-fix-flow" }, r.Options.Configs);
        Assert.Equal(3, r.Options.Seeds);
    }

    [Fact]
    public void Model_IsRequired_ForARun_ButNotForListOrValidate()
    {
        Assert.NotNull(OptionsParser.Parse(new[] { "--configs", "single" }).Error);
        Assert.Null(OptionsParser.Parse(new[] { "--list" }).Error);
        Assert.Null(OptionsParser.Parse(new[] { "--validate" }).Error);
    }

    [Theory]
    [InlineData("--seeds", "0")]
    [InlineData("--seeds", "x")]
    [InlineData("--timeout", "1")]
    [InlineData("--max-budget", "-1")]
    public void BadNumbers_AreRejected(string option, string value)
    {
        Assert.NotNull(OptionsParser.Parse(new[] { "--model", "m", option, value }).Error);
    }

    [Fact]
    public void OptionsWithEquals_AndFlags_Parse()
    {
        var r = OptionsParser.Parse(new[] { "--model=m", "--seeds=5", "--resume", "--only=a,b", "--max-budget=1.5" });
        Assert.Null(r.Error);
        Assert.Equal(5, r.Options!.Seeds);
        Assert.True(r.Options.Resume);
        Assert.Equal(new[] { "a", "b" }, r.Options.Only);
        Assert.Equal(1.5m, r.Options.MaxBudgetUsd);
    }

    [Fact]
    public void UnknownOption_AndHelp()
    {
        Assert.Contains("Unknown option", OptionsParser.Parse(new[] { "--nope" }).Error);
        Assert.True(OptionsParser.Parse(new[] { "--help" }).ShowHelp);
    }
}
