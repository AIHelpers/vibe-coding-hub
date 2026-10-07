using System.Diagnostics;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AiCodeAgent.App.Tests.Git;

public class GitChangeParsingTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"));

    private static string Z(params string[] entries) => string.Join('\0', entries) + '\0';

    [Fact]
    public void Parses_BranchAndEveryKind()
    {
        var status = GitChangeService.ParsePorcelain(Root, Z(
            "## main...origin/main [ahead 1]",
            " M src/Changed.cs",
            "M  src/Staged.cs",
            "A  src/New.cs",
            "AM src/NewThenEdited.cs",
            " D src/Gone.cs",
            "D  src/Removed.cs",
            "?? notes/todo.txt",
            "UU conflict.cs"));

        Assert.Equal("main", status.Branch);
        var byPath = status.Changes.ToDictionary(c => c.RelativePath);
        Assert.Equal(GitChangeKind.Modified, byPath["src/Changed.cs"].Kind);
        Assert.False(byPath["src/Changed.cs"].IsStaged);
        Assert.Equal(GitChangeKind.Modified, byPath["src/Staged.cs"].Kind);
        Assert.True(byPath["src/Staged.cs"].IsStaged);
        Assert.Equal(GitChangeKind.Added, byPath["src/New.cs"].Kind);
        Assert.Equal(GitChangeKind.Added, byPath["src/NewThenEdited.cs"].Kind);
        Assert.Equal(GitChangeKind.Deleted, byPath["src/Gone.cs"].Kind);
        Assert.Equal(GitChangeKind.Deleted, byPath["src/Removed.cs"].Kind);
        Assert.Equal(GitChangeKind.Untracked, byPath["notes/todo.txt"].Kind);
        Assert.Equal(GitChangeKind.Conflicted, byPath["conflict.cs"].Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "src", "Changed.cs")), byPath["src/Changed.cs"].FullPath);
    }

    [Fact]
    public void Rename_ConsumesOriginalPathEntry()
    {
        var status = GitChangeService.ParsePorcelain(Root, Z(
            "## dev",
            "R  new name.cs", "old name.cs",
            " M after.cs"));

        Assert.Equal(2, status.Changes.Count);
        var rename = status.Changes[0];
        Assert.Equal(GitChangeKind.Renamed, rename.Kind);
        Assert.Equal("new name.cs", rename.RelativePath);
        Assert.Equal("old name.cs", rename.OriginalRelativePath);
        Assert.Equal("after.cs", status.Changes[1].RelativePath);
    }

    [Theory]
    [InlineData("## No commits yet on main", "main")]
    [InlineData("## HEAD (no branch)", "HEAD (no branch)")]
    [InlineData("## feature/x", "feature/x")]
    public void Parses_UnusualBranchHeaders(string header, string expected)
        => Assert.Equal(expected, GitChangeService.ParsePorcelain(Root, Z(header)).Branch);

    [Fact]
    public void CleanTree_HasNoChanges()
        => Assert.Empty(GitChangeService.ParsePorcelain(Root, Z("## main")).Changes);
}

