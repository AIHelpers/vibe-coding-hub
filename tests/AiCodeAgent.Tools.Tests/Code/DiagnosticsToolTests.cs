using AiCodeAgent.Tools.Code;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Code;

public class DiagnosticsToolTests : TestHelpers.TempDirTestBase
{
    private readonly ILogger<DiagnosticsTool> _logger;
    private readonly DiagnosticsTool _tool;

    public DiagnosticsToolTests()
    {
        _logger = Substitute.For<ILogger<DiagnosticsTool>>();
        _tool = new DiagnosticsTool(_logger);
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        Assert.Equal("run_diagnostics", _tool.Name);
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
        
        Assert.Equal("run_diagnostics", definition.Name);
        Assert.NotNull(definition.Parameters);
        Assert.NotNull(definition.Parameters.Properties);
        Assert.Contains("type", definition.Parameters.Properties);
        Assert.Contains("path", definition.Parameters.Properties);
        Assert.Contains("args", definition.Parameters.Properties);
    }

    [Fact]
    public void Definition_TypeHasEnumValues()
    {
        var definition = _tool.Definition;
        var typeProp = definition.Parameters.Properties["type"];
        
        Assert.NotNull(typeProp.Enum);
        Assert.Contains("build", typeProp.Enum);
        Assert.Contains("test", typeProp.Enum);
        Assert.Contains("lint", typeProp.Enum);
        Assert.Contains("format_check", typeProp.Enum);
    }

    [Fact]
    public void DetectProjectType_DotnetProject_ReturnsDotnetCommands()
    {
        // Arrange - create a .csproj file
        WriteFile("test.csproj", "<Project><TargetFramework>net8.0</TargetFramework></Project>");
        
        // Act
        var method = typeof(DiagnosticsTool).GetMethod("DetectProjectType", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { WorkingDir, "build", "" });
        
        Assert.NotNull(result);
        var (command, detector) = ((string? command, string detector))result!;
        Assert.Equal("dotnet", detector);
        Assert.Contains("dotnet build", command);
    }

    [Fact]
    public void DetectProjectType_NpmProject_ReturnsNpmCommands()
    {
        // Arrange - create package.json
        WriteFile("package.json", "{}");
        
        // Act
        var method = typeof(DiagnosticsTool).GetMethod("DetectProjectType", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { WorkingDir, "test", "" });
        
        Assert.NotNull(result);
        var (command, detector) = ((string? command, string detector))result!;
        Assert.Equal("npm", detector);
        Assert.Contains("npm test", command);
    }

    [Fact]
    public void DetectProjectType_PythonProject_ReturnsPythonCommands()
    {
        // Arrange - create a Python file
        WriteFile("test.py", "print('hello')");
        
        // Act
        var method = typeof(DiagnosticsTool).GetMethod("DetectProjectType", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { WorkingDir, "test", "" });
        
        Assert.NotNull(result);
        var (command, detector) = ((string? command, string detector))result!;
        Assert.Equal("python", detector);
        Assert.Contains("pytest", command);
    }

    [Fact]
    public void DetectProjectType_UnknownProject_ReturnsNull()
    {
        // Arrange - empty directory
        
        // Act
        var method = typeof(DiagnosticsTool).GetMethod("DetectProjectType", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { WorkingDir, "build", "" });
        
        Assert.NotNull(result);
        var (command, detector) = ((string? command, string detector))result!;
        Assert.Null(command);
        Assert.Equal("unknown", detector);
    }

    [Fact]
    public void ParseDiagnosticOutput_ExitCodeZero_ReturnsPassed()
    {
        // Arrange
        var method = typeof(DiagnosticsTool).GetMethod("ParseDiagnosticOutput", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        // Act
        var result = method?.Invoke(null, new object?[] { "some output", "dotnet", 0 });
        
        Assert.NotNull(result);
        Assert.Contains("Passed", (string)result);
    }

    [Fact]
    public void ParseDiagnosticOutput_NonZeroExitCode_ReturnsFailed()
    {
        // Arrange
        var method = typeof(DiagnosticsTool).GetMethod("ParseDiagnosticOutput", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        // Act
        var result = method?.Invoke(null, new object?[] { "error CS1234: Something wrong", "dotnet", 1 });
        
        Assert.NotNull(result);
        var resultStr = (string)result;
        Assert.Contains("Failed", resultStr);
        Assert.Contains("error", resultStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDiagnosticOutput_TruncatesLongOutput()
    {
        // Arrange - create long output
        var longOutput = new string('a', 10000);
        
        // Act
        var method = typeof(DiagnosticsTool).GetMethod("ParseDiagnosticOutput", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = method?.Invoke(null, new object?[] { longOutput, "dotnet", 1 });
        
        Assert.NotNull(result);
        // Should contain truncated output (last 5000 chars)
        Assert.True(((string)result).Length <= 6000);
    }
}