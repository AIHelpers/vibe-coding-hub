using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;
using AiCodeAgent.Tools.Code;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Code;

public class LspToolsTests : TempDirTestBase
{
    private static LanguageProviderRegistry CreateRegistry()
    {
        var logger = Substitute.For<ILogger<LanguageProviderRegistry>>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        return new LanguageProviderRegistry(
            new ILanguageProvider[]
            {
                new CSharpLanguageProvider(),
                new TypeScriptLanguageProvider(),
                new PythonLanguageProvider()
            },
            logger,
            loggerFactory);
    }

    [Fact]
    public void FindReferencesTool_ExposesCorrectName()
    {
        var tool = new FindReferencesTool(
            Substitute.For<ILogger<FindReferencesTool>>(),
            CreateRegistry());

        Assert.Equal("find_references", tool.Name);
    }

    [Fact]
    public void FindReferencesTool_HasRequiredParameters()
    {
        var tool = new FindReferencesTool(
            Substitute.For<ILogger<FindReferencesTool>>(),
            CreateRegistry());

        Assert.Contains("path", tool.Definition.Parameters.Required);
        Assert.Contains("line", tool.Definition.Parameters.Required);
        Assert.Contains("character", tool.Definition.Parameters.Required);
    }

    [Fact]
    public void FindReferencesTool_IsReadRisk()
    {
        var tool = new FindReferencesTool(
            Substitute.For<ILogger<FindReferencesTool>>(),
            CreateRegistry());

        Assert.Equal(RiskLevel.Read, tool.Risk);
    }

    [Fact]
    public async Task FindReferencesTool_ReturnsError_WhenServerUnavailable()
    {
        WriteFile("fake.unknown", "content");
        var tool = new FindReferencesTool(
            Substitute.For<ILogger<FindReferencesTool>>(),
            CreateRegistry());

        var result = await tool.ExecuteAsync(
            Call(new() { ["path"] = "fake.unknown", ["line"] = 0, ["character"] = 0 }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("No language server", result.Content);
    }

    [Fact]
    public void GoToDefinitionTool_ExposesCorrectName()
    {
        var tool = new GoToDefinitionTool(
            Substitute.For<ILogger<GoToDefinitionTool>>(),
            CreateRegistry());

        Assert.Equal("go_to_definition", tool.Name);
    }

    [Fact]
    public void GoToDefinitionTool_HasRequiredParameters()
    {
        var tool = new GoToDefinitionTool(
            Substitute.For<ILogger<GoToDefinitionTool>>(),
            CreateRegistry());

        Assert.Contains("path", tool.Definition.Parameters.Required);
        Assert.Contains("line", tool.Definition.Parameters.Required);
        Assert.Contains("character", tool.Definition.Parameters.Required);
    }

    [Fact]
    public void GoToDefinitionTool_IsReadRisk()
    {
        var tool = new GoToDefinitionTool(
            Substitute.For<ILogger<GoToDefinitionTool>>(),
            CreateRegistry());

        Assert.Equal(RiskLevel.Read, tool.Risk);
    }

    [Fact]
    public void GetDiagnosticsTool_ExposesCorrectName()
    {
        var tool = new GetDiagnosticsTool(
            Substitute.For<ILogger<GetDiagnosticsTool>>(),
            CreateRegistry());

        Assert.Equal("get_diagnostics", tool.Name);
    }

    [Fact]
    public void GetDiagnosticsTool_HasRequiredPathParameter()
    {
        var tool = new GetDiagnosticsTool(
            Substitute.For<ILogger<GetDiagnosticsTool>>(),
            CreateRegistry());

        Assert.Contains("path", tool.Definition.Parameters.Required);
        Assert.Contains("severity", tool.Definition.Parameters.Properties.Keys);
    }

    [Fact]
    public void GetDiagnosticsTool_IsReadRisk()
    {
        var tool = new GetDiagnosticsTool(
            Substitute.For<ILogger<GetDiagnosticsTool>>(),
            CreateRegistry());

        Assert.Equal(RiskLevel.Read, tool.Risk);
    }

    [Fact]
    public async Task GetDiagnosticsTool_ReturnsError_WhenServerUnavailable()
    {
        WriteFile("fake.unknown", "content");
        var tool = new GetDiagnosticsTool(
            Substitute.For<ILogger<GetDiagnosticsTool>>(),
            CreateRegistry());

        var result = await tool.ExecuteAsync(
            Call(new() { ["path"] = "fake.unknown" }),
            Context());

        Assert.True(result.IsError);
        Assert.Contains("No language server", result.Content);
    }
}