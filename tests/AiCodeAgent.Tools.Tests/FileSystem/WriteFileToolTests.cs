using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.FileSystem;

public class WriteFileToolTests : TempDirTestBase
{
    private readonly WriteFileTool _tool = new(Substitute.For<ILogger<WriteFileTool>>());

    [Fact]
    public void Name_Is_write_file()
    {
        Assert.Equal("write_file", _tool.Name);
    }

    [Fact]
    public void Definition_RequiresPathAndContent()
    {
        Assert.Contains("path", _tool.Definition.Parameters.Required);
        Assert.Contains("content", _tool.Definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesNewFile()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "new.txt", ["content"] = "Hello World" }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("Updated", result.Content);
        Assert.Equal("Hello World", ReadFile("new.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesExistingFile()
    {
        WriteFile("test.txt", "old content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = "new content" }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("Updated", result.Content);
        Assert.Equal("new content", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_AppendsToFile_WhenAppendTrue()
    {
        WriteFile("test.txt", "line1\n");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = "line2\n", ["append"] = true }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("Appended", result.Content);
        Assert.Equal("line1\nline2\n", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_CreatesParentDirectories_WhenCreateDirsTrue()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "subdir/nested/file.txt", ["content"] = "content" }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("content", ReadFile("subdir/nested/file.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_InReadOnlyMode()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = "content" }),
            Context(readOnly: true));

        Assert.True(result.IsError);
        Assert.Contains("read-only", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathOutsideAllowedPaths()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = "content" }),
            Context(allowedPaths: new() { @"C:\other" }));

        Assert.True(result.IsError);
        Assert.Contains("Access denied", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsPath_WhenWithinAllowedPaths()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = "content" }),
            Context(allowedPaths: new() { WorkingDir }));

        Assert.False(result.IsError);
        Assert.Equal("content", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_IncludesLineAndByteCount_InOutput()
    {
        var content = "line1\nline2\nline3";
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["content"] = content }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("3 lines", result.Content);
    }
}
