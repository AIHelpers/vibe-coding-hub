using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.App.Tests.Setup;

/// <summary>E3-S3: the sample that ships inside the app is the one in <c>samples/TodoApp</c>.</summary>
public class EmbeddedSampleFileSourceTests
{
    private static string SampleFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "TodoApp");
    }

    [Fact]
    public void TheAppEmbedsEveryFileOfTheSample_DotFilesIncluded()
    {
        var embedded = new EmbeddedSampleFileSource().GetFiles().Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var onDisk = new DirectorySampleFileSource(SampleFolder()).GetFiles().Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(embedded);
        Assert.Equal(onDisk, embedded);
        Assert.Contains(".gitignore", embedded);
        Assert.Contains("AGENTS.md", embedded);
        Assert.Contains("src/TodoApp/TodoService.cs", embedded);
        Assert.Contains(embedded, p => p.StartsWith(".aiagent/characters/", StringComparison.Ordinal));
        Assert.Contains(embedded, p => p.StartsWith(".aiagent/skills/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEmbeddedFilesHaveTheSameContentAsTheFilesOnDisk()
    {
        var onDisk = new DirectorySampleFileSource(SampleFolder()).GetFiles().ToDictionary(f => f.RelativePath);

        foreach (var file in new EmbeddedSampleFileSource().GetFiles())
        {
            using var a = file.Open();
            using var b = onDisk[file.RelativePath].Open();
            using var ma = new MemoryStream();
            using var mb = new MemoryStream();
            a.CopyTo(ma);
            b.CopyTo(mb);
            Assert.True(ma.ToArray().SequenceEqual(mb.ToArray()), file.RelativePath);
        }
    }

    [Fact]
    public async Task TheEmbeddedSample_InstallsAsAnOpenableProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "embedded-sample-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installer = new SampleProjectInstaller(new EmbeddedSampleFileSource());

            var result = await installer.InstallAsync(root);

            Assert.True(File.Exists(Path.Combine(result.Path, "TodoApp.slnx")));
            Assert.True(File.Exists(Path.Combine(result.Path, ".aiagent", "characters", "dana-dotnet.md")));
            Assert.True(result.GitReady, result.Warning);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }
}