/// <summary>End-to-end against a real temporary git repository (skipped silently when git isn't installed).</summary>
public class GitRepoIntegrationTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "gitrepo-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly bool _gitAvailable;

    public GitRepoIntegrationTests()
    {
        Directory.CreateDirectory(_repo);
        _gitAvailable = Git("init", "-q", "-b", "main").ok;
        if (!_gitAvailable) return;

        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
        // Byte-exact files regardless of the machine's git config (Windows runners use autocrlf=true).
        Git("config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "Keep.cs"), "keep\n");
        File.WriteAllText(Path.Combine(_repo, "src", "Edit.cs"), "line1\nline2\n");
        File.WriteAllText(Path.Combine(_repo, "src", "Remove.cs"), "bye\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "ignored.log\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, true);
        }
        catch { /* best effort */ }
    }

    private (bool ok, string output) Git(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, o);
        }
        catch { return (false, string.Empty); }
    }

    private void MakeChanges()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "Edit.cs"), "line1\nCHANGED\n");
        File.Delete(Path.Combine(_repo, "src", "Remove.cs"));
        File.WriteAllText(Path.Combine(_repo, "src", "Brand New.cs"), "new\n");
        File.WriteAllText(Path.Combine(_repo, "ignored.log"), "noise\n");
    }

    [Fact]
    public async Task Status_ReportsEditedDeletedUntracked_AndHonoursGitignore()
    {
        if (!_gitAvailable) return;
        MakeChanges();

        var status = await new GitChangeService().GetStatusAsync(_repo);

        Assert.NotNull(status);
        Assert.Equal("main", status!.Branch);
        var kinds = status.Changes.ToDictionary(c => c.RelativePath, c => c.Kind);
        Assert.Equal(GitChangeKind.Modified, kinds["src/Edit.cs"]);
        Assert.Equal(GitChangeKind.Deleted, kinds["src/Remove.cs"]);
        Assert.Equal(GitChangeKind.Untracked, kinds["src/Brand New.cs"]);
        Assert.DoesNotContain("ignored.log", kinds.Keys);
        Assert.DoesNotContain("src/Keep.cs", kinds.Keys);
    }

    [Fact]
    public async Task Status_OfSubfolder_OnlyListsThatFolder()
    {
        if (!_gitAvailable) return;
        File.WriteAllText(Path.Combine(_repo, "src", "Edit.cs"), "x\n");
        File.WriteAllText(Path.Combine(_repo, "top.txt"), "y\n");

        var status = await new GitChangeService().GetStatusAsync(Path.Combine(_repo, "src"));

        Assert.Equal(new[] { "src/Edit.cs" }, status!.Changes.Select(c => c.RelativePath));
    }

    [Fact]
    public async Task RepoRoot_KeepsCallersSpelling_ThroughSymlinks()
    {
        // macOS temp lives under /var -> /private/var; git prints the resolved path, which must not leak
        // into the explorer (its paths would stop matching and badges would disappear).
        if (!_gitAvailable || OperatingSystem.IsWindows()) return;
        var link = _repo + "-link";
        Directory.CreateSymbolicLink(link, _repo);
        try
        {
            var svc = new GitChangeService();
            Assert.Equal(link, await svc.FindRepoRootAsync(link));
            Assert.Equal(link, await svc.FindRepoRootAsync(Path.Combine(link, "src")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("src/", 1)]
    [InlineData("src/deep/", 2)]
    public void RootInCallersSpelling_GoesUpByPrefixDepth(string prefix, int depth)
    {
        var root = Path.Combine(Path.GetTempPath(), "r");
        var dir = depth switch { 0 => root, 1 => Path.Combine(root, "src"), _ => Path.Combine(root, "src", "deep") };
        Assert.Equal(root, GitChangeService.RootInCallersSpelling(dir, prefix));
    }

    [Fact]
    public async Task NotARepository_ReturnsNull()
    {
        if (!_gitAvailable) return;
        var plain = Path.Combine(Path.GetTempPath(), "plain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plain);
        try { Assert.Null(await new GitChangeService().GetStatusAsync(plain)); }
        finally { Directory.Delete(plain, true); }
    }

    [Fact]
    public async Task HeadContent_IsTheCommittedVersion()
    {
        if (!_gitAvailable) return;
        MakeChanges();
        var svc = new GitChangeService();

        Assert.Equal("line1\nline2\n", await svc.GetHeadContentAsync(_repo, "src/Edit.cs"));
        Assert.Null(await svc.GetHeadContentAsync(_repo, "src/Brand New.cs"));
    }

    [AvaloniaFact]
    public async Task Explorer_ShowsBadgesChangesListAndBaseline()
    {
        if (!_gitAvailable) return;
        MakeChanges();
        var vm = new FileExplorerViewModel { EnableWatching = false, RootPath = _repo };

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.IsGitRepository);
        Assert.True(vm.HasGitChanges);
        Assert.Equal("Changes (3) · main", vm.GitSummary);

        var src = vm.RootItems[0].Children.First(c => c.Name == "src");
        Assert.True(src.ContainsGitChanges);          // folder dot
        src.IsExpanded = true;                         // lazy-load, badges must follow
        // Up to 10 s: on a busy CI machine loading the folder and running git can take a while.
        for (var i = 0; i < 400 && !src.IsInitiallyLoaded; i++) await Task.Delay(25);
        for (var i = 0; i < 400 && !src.Children.Any(c => c.HasGitChange); i++)
            await Task.Delay(25);

        var edit = src.Children.First(c => c.Name == "Edit.cs");
        Assert.Equal(GitChangeKind.Modified, edit.GitKind);
        Assert.Equal("M", edit.GitBadge);
        Assert.Equal(GitChangeKind.None, src.Children.First(c => c.Name == "Keep.cs").GitKind);

        var change = vm.FindGitChange(edit.FullPath)!;
        Assert.Equal("line1\nline2\n", await vm.GetGitBaselineAsync(change));

        var untracked = vm.FindGitChange(Path.Combine(_repo, "src", "Brand New.cs"))!;
        Assert.Equal(string.Empty, await vm.GetGitBaselineAsync(untracked));

        // Deleted files are gone from the tree but still listed so their diff can be opened.
        var deleted = vm.Changes.Single(c => c.Kind == GitChangeKind.Deleted);
        Assert.Equal("bye\n", await vm.GetGitBaselineAsync(deleted));
        vm.Dispose();
    }

    [Fact]
    public async Task Explorer_Commit_ClearsChanges()
    {
        if (!_gitAvailable) return;
        MakeChanges();
        var vm = new FileExplorerViewModel { EnableWatching = false, RootPath = _repo };
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.HasGitChanges);

        Git("add", "-A");
        Git("commit", "-q", "-m", "second");
        await vm.RefreshGitStatusAsync();

        Assert.False(vm.HasGitChanges);
        Assert.Equal("main · no uncommitted changes", vm.GitSummary);
        vm.Dispose();
    }
}
