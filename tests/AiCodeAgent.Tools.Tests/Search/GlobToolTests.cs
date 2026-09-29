using AiCodeAgent.Tools.Search;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.Search;

public class GlobToolTests : TempDirTestBase
{
    private readonly GlobTool _tool = new(NullLogger<GlobTool>.Instance);

    [Theory]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "src/deep/b.cs", true)]
    [InlineData("**/*.cs", "src/b.ts", false)]
    [InlineData("*.cs", "src/b.cs", true)]              // no slash => any depth
    [InlineData("src/*.cs", "src/b.cs", true)]
    [InlineData("src/*.cs", "src/deep/b.cs", false)]    // single * does not cross directories
    [InlineData("src/**/*.cs", "src/b.cs", true)]       // ** matches zero directories
    [InlineData("*.{ts,tsx}", "ui/App.tsx", true)]
    [InlineData("*.{ts,tsx}", "ui/App.js", false)]
    [InlineData("test?.txt", "test1.txt", true)]
    [InlineData("test?.txt", "test12.txt", false)]
    public void GlobToRegex_MatchesExpectedPaths(string glob, string path, bool expected) =>
        Assert.Equal(expected, GlobTool.GlobToRegex(glob).IsMatch(path));

    [Fact]
    public async Task Execute_FindsFilesAndSkipsIgnoredDirectories()
    {
        WriteFile("src/a.cs", "");
        WriteFile("src/sub/b.cs", "");
        WriteFile("node_modules/pkg/c.cs", "");
        WriteFile("bin/d.cs", "");
        WriteFile("readme.md", "");

        var result = await _tool.ExecuteAsync(Call(new() { ["pattern"] = "**/*.cs" }), Context());

        Assert.False(result.IsError);
        Assert.Contains("src/a.cs", result.Content);
        Assert.Contains("src/sub/b.cs", result.Content);
        Assert.DoesNotContain("node_modules", result.Content);
        Assert.DoesNotContain("bin/", result.Content);
        Assert.DoesNotContain("readme.md", result.Content);
    }

    [Fact]
    public async Task Execute_ReportsNoMatches()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["pattern"] = "**/*.zzz" }), Context());
        Assert.False(result.IsError);
        Assert.Contains("No files match", result.Content);
    }

    [Fact]
    public async Task Execute_RejectsPathsOutsideAllowedRoots()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["pattern"] = "*", ["path"] = Path.GetTempPath() }),
            Context(allowedPaths: new() { WorkingDir }));
        Assert.True(result.IsError);
    }
}
