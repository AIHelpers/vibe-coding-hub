using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

public class ShouldClarifyTests
{
    private static bool Empty() => false;
    private static bool WithSource() => true;

    [Theory]
    // Build requests in an empty folder with little detail: ask.
    [InlineData("Build a new web app for booking rooms", false, RequirementsClarifierMode.Auto, true)]
    [InlineData("Create a todo API", false, RequirementsClarifierMode.Auto, true)]
    [InlineData("Make a dashboard app from scratch", true, RequirementsClarifierMode.Auto, true)]
    // Questions, slash commands and non-build work: never ask.
    [InlineData("Explain the structure of this project", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("Fix the failing test", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("Refactor the chat view model", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("/fix the parser", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("/requirements", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("What does the orchestrator do?", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("How do I build a new web app?", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("Review this pull request", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("Write tests for the parser", false, RequirementsClarifierMode.Auto, false)]
    [InlineData("Make it faster", false, RequirementsClarifierMode.Auto, false)]
    // Enough detail already named (web + JWT auth + PostgreSQL): no questions.
    [InlineData("Build a new web app with JWT auth and PostgreSQL", false, RequirementsClarifierMode.Auto, false)]
    // Auto in an existing project: only a fresh start asks.
    [InlineData("Build a new web app for booking rooms", true, RequirementsClarifierMode.Auto, false)]
    [InlineData("Build a booking app", true, RequirementsClarifierMode.Auto, false)]
    // Off never asks.
    [InlineData("Build a new web app for booking rooms", false, RequirementsClarifierMode.Off, false)]
    // Always asks for every build task, even in an existing project.
    [InlineData("Build a booking app", true, RequirementsClarifierMode.Always, true)]
    [InlineData("Build a new web app with JWT auth and PostgreSQL", false, RequirementsClarifierMode.Always, true)]
    [InlineData("Fix the failing test", true, RequirementsClarifierMode.Always, false)]
    public void ShouldClarify_FollowsTheRules(string prompt, bool hasSourceFiles, RequirementsClarifierMode mode, bool expected)
    {
        Assert.Equal(expected, ClarifierTrigger.ShouldClarify(prompt, () => hasSourceFiles, mode));
    }

    [Fact]
    public void ShouldClarify_DoesNotInspectTheFolder_WhenModeIsOff()
    {
        var inspected = false;
        var result = ClarifierTrigger.ShouldClarify("Build a web app", () => { inspected = true; return false; }, RequirementsClarifierMode.Off);

        Assert.False(result);
        Assert.False(inspected);
    }

    [Fact]
    public void ShouldClarify_DoesNotInspectTheFolder_WhenAlwaysMode()
    {
        var inspected = false;
        ClarifierTrigger.ShouldClarify("Build a web app", () => { inspected = true; return false; }, RequirementsClarifierMode.Always);

        Assert.False(inspected);
    }

    [Fact]
    public void HasSourceFiles_FalseForMissingOrEmptyFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "clarifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(ClarifierTrigger.HasSourceFiles(root));
            Assert.False(ClarifierTrigger.HasSourceFiles(Path.Combine(root, "missing")));
            Assert.False(ClarifierTrigger.HasSourceFiles(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasSourceFiles_IgnoresDependencyAndHiddenFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "clarifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "");
        File.WriteAllText(Path.Combine(root, ".git", "config.cs"), "");
        File.WriteAllText(Path.Combine(root, "bin", "app.cs"), "");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "");
        try
        {
            Assert.False(ClarifierTrigger.HasSourceFiles(root));

            File.WriteAllText(Path.Combine(root, "Program.cs"), "");
            Assert.True(ClarifierTrigger.HasSourceFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppRequirements_ToStatusLine_JoinsSetFieldsOnOneLine()
    {
        var requirements = new AppRequirements
        {
            Platform = "web",
            Auth = "JWT",
            DataModel = "PostgreSQL",
            Deployment = "none"
        };

        Assert.Equal("Requirements: web · JWT auth · PostgreSQL", requirements.ToStatusLine());
        Assert.DoesNotContain("AppRequirements", requirements.ToString());
        Assert.Null(new AppRequirements().ToStatusLine());
    }
}
