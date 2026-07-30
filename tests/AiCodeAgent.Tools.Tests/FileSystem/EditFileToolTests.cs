using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.FileSystem;

public class EditFileToolTests : TempDirTestBase
{
    private readonly EditFileTool _tool = new(Substitute.For<ILogger<EditFileTool>>());

    [Fact]
    public void Name_Is_edit_file()
    {
        Assert.Equal("edit_file", _tool.Name);
    }

    [Fact]
    public void Definition_RequiresPathOldAndNewString()
    {
        Assert.Contains("path", _tool.Definition.Parameters.Required);
        Assert.Contains("old_string", _tool.Definition.Parameters.Required);
        Assert.Contains("new_string", _tool.Definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileNotFound()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "missing.txt", ["old_string"] = "a", ["new_string"] = "b" }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenOldStringNotFound()
    {
        WriteFile("test.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "xyz", ["new_string"] = "abc" }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("Text not found", result.Content);
        // File unchanged
        Assert.Equal("hello world", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesFirstOccurrence_ByDefault()
    {
        WriteFile("test.txt", "aaa bbb aaa bbb");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "bbb", ["new_string"] = "XXX" }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("aaa XXX aaa bbb", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesAllOccurrences_WhenOccurrenceZero()
    {
        WriteFile("test.txt", "aaa bbb aaa bbb");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "bbb", ["new_string"] = "XXX", ["occurrence"] = 0 }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("aaa XXX aaa XXX", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesNthOccurrence_WhenOccurrenceSpecified()
    {
        WriteFile("test.txt", "aaa bbb aaa bbb");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "bbb", ["new_string"] = "XXX", ["occurrence"] = 2 }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("aaa bbb aaa XXX", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_CleansUpBackupFile_AfterSuccess()
    {
        WriteFile("test.txt", "hello world");

        await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "hello", ["new_string"] = "hi" }),
            Context());

        // Backup file should be cleaned up after successful edit
        Assert.False(File.Exists(Path.Combine(WorkingDir, "test.txt.bak")));
        Assert.Equal("hi world", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_InReadOnlyMode()
    {
        WriteFile("test.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "hello", ["new_string"] = "hi" }),
            Context(readOnly: true));

        Assert.True(result.IsError);
        Assert.Contains("read-only", result.Content);
        // File unchanged
        Assert.Equal("hello world", ReadFile("test.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesDiffInOutput()
    {
        WriteFile("test.txt", "line1\nline2\nline3");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "test.txt", ["old_string"] = "line2", ["new_string"] = "LINE2" }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("- line2", result.Content);
        Assert.Contains("+ LINE2", result.Content);
    }
}