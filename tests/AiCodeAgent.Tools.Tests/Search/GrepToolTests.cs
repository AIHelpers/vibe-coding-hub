using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Search;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Search;

public class GrepToolTests : TempDirTestBase
{
    private readonly GrepTool _tool = new(Substitute.For<ILogger<GrepTool>>());

    [Fact]
    public void Name_Is_grep()
    {
        Assert.Equal("grep", _tool.Name);
    }

    [Fact]
    public void Definition_RequiresPattern()
    {
        Assert.Contains("pattern", _tool.Definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoMatchesMessage_WhenPatternNotFound()
    {
        WriteFile("test.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "xyz", ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("No matches", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_FindsMatches_WhenPatternExists()
    {
        WriteFile("test.txt", "public class Foo\npublic class Bar");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "class", ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("class", result.Content);
        Assert.Contains("test.txt", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPatternIsInvalidRegex()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "[", ["path"] = "." }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("Invalid regex", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_IsCaseInsensitive_ByDefault()
    {
        WriteFile("test.txt", "Hello World");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "hello", ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("Hello", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_IsCaseSensitive_WhenCaseSensitiveTrue()
    {
        WriteFile("test.txt", "Hello World");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "hello", ["path"] = ".", ["case_sensitive"] = true }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("No matches", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesContextLines_AroundMatch()
    {
        WriteFile("test.txt", "line1\nline2\nMATCH\nline4\nline5");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "MATCH", ["path"] = ".", ["context_lines"] = 1 }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("line2", result.Content);
        Assert.Contains("MATCH", result.Content);
        Assert.Contains("line4", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_LimitsResults_ToMaxResults()
    {
        var content = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"match{i}"));
        WriteFile("test.txt", content);

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "match", ["path"] = ".", ["max_results"] = 3 }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("3 match(es)", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_SearchesSpecificFile_WhenPathIsFile()
    {
        WriteFile("a.txt", "target line");
        WriteFile("b.txt", "target line");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "target", ["path"] = "a.txt" }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("a.txt", result.Content);
        Assert.DoesNotContain("b.txt", result.Content);
    }
}