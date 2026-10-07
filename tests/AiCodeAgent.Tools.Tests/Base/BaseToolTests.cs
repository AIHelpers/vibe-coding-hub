using System.Text.Json;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Base;

public class BaseToolTests
{
    private class TestTool : BaseTool
    {
        public TestTool(ILogger logger) : base(logger) { }

        public override string Name => "test";
        public override string Description => "test tool";
        public override RiskLevel Risk => RiskLevel.Read;
        public override ToolDefinition Definition => new() { Name = "test" };

        public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
            => Task.FromResult(Success("test"));

        public T PublicGetArg<T>(ToolCall call, string key, T defaultValue = default!)
            => GetArg<T>(call, key, defaultValue);

        public string PublicResolvePath(string path, AgentExecutionContext context)
            => ResolvePath(path, context);

        public void PublicValidatePath(string resolvedPath, AgentExecutionContext context)
            => ValidatePath(resolvedPath, context);

        public ToolResult PublicSuccess(string content, object? data = null)
            => Success(content, data);

        public ToolResult PublicError(string message)
            => Error(message);
    }

    private static TestTool CreateTool() => new(Substitute.For<ILogger>());

    /// <summary>An absolute path on the current OS (C:\a\b on Windows, /a/b elsewhere).</summary>
    private static string Abs(params string[] parts) =>
        Path.Combine(new[] { OperatingSystem.IsWindows() ? @"C:\" : "/" }.Concat(parts).ToArray());

    private static AgentExecutionContext CreateContext(string? workingDir = null, bool readOnly = false, List<string>? allowedPaths = null) => new()
    {
        SessionId = "test",
        WorkingDirectory = workingDir ?? Abs("test"),
        IsReadOnly = readOnly,
        AllowedPaths = allowedPaths ?? new()
    };

    [Fact]
    public void GetArg_ReturnsStringValue_FromJsonElement()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["path"] = JsonSerializer.SerializeToElement("hello.txt") } };

        var result = tool.PublicGetArg<string>(call, "path");

        Assert.Equal("hello.txt", result);
    }

    [Fact]
    public void GetArg_ReturnsIntValue_FromJsonElement()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["count"] = JsonSerializer.SerializeToElement(42) } };

        var result = tool.PublicGetArg<int>(call, "count");

        Assert.Equal(42, result);
    }

    [Fact]
    public void GetArg_ReturnsBoolValue_FromJsonElement()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["flag"] = JsonSerializer.SerializeToElement(true) } };

        var result = tool.PublicGetArg<bool>(call, "flag");

        Assert.True(result);
    }

    [Fact]
    public void GetArg_ReturnsDefaultValue_WhenKeyMissing()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() };

        var result = tool.PublicGetArg<string>(call, "missing", "default");

        Assert.Equal("default", result);
    }

    [Fact]
    public void GetArg_ReturnsDefaultValue_WhenValueIsNull()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["key"] = null } };

        var result = tool.PublicGetArg<string>(call, "key", "default");

        Assert.Equal("default", result);
    }

    [Fact]
    public void GetArg_ReturnsStringArray_FromJsonElementArray()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["items"] = JsonSerializer.SerializeToElement(new[] { "a", "b", "c" }) } };

        var result = tool.PublicGetArg<string[]>(call, "items");

        Assert.Equal(3, result.Length);
        Assert.Equal("a", result[0]);
        Assert.Equal("b", result[1]);
        Assert.Equal("c", result[2]);
    }

    [Fact]
    public void GetArg_ReturnsDefaultValue_ForNonJsonElementValue()
    {
        var tool = CreateTool();
        var call = new ToolCall { Arguments = new() { ["key"] = "plain-string" } };

        var result = tool.PublicGetArg<string>(call, "key", "default");

        Assert.Equal("plain-string", result);
    }

    [Fact]
    public void ResolvePath_ReturnsAbsolutePath_AsIs()
    {
        var tool = CreateTool();
        var context = CreateContext();

        var result = tool.PublicResolvePath(Abs("absolute", "path.txt"), context);

        Assert.Equal(Abs("absolute", "path.txt"), result);
    }

    [Fact]
    public void ResolvePath_ResolvesRelativePath_AgainstWorkingDirectory()
    {
        var tool = CreateTool();
        var context = CreateContext(Abs("work"));

        var result = tool.PublicResolvePath("file.txt", context);

        Assert.Equal(Abs("work", "file.txt"), result);
    }

    [Fact]
    public void ValidatePath_DoesNotThrow_WhenNoAllowedPaths()
    {
        var tool = CreateTool();
        var context = CreateContext();

        tool.PublicValidatePath(Abs("any", "path"), context);
    }

    [Fact]
    public void ValidatePath_DoesNotThrow_WhenPathWithinAllowedPaths()
    {
        var tool = CreateTool();
        var context = CreateContext(allowedPaths: new() { Abs("allowed") });

        tool.PublicValidatePath(Abs("allowed", "file.txt"), context);
    }

    [Fact]
    public void ValidatePath_Throws_WhenPathOutsideAllowedPaths()
    {
        var tool = CreateTool();
        var context = CreateContext(allowedPaths: new() { Abs("allowed") });

        Assert.Throws<UnauthorizedAccessException>(() =>
            tool.PublicValidatePath(Abs("other", "file.txt"), context));
    }

    [Fact]
    public void ValidatePath_IsCaseInsensitive_ForAllowedPaths()
    {
        var tool = CreateTool();
        var context = CreateContext(allowedPaths: new() { Abs("ALLOWED") });

        tool.PublicValidatePath(Abs("allowed", "file.txt").ToLowerInvariant(), context);
    }

    [Fact]
    public void Success_ReturnsResult_WithContent()
    {
        var tool = CreateTool();

        var result = tool.PublicSuccess("hello");

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
        Assert.Equal("test", result.ToolName);
    }

    [Fact]
    public void Success_ReturnsResult_WithData()
    {
        var tool = CreateTool();
        var data = new { Value = 42 };

        var result = tool.PublicSuccess("hello", data);

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
        Assert.Equal(data, result.Data);
    }

    [Fact]
    public void Error_ReturnsResult_WithErrorFlag()
    {
        var tool = CreateTool();

        var result = tool.PublicError("something went wrong");

        Assert.True(result.IsError);
        Assert.Equal("something went wrong", result.Content);
        Assert.Equal("test", result.ToolName);
    }
}
