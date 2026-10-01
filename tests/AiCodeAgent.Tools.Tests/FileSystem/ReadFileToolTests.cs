using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.FileSystem;

public class ReadFileToolTests : TempDirTestBase
{
    private readonly ReadFileTool _tool = new(Substitute.For<ILogger<ReadFileTool>>());

    [Fact]
    public void Name_Is_read_file()
    {
        Assert.Equal("read_file", _tool.Name);
    }

    [Fact]
    public void Definition_RequiresPath()
    {
        Assert.Contains("path", _tool.Definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileNotFound()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "missing.txt" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFileContent_WhenFileExists()
    {
        WriteFile("test.txt", "Hello World");

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "test.txt" }), Context());

        Assert.False(result.IsError);
        Assert.Contains("Hello World", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSpecificLineRange_WhenStartAndEndProvided()
    {
        WriteFile("lines.txt", "line1\nline2\nline3\nline4\nline5");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "lines.txt", ["start_line"] = 2, ["end_line"] = 4 }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("line2", result.Content);
        Assert.Contains("line3", result.Content);
        Assert.Contains("line4", result.Content);
        Assert.DoesNotContain("line1", result.Content);
        Assert.DoesNotContain("line5", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsLanguageTag_ForKnownExtension()
    {
        WriteFile("code.cs", "public class C {}");

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "code.cs" }), Context());

        Assert.False(result.IsError);
        Assert.Contains("csharp", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathOutsideAllowedPaths()
    {
        WriteFile("test.txt", "content");
        var outsideDir = Path.Combine(Path.GetTempPath(), "aiagent-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            var result = await _tool.ExecuteAsync(
                Call(new() { ["path"] = "test.txt" }),
                Context(allowedPaths: new() { outsideDir }));

            Assert.True(result.IsError);
            Assert.Contains("Access denied", result.Content);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllowsPath_WhenWithinAllowedPaths()
    {
        WriteFile("test.txt", "content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt" }),
            Context(allowedPaths: new() { WorkingDir }));

        Assert.False(result.IsError);
        Assert.Contains("content", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileTooLarge()
    {
        var bigPath = Path.Combine(WorkingDir, "big.txt");
        await using var fs = File.Create(bigPath);
        fs.SetLength(11 * 1024 * 1024);
        fs.Close();

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "big.txt" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("too large", result.Content);
    }
}