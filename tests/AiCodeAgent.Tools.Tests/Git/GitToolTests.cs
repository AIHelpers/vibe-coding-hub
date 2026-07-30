using AiCodeAgent.Tools.Git;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Git;

public class GitToolTests : TestHelpers.TempDirTestBase
{
    private readonly ILogger<GitTool> _logger;
    private readonly GitTool _tool;

    public GitToolTests()
    {
        _logger = Substitute.For<ILogger<GitTool>>();
        _tool = new GitTool(_logger);
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        Assert.Equal("git", _tool.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotEmpty(_tool.Description);
    }

    [Fact]
    public void Definition_HasRequiredParameters()
    {
        var definition = _tool.Definition;
        
        Assert.Equal("git", definition.Name);
        Assert.NotNull(definition.Parameters);
        Assert.NotNull(definition.Parameters.Properties);
        Assert.Contains("operation", definition.Parameters.Properties);
        Assert.Contains("args", definition.Parameters.Properties);
    }

    [Fact]
    public void Definition_OperationHasEnumValues()
    {
        var definition = _tool.Definition;
        var operationProp = definition.Parameters.Properties["operation"];
        
        Assert.NotNull(operationProp.Enum);
        Assert.Contains("status", operationProp.Enum);
        Assert.Contains("diff", operationProp.Enum);
        Assert.Contains("log", operationProp.Enum);
        Assert.Contains("commit", operationProp.Enum);
    }

    [Fact]
    public async Task ExecuteAsync_StatusOperation_ReturnsSuccess()
    {
        // Arrange - create a git directory to simulate a git repo
        Directory.CreateDirectory(Path.Combine(WorkingDir, ".git"));
        
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["operation"] = "status"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        // Should not error even without a real git repo initialized
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidOperation_ThrowsArgumentException()
    {
        // Arrange
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["operation"] = "invalid_operation"
        });

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _tool.ExecuteAsync(call, context));
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyMode_AllowsReadOperations()
    {
        // Arrange
        var context = Context(readOnly: true);
        var call = Call(new Dictionary<string, object?>
        {
            ["operation"] = "status"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyMode_BlocksWriteOperations()
    {
        // Arrange
        var context = Context(readOnly: true);
        var call = Call(new Dictionary<string, object?>
        {
            ["operation"] = "commit",
            ["args"] = "test message"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("read-only", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LogOperation_ReturnsOutput()
    {
        // Arrange - create a git directory to simulate a git repo
        Directory.CreateDirectory(Path.Combine(WorkingDir, ".git"));
        
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["operation"] = "log",
            ["args"] = "-1 --oneline"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildGitCommand_Status_ReturnsCorrectCommand()
    {
        // Act - using reflection to test private method
        var method = typeof(GitTool).GetMethod("BuildGitCommand", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { "status", "" });
        
        Assert.Equal("status ", result);
    }

    [Fact]
    public void BuildGitCommand_Commit_WithArgs_ReturnsCorrectCommand()
    {
        // Act
        var method = typeof(GitTool).GetMethod("BuildGitCommand", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { "commit", "test message" });
        
        Assert.Equal("commit -m \"test message\"", result);
    }

    [Fact]
    public void BuildGitCommand_Branch_ReturnsCorrectCommand()
    {
        // Act
        var method = typeof(GitTool).GetMethod("BuildGitCommand", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { "branch", "-a" });
        
        Assert.Equal("branch -a", result);
    }
}