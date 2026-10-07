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
        // Keep files byte-for-byte: Windows runners set core.autocrlf=true globally, which would
        // check merged files out with CRLF. Worktrees share this repo-level setting.
        Git(RepoDir, "config", "core.autocrlf", "false");
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

    [Fact]
    public async Task UncommittedWork_IsVisibleToAgents_AndResultsComeBackUncommitted()
    {
        InitRepo();
        // An earlier step changed a tracked file and created a new one, without committing.
        File.WriteAllText(Path.Combine(RepoDir, "a.txt"), "one\nplan\n");
        File.WriteAllText(Path.Combine(RepoDir, "plan.md"), "the plan\n");
        var head = Git(RepoDir, "rev-parse", "HEAD").Trim();

        var lease = (await _manager.AcquireAsync(RepoDir, "flow1/backend#1", "backend"))!;
        Assert.NotNull(lease.BaseSnapshot);
        Assert.Equal("one\nplan\n", File.ReadAllText(Path.Combine(lease.WorkingDirectory, "a.txt")));
        Assert.Equal("the plan\n", File.ReadAllText(Path.Combine(lease.WorkingDirectory, "plan.md")));
        // Taking the snapshot touched nothing in the main checkout.
        Assert.Equal(head, Git(RepoDir, "rev-parse", "HEAD").Trim());
        Assert.Contains("?? plan.md", Git(RepoDir, "status", "--porcelain"));

        File.WriteAllText(Path.Combine(lease.WorkingDirectory, "api.cs"), "class Api {}\n");
        var result = await _manager.CompleteAsync(lease, "backend", merge: true);

        Assert.Equal(WorkspaceMergeOutcome.Merged, result.Outcome);
        Assert.Contains("uncommitted", result.Message);
        Assert.Equal("class Api {}\n", File.ReadAllText(Path.Combine(RepoDir, "api.cs")));
        Assert.Equal("one\nplan\n", File.ReadAllText(Path.Combine(RepoDir, "a.txt")));
        Assert.Equal(head, Git(RepoDir, "rev-parse", "HEAD").Trim()); // no commit on the user's branch
        Assert.False(Directory.Exists(lease.RootPath));
    }

    [Fact]
    public async Task UncommittedWork_TwoParallelAgents_BothApply_AndConflictsAreKept()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(RepoDir, "plan.md"), "the plan\n");
        var a = (await _manager.AcquireAsync(RepoDir, "flow2/a#1", "agent-a"))!;
        var b = (await _manager.AcquireAsync(RepoDir, "flow2/b#1", "agent-b"))!;
        var c = (await _manager.AcquireAsync(RepoDir, "flow2/c#1", "agent-c"))!;
        File.WriteAllText(Path.Combine(a.WorkingDirectory, "a-only.txt"), "A\n");
        File.WriteAllText(Path.Combine(b.WorkingDirectory, "b-only.txt"), "B\n");
        File.WriteAllText(Path.Combine(a.WorkingDirectory, "plan.md"), "plan by A\n");
        File.WriteAllText(Path.Combine(c.WorkingDirectory, "plan.md"), "plan by C\n");

        Assert.Equal(WorkspaceMergeOutcome.Merged, (await _manager.CompleteAsync(a, "a", merge: true)).Outcome);
        Assert.Equal(WorkspaceMergeOutcome.Merged, (await _manager.CompleteAsync(b, "b", merge: true)).Outcome);
        var rc = await _manager.CompleteAsync(c, "c", merge: true);

        Assert.Equal(WorkspaceMergeOutcome.Conflict, rc.Outcome);
        Assert.Contains("plan.md", rc.ConflictFiles);
        Assert.True(Directory.Exists(c.RootPath));
        Assert.Equal("plan by A\n", File.ReadAllText(Path.Combine(RepoDir, "plan.md")));
        Assert.True(File.Exists(Path.Combine(RepoDir, "a-only.txt")));
        Assert.True(File.Exists(Path.Combine(RepoDir, "b-only.txt")));
    }

    [Fact]
    public async Task EachLoopRound_GetsItsOwnBranch()
    {
        InitRepo();
        // Round 1 left its branch behind (e.g. after a conflict); round 2 must still be isolated.
        var round1 = (await _manager.AcquireAsync(RepoDir, "flow3/backend#1", "backend"))!;
        var round2 = await _manager.AcquireAsync(RepoDir, "flow3/backend#2", "backend");

        Assert.NotNull(round2);
        Assert.NotEqual(round1.Branch, round2!.Branch);
        Assert.NotEqual(round1.RootPath, round2.RootPath);
    }

    [Theory]
    [InlineData("agent one", "agent-one")]
    [InlineData("../evil", "evil")]
    [InlineData("", "agent")]
    public void Slug_SanitizesNames(string input, string expected) =>
        Assert.Equal(expected, GitWorktreeManager.Slug(input));
}
