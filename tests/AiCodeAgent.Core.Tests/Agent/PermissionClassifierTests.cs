using System.Collections.Generic;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

public class PermissionClassifierTests
{
    private readonly PermissionClassifier _classifier = new();

    private static ToolCall Call(string name, string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command != null)
            args["command"] = command;
        return new ToolCall { Name = name, Arguments = args };
    }

    [Fact]
    public void Classify_Read_AlwaysAllows()
    {
        Assert.Equal(PermissionDecision.Allow, _classifier.Classify(Call("read_file"), RiskLevel.Read));
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build")]
    [InlineData("npm test")]
    [InlineData("npm run test")]
    [InlineData("git status")]
    [InlineData("git diff")]
    [InlineData("git log")]
    [InlineData("ls")]
    [InlineData("dir")]
    [InlineData("cat README.md")]
    [InlineData("grep foo .")]
    [InlineData("find . -name *.cs")]
    [InlineData("echo hello")]
    [InlineData("pwd")]
    [InlineData("make")]
    [InlineData("node --version")]
    public void Classify_SafeCommand_Allows(string command)
    {
        Assert.Equal(PermissionDecision.Allow, _classifier.Classify(Call("execute_command", command), RiskLevel.Execute));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("curl http://example.com")]
    [InlineData("chmod 777 .")]
    [InlineData("shutdown /s")]
    [InlineData("format c:")]
    public void Classify_DangerousCommand_Denies(string command)
    {
        Assert.Equal(PermissionDecision.Deny, _classifier.Classify(Call("execute_command", command), RiskLevel.Execute));
    }

    [Fact]
    public void Classify_Execute_WithoutCommand_Denies()
    {
        Assert.Equal(PermissionDecision.Deny, _classifier.Classify(Call("execute_command"), RiskLevel.Execute));
    }

    [Fact]
    public void Classify_Write_Allows()
    {
        Assert.Equal(PermissionDecision.Allow, _classifier.Classify(Call("write_file"), RiskLevel.Write));
    }

    [Theory]
    [InlineData("mkdir newdir")]
    [InlineData("touch file.txt")]
    [InlineData("cp a.txt b.txt")]
    [InlineData("mv a.txt b.txt")]
    public void Classify_CommonFilesystemCommand_Allows(string command)
    {
        Assert.Equal(PermissionDecision.Allow, _classifier.Classify(Call("execute_command", command), RiskLevel.Execute));
    }
}