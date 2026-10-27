using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>E3-S3: the sample in <c>samples/TodoApp</c> has everything the story asks for.</summary>
public sealed class SampleProjectContentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sample-content-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(SampleRepo.SampleFolder(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("TodoApp.slnx")]
    [InlineData("AGENTS.md")]
    [InlineData(".gitignore")]
    [InlineData("src/TodoApp/TodoApp.csproj")]
    [InlineData("src/TodoApp/TodoService.cs")]
    [InlineData("tests/TodoApp.Tests/TodoApp.Tests.csproj")]
    [InlineData("tests/TodoApp.Tests/TodoServiceTests.cs")]
    public void TheSampleHasTheFile(string relative)
    {
        Assert.True(File.Exists(Path.Combine(SampleRepo.SampleFolder(), relative.Replace('/', Path.DirectorySeparatorChar))), relative);
    }

    [Fact]
    public void BothProjectsTargetNet10_AndTheSolutionListsThem()
    {
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", Read("src/TodoApp/TodoApp.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", Read("tests/TodoApp.Tests/TodoApp.Tests.csproj"));
        Assert.Contains("xunit", Read("tests/TodoApp.Tests/TodoApp.Tests.csproj"));

        var slnx = Read("TodoApp.slnx");
        Assert.Contains("src/TodoApp/TodoApp.csproj", slnx);
        Assert.Contains("tests/TodoApp.Tests/TodoApp.Tests.csproj", slnx);
    }

    [Fact]
    public void AgentsMd_GivesTheExactBuildAndTestCommands()
    {
        var agents = Read("AGENTS.md");

        Assert.Contains("dotnet build TodoApp.slnx", agents);
        Assert.Contains("dotnet test TodoApp.slnx", agents);
    }

    [Fact]
    public void GitIgnore_ExcludesBinObjAndLogs()
    {
        var lines = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToArray();

        Assert.Contains("bin/", lines);
        Assert.Contains("obj/", lines);
        Assert.Contains("logs/", lines);
    }

    [Fact]
    public void TheExampleCharactersAndSkillsAreThere_AndEveryReferencedSkillExists()
    {
        var characters = Directory.GetFiles(Path.Combine(SampleRepo.SampleFolder(), ".aiagent", "characters"), "*.md");
        Assert.True(characters.Length >= 3, "the example characters");

        var skills = Directory.GetDirectories(Path.Combine(SampleRepo.SampleFolder(), ".aiagent", "skills"))
            .Select(d => Path.GetFileName(d)!).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(skills);
        foreach (var skill in skills)
            Assert.True(File.Exists(Path.Combine(SampleRepo.SampleFolder(), ".aiagent", "skills", skill, "SKILL.md")), skill);

        // "skills: [a, b]" and "pinned-skills: [c]" in the characters must all be in the folder.
        foreach (var file in characters)
        {
            foreach (var line in File.ReadAllLines(file).Where(l => l.StartsWith("skills:") || l.StartsWith("pinned-skills:")))
            {
                var list = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
                foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Assert.True(skills.Contains(name), $"{Path.GetFileName(file)} uses skill '{name}' which is not in the sample");
            }
        }
    }

    [Fact]
    public void TheDeliberateBug_IsInTheShippedSample()
    {
        Assert.Contains("x.Due <= today", Read("src/TodoApp/TodoService.cs"));
        Assert.Contains("public void Overdue_ExcludesCompletedItems()", Read("tests/TodoApp.Tests/TodoServiceTests.cs"));
    }

    [Fact]
    public void TheDirectorySource_SkipsBuildOutput()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "obj"));
        File.WriteAllText(Path.Combine(_dir, "src", "bin", "x.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, "obj", "y.json"), "y");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "k");

        var files = new DirectorySampleFileSource(_dir).GetFiles().Select(f => f.RelativePath).ToArray();

        Assert.Equal(new[] { "keep.txt" }, files);
    }

    [Fact]
    public async Task InstallingTheRealSample_GivesACleanGitRepositoryWithOneCommit()
    {
        var installer = new SampleProjectInstaller(new DirectorySampleFileSource(SampleRepo.SampleFolder()));

        var result = await installer.InstallAsync(_dir);

        Assert.True(result.GitReady, result.Warning);
        var git = new ProcessGitRunner();
        var status = await git.RunAsync(result.Path, new[] { "status", "--porcelain" }, CancellationToken.None);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal(string.Empty, status.Output);
        var count = await git.RunAsync(result.Path, new[] { "rev-list", "--count", "HEAD" }, CancellationToken.None);
        Assert.Equal("1", count.Output);
    }
}
