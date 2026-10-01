using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.FileSystem;

public class ListDirectoryToolTests : TempDirTestBase
{
    private readonly ListDirectoryTool _tool = new(Substitute.For<ILogger<ListDirectoryTool>>());

    [Fact]
    public void Name_Is_list_directory()
    {
        Assert.Equal("list_directory", _tool.Name);
    }

    [Fact]
    public void Definition_HasNoRequiredParameters()
    {
        Assert.Empty(_tool.Definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_ListsDirectoryContents()
    {
        WriteFile("file1.txt", "content1");
        WriteFile("file2.txt", "content2");
        Directory.CreateDirectory(Path.Combine(WorkingDir, "subdir"));

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "." }), Context());

        Assert.False(result.IsError);
        Assert.Contains("file1.txt", result.Content);
        Assert.Contains("file2.txt", result.Content);
        Assert.Contains("subdir", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_ForNonExistentDirectory()
    {
        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "nonexistent" }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("Directory not found", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ListsRecursively_WhenRecursiveTrue()
    {
        WriteFile("top.txt", "content");
        Directory.CreateDirectory(Path.Combine(WorkingDir, "sub"));
        WriteFile("sub/nested.txt", "nested");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = ".", ["recursive"] = true }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("top.txt", result.Content);
        Assert.Contains("nested.txt", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByPattern()
    {
        WriteFile("file.cs", "content");
        WriteFile("file.txt", "content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = ".", ["pattern"] = "*.cs" }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("file.cs", result.Content);
        Assert.DoesNotContain("file.txt", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ShowsHiddenFiles_WhenShowHiddenTrue()
    {
        WriteFile(".hidden", "content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = ".", ["show_hidden"] = true }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains(".hidden", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_HidesHiddenFiles_ByDefault()
    {
        WriteFile(".hidden", "content");
        WriteFile("visible.txt", "content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.DoesNotContain(".hidden", result.Content);
        Assert.Contains("visible.txt", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresDefaultDirectories()
    {
        Directory.CreateDirectory(Path.Combine(WorkingDir, "bin"));
        Directory.CreateDirectory(Path.Combine(WorkingDir, "obj"));
        WriteFile("keep.txt", "content");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.DoesNotContain("bin/", result.Content);
        Assert.DoesNotContain("obj/", result.Content);
        Assert.Contains("keep.txt", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ShowsFileSizes()
    {
        WriteFile("test.txt", "Hello World");

        var result = await _tool.ExecuteAsync(
            Call(new() { ["path"] = "." }),
            Context());

        Assert.False(result.IsError);
        Assert.Contains("B)", result.Content);
    }
}
