using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Tests.Context;

public class ProjectMemoryLoaderTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiagent-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Parse_ExtractsKnownSections()
    {
        var content = """
            # AIAGENT.md

            ## Instructions
            Do the thing.

            ## Conventions
            Use tabs.

            ## Compact Instructions
            Keep tool results.

            ## Build Commands
            dotnet build

            ## Test Commands
            dotnet test

            ## Lint Commands
            dotnet format --verify
            """;

        var mem = ProjectMemoryLoader.Parse("/tmp/AIAGENT.md", content);

        Assert.Contains("Do the thing.", mem.Instructions);
        Assert.Contains("Use tabs.", mem.Conventions);
        Assert.Contains("Keep tool results.", mem.CompactInstructions);
        Assert.Contains("dotnet build", mem.BuildCommands);
        Assert.Contains("dotnet test", mem.TestCommands);
        Assert.Contains("dotnet format --verify", mem.LintCommands);
    }

    [Fact]
    public void Parse_ExtractsCustomSections()
    {
        var content = """
            # AIAGENT.md

            ## Instructions
            base

            ## Deployment
            fly deploy

            ## Notes
            remember X
            """;

        var mem = ProjectMemoryLoader.Parse("/tmp/AIAGENT.md", content);

        Assert.Empty(mem.BuildCommands);
        Assert.Equal(2, mem.CustomSections.Count);
        Assert.Contains("Deployment", mem.CustomSections.Keys);
        Assert.Contains("fly deploy", mem.CustomSections["Deployment"]);
        Assert.Contains("remember X", mem.CustomSections["Notes"]);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNoFile()
    {
        var dir = CreateTempDir();
        try
        {
            var loader = new ProjectMemoryLoader();
            var mem = await loader.LoadAsync(dir);
            Assert.Null(mem);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LoadAsync_ReadsLocalFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "AIAGENT.md");
            await File.WriteAllTextAsync(path, "## Instructions\nhello world\n");

            var loader = new ProjectMemoryLoader();
            var mem = await loader.LoadAsync(dir);

            Assert.NotNull(mem);
            Assert.Equal(path, mem!.FilePath);
            Assert.Contains("hello world", mem.Instructions);
            Assert.True(mem.HasContent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InitAsync_CreatesTemplateFile()
    {
        var dir = CreateTempDir();
        try
        {
            var loader = new ProjectMemoryLoader();
            var path = await loader.InitAsync(dir);

            Assert.True(File.Exists(path));
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("## Instructions", text);
            Assert.Contains("## Conventions", text);
            Assert.Contains("## Compact Instructions", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InitAsync_DoesNotOverwrite_ExistingFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "AIAGENT.md");
            await File.WriteAllTextAsync(path, "CUSTOM");

            var loader = new ProjectMemoryLoader();
            var result = await loader.InitAsync(dir);

            var text = await File.ReadAllTextAsync(result);
            Assert.Equal("CUSTOM", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DiagnoseAsync_ReportsMissingMemory_AsWarning()
    {
        var dir = CreateTempDir();
        try
        {
            var loader = new ProjectMemoryLoader();
            var report = await loader.DiagnoseAsync(dir);

            var memCheck = Assert.Single(report.Checks.Where(c => c.Name == "AIAGENT.md"));
            Assert.Equal(DoctorStatus.Warning, memCheck.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DiagnoseAsync_ReportsExistingMemory_AsOk()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "AIAGENT.md");
            await File.WriteAllTextAsync(path, "## Instructions\nx\n");

            var loader = new ProjectMemoryLoader();
            var report = await loader.DiagnoseAsync(dir);

            var memCheck = Assert.Single(report.Checks.Where(c => c.Name == "AIAGENT.md"));
            Assert.Equal(DoctorStatus.Ok, memCheck.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ToPromptBlock_ReturnsEmpty_WhenNoContent()
    {
        var mem = new ProjectMemory { RawContent = "" };
        Assert.Equal(string.Empty, mem.ToPromptBlock());
    }

    [Fact]
    public void ToPromptBlock_ContainsRawContent_WhenPresent()
    {
        var mem = new ProjectMemory { RawContent = "## Instructions\nhello\n" };
        var block = mem.ToPromptBlock();
        Assert.Contains("hello", block);
        Assert.Contains("Project Memory", block);
    }
}