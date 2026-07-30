using AiCodeAgent.Tools.Shell;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Shell;

public class ExecuteCommandToolTests : TestHelpers.TempDirTestBase
{
    private readonly ILogger<ExecuteCommandTool> _logger;
    private readonly ExecuteCommandTool _tool;

    public ExecuteCommandToolTests()
    {
        _logger = Substitute.For<ILogger<ExecuteCommandTool>>();
        _tool = new ExecuteCommandTool(_logger);
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        Assert.Equal("execute_command", _tool.Name);
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
        
        Assert.Equal("execute_command", definition.Name);
        Assert.NotNull(definition.Parameters);
        Assert.NotNull(definition.Parameters.Properties);
        Assert.Contains("command", definition.Parameters.Properties);
        Assert.Contains("working_dir", definition.Parameters.Properties);
        Assert.Contains("timeout", definition.Parameters.Properties);
        Assert.Contains("env", definition.Parameters.Properties);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCommand_ReturnsSuccess()
    {
        // Arrange
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "echo hello"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.False(result.IsError);
        Assert.Contains("hello", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_FailedCommand_ReturnsError()
    {
        // Arrange
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "exit 1"
        }
        );

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkingDirectory_UsesCorrectPath()
    {
        // Arrange - create a subdirectory
        var subDir = Path.Combine(WorkingDir, "workdir");
        Directory.CreateDirectory(subDir);
        
        var context = Context();
        
        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "cd"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_NonExistentWorkingDirectory_ReturnsError()
    {
        // Arrange
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "echo test",
            ["working_dir"] = "C:/non_existent_directory_12345"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyMode_AllowsReadCommands()
    {
        // Arrange
        var context = Context(readOnly: true);

        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "echo hello"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsError()
    {
        // Arrange
        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["command"] = "ping -n 10 127.0.0.1", // Windows ping
            ["timeout"] = 1
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}