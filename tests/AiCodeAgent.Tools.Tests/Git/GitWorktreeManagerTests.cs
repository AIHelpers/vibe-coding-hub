using System.Diagnostics;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Tools.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.Git;

public class GitWorktreeManagerTests : TestHelpers.TempDirTestBase
{
    private readonly GitWorktreeManager _manager = new(NullLogger<GitWorktreeManager>.Instance);
    private string RepoDir => Path.Combine(WorkingDir, "repo");

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private void InitRepo()
    {
        Directory.CreateDirectory(RepoDir);
        Git(RepoDir, "init", "-q");
        File.WriteAllText(Path.Combine(RepoDir, "a.txt"), "one\n");
        Git(RepoDir, "add", "-A");
        Git(RepoDir, "commit", "-q", "-m", "init");
    }

    [Fact]
    public async Task Acquire_ReturnsNull_OutsideGitRepo()
    {
        Directory.CreateDirectory(RepoDir);
        Assert.Null(await _manager.AcquireAsync(RepoDir, "session1", "impl"));
    }

    [Fact]
    public async Task Acquire_ReturnsNull_WhenRepoHasNoCommits()
    {
        Directory.CreateDirectory(RepoDir);
        Git(RepoDir, "init", "-q");
        Assert.Null(await _manager.AcquireAsync(RepoDir, "session1", "impl"));
    }

    [Fact]
    public async Task Acquire_CreatesIsolatedCheckout()
    {
        InitRepo();
        var lease = await _manager.AcquireAsync(RepoDir, "session1", "impl");

        Assert.NotNull(lease);
        Assert.NotEqual(Path.GetFullPath(RepoDir), Path.GetFullPath(lease!.WorkingDirectory));
        Assert.True(File.Exists(Path.Combine(lease.WorkingDirectory, "a.txt")));
        Assert.StartsWith("agent/", lease.Branch);
    }

    [Fact]
    public async Task Complete_NoChanges_RemovesWorktree()
    {
        InitRepo();
        var lease = (await _manager.AcquireAsync(RepoDir, "session1", "impl"))!;

        var result = await _manager.CompleteAsync(lease, "nothing", merge: true);

        Assert.Equal(WorkspaceMergeOutcome.NoChanges, result.Outcome);
        Assert.False(Directory.Exists(lease.RootPath));
    }

    [Fact]
    public async Task Complete_MergesAgentChangesIntoBaseCheckout()
    {
        InitRepo();
        var lease = (await _manager.AcquireAsync(RepoDir, "session1", "impl"))!;
        File.WriteAllText(Path.Combine(lease.WorkingDirectory, "new.txt"), "hello\n");

        var result = await _manager.CompleteAsync(lease, "add new.txt", merge: true);

        Assert.Equal(WorkspaceMergeOutcome.Merged, result.Outcome);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(RepoDir, "new.txt")));
        Assert.False(Directory.Exists(lease.RootPath));
    }

    [Fact]
    public async Task ParallelAgents_EditingDifferentFiles_BothMerge()
    {
        InitRepo();
        var a = (await _manager.AcquireAsync(RepoDir, "session1", "agent-a"))!;
        var b = (await _manager.AcquireAsync(RepoDir, "session1", "agent-b"))!;
        File.WriteAllText(Path.Combine(a.WorkingDirectory, "a-only.txt"), "A\n");
        File.WriteAllText(Path.Combine(b.WorkingDirectory, "b-only.txt"), "B\n");

        var ra = await _manager.CompleteAsync(a, "a", merge: true);
        var rb = await _manager.CompleteAsync(b, "b", merge: true);

        Assert.True(ra.Succeeded);
        Assert.True(rb.Succeeded);
        Assert.True(File.Exists(Path.Combine(RepoDir, "a-only.txt")));
        Assert.True(File.Exists(Path.Combine(RepoDir, "b-only.txt")));
    }

    [Fact]
    public async Task ParallelAgents_ConflictingEdits_KeepsSecondBranchAndAbortsMerge()
    {
        InitRepo();
        var a = (await _manager.AcquireAsync(RepoDir, "session1", "agent-a"))!;
        var b = (await _manager.AcquireAsync(RepoDir, "session1", "agent-b"))!;
        File.WriteAllText(Path.Combine(a.WorkingDirectory, "a.txt"), "from A\n");
        File.WriteAllText(Path.Combine(b.WorkingDirectory, "a.txt"), "from B\n");

        var ra = await _manager.CompleteAsync(a, "a", merge: true);
        var rb = await _manager.CompleteAsync(b, "b", merge: true);

        Assert.Equal(WorkspaceMergeOutcome.Merged, ra.Outcome);
        Assert.Equal(WorkspaceMergeOutcome.Conflict, rb.Outcome);
        Assert.Contains("a.txt", rb.ConflictFiles);
        Assert.True(Directory.Exists(b.RootPath));
        // Base checkout is left clean with A's version, not stuck mid-merge.
        Assert.Equal("from A\n", File.ReadAllText(Path.Combine(RepoDir, "a.txt")));
        Assert.Equal(string.Empty, Git(RepoDir, "status", "--porcelain").Trim());
    }

    [Fact]
    public async Task Complete_WithMergeFalse_KeepsBranch()
    {
        InitRepo();
        var lease = (await _manager.AcquireAsync(RepoDir, "session1", "impl"))!;
        File.WriteAllText(Path.Combine(lease.WorkingDirectory, "wip.txt"), "x\n");

        var result = await _manager.CompleteAsync(lease, "wip", merge: false);

        Assert.Equal(WorkspaceMergeOutcome.Kept, result.Outcome);
        Assert.False(File.Exists(Path.Combine(RepoDir, "wip.txt")));
        Assert.True(Directory.Exists(lease.RootPath));
    }

    [Theory]
    [InlineData("agent one", "agent-one")]
    [InlineData("../evil", "evil")]
    [InlineData("", "agent")]
    public void Slug_SanitizesNames(string input, string expected) =>
        Assert.Equal(expected, GitWorktreeManager.Slug(input));
}
