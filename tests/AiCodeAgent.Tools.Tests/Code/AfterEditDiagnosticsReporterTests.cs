using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;
using AiCodeAgent.Tools.Code;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Code;

public class AfterEditDiagnosticsReporterTests : TempDirTestBase
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
    public async Task ReportAsync_ReturnsEmpty_WhenNoProviderForExtension()
    {
        var reporter = new AfterEditDiagnosticsReporter(
            CreateRegistry(),
            Substitute.For<ILogger<AfterEditDiagnosticsReporter>>());

        // .unknown has no provider
        var result = await reporter.ReportAsync(
            Path.Combine(WorkingDir, "file.unknown"),
            "content",
            WorkingDir);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ReportAsync_ReturnsEmpty_WhenServerUnavailable()
    {
        var reporter = new AfterEditDiagnosticsReporter(
            CreateRegistry(),
            Substitute.For<ILogger<AfterEditDiagnosticsReporter>>());
        reporter.WaitForDiagnostics = TimeSpan.FromMilliseconds(50);

        // C# provider exists but no OmniSharp server running in test env.
        var csFile = Path.Combine(WorkingDir, "Test.cs");
        await File.WriteAllTextAsync(csFile, "class Test { }");

        var result = await reporter.ReportAsync(csFile, "class Test { }", WorkingDir);

        // No server -> no diagnostics surfaced (empty, not throw).
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ReportAsync_DoesNotThrow_OnMissingFile()
    {
        var reporter = new AfterEditDiagnosticsReporter(
            CreateRegistry(),
            Substitute.For<ILogger<AfterEditDiagnosticsReporter>>());
        reporter.WaitForDiagnostics = TimeSpan.FromMilliseconds(50);

        // Pass a path that doesn't exist; should be handled gracefully.
        var result = await reporter.ReportAsync(
            Path.Combine(WorkingDir, "Missing.cs"),
            "class Test { }",
            WorkingDir);

        Assert.Equal(string.Empty, result);
    }
}