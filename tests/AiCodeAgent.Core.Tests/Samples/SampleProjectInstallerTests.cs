using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>E3-S3: "Try the sample project" copies the sample and sets up git.</summary>
public sealed class SampleProjectInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sample-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private sealed class MemorySource : ISampleFileSource
    {
        private readonly Dictionary<string, string> _files;
        public MemorySource(Dictionary<string, string> files) => _files = files;
        public IReadOnlyList<SampleFile> GetFiles() =>
            _files.Select(f => new SampleFile(f.Key, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.Value)))).ToList();
    }

    private sealed class FakeGit : IGitRunner
    {
        public List<string> Calls { get; } = new();
        public Func<IReadOnlyList<string>, (int, string)> Respond { get; set; } = _ => (0, string.Empty);

        public Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(Respond(arguments));
        }
    }

    private static MemorySource Sample() => new(new Dictionary<string, string>
    {
        ["AGENTS.md"] = "# agents",
        [".gitignore"] = "bin/\nobj/\nlogs/\n",
        ["src/TodoApp/TodoService.cs"] = "class TodoService {}",
        [".aiagent/characters/dana.md"] = "---\nid: dana\n---\n"
    });

    [Fact]
    public async Task Install_CopiesEveryFile_IncludingDotFolders()
    {
        var installer = new SampleProjectInstaller(Sample(), new FakeGit());

        var result = await installer.InstallAsync(_root);

        Assert.Equal(Path.Combine(_root, "TodoApp"), result.Path);
        Assert.Equal("# agents", File.ReadAllText(Path.Combine(result.Path, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(result.Path, "src", "TodoApp", "TodoService.cs")));
        Assert.True(File.Exists(Path.Combine(result.Path, ".aiagent", "characters", "dana.md")));
        Assert.True(File.Exists(Path.Combine(result.Path, ".gitignore")));
    }

    [Fact]
    public async Task Install_InitializesGit_AndMakesTheInitialCommit()
    {
        var git = new FakeGit();
        var installer = new SampleProjectInstaller(Sample(), git);

        var result = await installer.InstallAsync(_root);

        Assert.True(result.GitReady);
        Assert.Null(result.Warning);
        Assert.Equal("init --initial-branch=main", git.Calls[0]);
        Assert.Contains("add -A", git.Calls);
        var commit = git.Calls.Last();
        Assert.StartsWith("commit --no-gpg-sign -m", commit);
        Assert.True(git.Calls.IndexOf("add -A") < git.Calls.Count - 1, "add comes before commit");
    }

    [Fact]
    public async Task Install_SetsAnIdentityOnlyWhenTheUserHasNone()
    {
        var withIdentity = new FakeGit { Respond = a => a[0] == "config" && a.Count == 2 ? (0, "Ada") : (0, "") };
        await new SampleProjectInstaller(Sample(), withIdentity).InstallAsync(Path.Combine(_root, "a"));
        Assert.DoesNotContain(withIdentity.Calls, c => c.StartsWith("config user.name ") || c.StartsWith("config user.email "));

        var noIdentity = new FakeGit { Respond = a => a[0] == "config" && a.Count == 2 ? (1, "") : (0, "") };
        await new SampleProjectInstaller(Sample(), noIdentity).InstallAsync(Path.Combine(_root, "b"));
        Assert.Contains("config user.name Vibe Coding Hub", noIdentity.Calls);
        Assert.Contains("config user.email sample@vibecodinghub.local", noIdentity.Calls);
    }

    [Fact]
    public async Task Install_FallsBackToPlainGitInit_ForOldGit()
    {
        var git = new FakeGit { Respond = a => a[0] == "init" && a.Count == 2 ? (129, "unknown option") : (0, "") };

        var result = await new SampleProjectInstaller(Sample(), git).InstallAsync(_root);

        Assert.True(result.GitReady);
        Assert.Contains("init", git.Calls);
    }

    [Fact]
    public async Task Install_WithoutGit_StillCopiesTheProject_AndWarns()
    {
        var git = new FakeGit { Respond = _ => (-1, "git is not installed or not on PATH") };

        var result = await new SampleProjectInstaller(Sample(), git).InstallAsync(_root);

        Assert.False(result.GitReady);
        Assert.Contains("git is not installed", result.Warning);
        Assert.True(File.Exists(Path.Combine(result.Path, "AGENTS.md")));
    }

    [Fact]
    public async Task Install_NeverTouchesAnExistingFolder_ItMakesTodoApp2()
    {
        var installer = new SampleProjectInstaller(Sample(), new FakeGit());
        var first = await installer.InstallAsync(_root);
        File.WriteAllText(Path.Combine(first.Path, "AGENTS.md"), "my own edits");

        var second = await installer.InstallAsync(_root);
        var third = await installer.InstallAsync(_root);

        Assert.Equal(Path.Combine(_root, "TodoApp-2"), second.Path);
        Assert.Equal(Path.Combine(_root, "TodoApp-3"), third.Path);
        Assert.Equal("my own edits", File.ReadAllText(Path.Combine(first.Path, "AGENTS.md")));
        Assert.Equal("# agents", File.ReadAllText(Path.Combine(second.Path, "AGENTS.md")));
    }

    [Fact]
    public async Task Install_RejectsAFilePathThatEscapesTheProject()
    {
        var source = new MemorySource(new Dictionary<string, string> { ["../evil.txt"] = "x" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SampleProjectInstaller(source, new FakeGit()).InstallAsync(_root));

        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "TodoApp")), "the half-made copy is removed");
    }

    [Fact]
    public async Task Install_WithNoFiles_Throws()
    {
        var installer = new SampleProjectInstaller(new MemorySource(new Dictionary<string, string>()), new FakeGit());

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(_root));
    }

    [Fact]
    public void DefaultSamplesRoot_IsDocumentsVibeCodingHubSamples()
    {
        var root = SampleProjectInstaller.DefaultSamplesRoot();

        Assert.EndsWith(Path.Combine("VibeCodingHub", "samples"), root);
    }

    [Fact]
    public void FirstTaskPrompt_IsTheTextFromTheStory()
    {
        Assert.Equal(
            "The Overdue_ExcludesCompletedItems test is failing. Fix the overdue logic in TodoService and verify with the tests.",
            SampleProjectInstaller.FirstTaskPrompt);
    }
}
