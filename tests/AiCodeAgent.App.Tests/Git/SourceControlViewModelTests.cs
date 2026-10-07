using System.Diagnostics;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using Xunit;

namespace AiCodeAgent.App.Tests.Git;

/// <summary>Drives the Source Control panel against real temporary git repositories (no-ops when git isn't installed).</summary>
public class SourceControlViewModelTests : IDisposable
{
    private readonly List<string> _dirs = new();
    private readonly string _repo;
    private readonly bool _gitAvailable;

    public SourceControlViewModelTests()
    {
        _repo = NewRepo(out _gitAvailable);
        if (!_gitAvailable) return;
        Write("a.txt", "a1\n");
        Write("b.txt", "b1\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(d, true);
            }
            catch { /* best effort */ }
        }
    }

    private string NewRepo(out bool ok)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        ok = Git(dir, "init", "-q", "-b", "main").ok;
        if (ok)
        {
            Git(dir, "config", "user.email", "t@example.com");
            Git(dir, "config", "user.name", "Test");
            Git(dir, "config", "commit.gpgsign", "false");
            // Byte-exact files regardless of the machine's git config (Windows runners use autocrlf=true).
            Git(dir, "config", "core.autocrlf", "false");
        }
        return dir;
    }

    private static (bool ok, string output) Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, o);
        }
        catch { return (false, string.Empty); }
    }

    private void Write(string rel, string content) => File.WriteAllText(Path.Combine(_repo, rel), content);

    private async Task<SourceControlViewModel> OpenAsync(string? root = null, params string[] extra)
    {
        var explorer = new FileExplorerViewModel { EnableWatching = false, EnableGit = false, RootPath = root ?? _repo };
        explorer.SetExtraFolders(extra);
        var vm = new SourceControlViewModel(explorer);
        await vm.RefreshAsync();
        return vm;
    }

    private static string[] Names(IEnumerable<SourceControlEntry> entries) => entries.Select(e => e.Change.RelativePath).ToArray();

    [Fact]
    public async Task Refresh_SplitsStagedAndUnstaged_AndFileEditedAfterStagingAppearsInBoth()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        Git(_repo, "add", "a.txt");
        Write("a.txt", "a3\n");
        Write("new.txt", "n\n");

        var vm = await OpenAsync();

        Assert.True(vm.IsGitRepository);
        Assert.Equal("main", vm.Branch);
        Assert.Equal(new[] { "a.txt" }, Names(vm.Staged));
        Assert.Equal(new[] { "a.txt", "new.txt" }, Names(vm.Unstaged));
        Assert.Equal("Staged Changes (1)", vm.StagedHeader);
        Assert.Equal("Changes (2)", vm.UnstagedHeader);
        Assert.Equal(GitChangeKind.Untracked, vm.Unstaged.Single(e => e.FileName == "new.txt").Kind);
    }

    [Fact]
    public async Task StageAndUnstage_MoveRowsBetweenLists()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        var vm = await OpenAsync();
        var row = vm.Unstaged.Single();

        await vm.StageCommand.ExecuteAsync(row);
        Assert.Equal(new[] { "a.txt" }, Names(vm.Staged));
        Assert.Empty(vm.Unstaged);
        Assert.Equal("✓ Commit", vm.CommitButtonText);

        await vm.UnstageCommand.ExecuteAsync(vm.Staged.Single());
        Assert.Empty(vm.Staged);
        Assert.Equal(new[] { "a.txt" }, Names(vm.Unstaged));
        Assert.Equal("✓ Commit All", vm.CommitButtonText);
    }

    [Fact]
    public async Task StageAll_UnstageAll()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        Write("new.txt", "n\n");
        var vm = await OpenAsync();

        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Staged.Count);
        Assert.Empty(vm.Unstaged);

        await vm.UnstageAllCommand.ExecuteAsync(null);
        Assert.Empty(vm.Staged);
        Assert.Equal(2, vm.Unstaged.Count);
    }

    [Fact]
    public async Task Discard_NeedsConfirmation_ThenRestoresTrackedAndDeletesUntracked()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "changed\n");
        Write("new.txt", "n\n");
        var vm = await OpenAsync();
        var tracked = vm.Unstaged.Single(e => e.FileName == "a.txt");
        var untracked = vm.Unstaged.Single(e => e.FileName == "new.txt");

        // First click only asks.
        vm.DiscardCommand.Execute(tracked);
        Assert.True(tracked.ConfirmingDiscard);
        Assert.True(tracked.ShowDiscardConfirm);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));

        // Cancelling keeps everything.
        vm.CancelDiscardCommand.Execute(tracked);
        Assert.False(tracked.ConfirmingDiscard);
        await vm.ConfirmDiscardCommand.ExecuteAsync(tracked); // not confirming => ignored
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));

        vm.DiscardCommand.Execute(tracked);
        await vm.ConfirmDiscardCommand.ExecuteAsync(tracked);
        Assert.Equal("a1\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));

        untracked = vm.Unstaged.Single(e => e.FileName == "new.txt");
        vm.DiscardCommand.Execute(untracked);
        await vm.ConfirmDiscardCommand.ExecuteAsync(untracked);
        Assert.False(File.Exists(Path.Combine(_repo, "new.txt")));
        Assert.True(vm.IsClean);
    }

    [Fact]
    public async Task Conflicted_FilesCannotBeDiscarded()
    {
        if (!_gitAvailable) return;
        Git(_repo, "checkout", "-q", "-b", "other");
        Write("a.txt", "other\n"); Git(_repo, "commit", "-qam", "o");
        Git(_repo, "checkout", "-q", "main");
        Write("a.txt", "main\n"); Git(_repo, "commit", "-qam", "m");
        Git(_repo, "merge", "other");

        var vm = await OpenAsync();

        var row = vm.Unstaged.Single();
        Assert.Equal(GitChangeKind.Conflicted, row.Kind);
        Assert.False(row.CanDiscard);
        vm.DiscardCommand.Execute(row);
        Assert.False(row.ConfirmingDiscard);
    }

    [Fact]
    public async Task Commit_Staged_CreatesCommit_ClearsMessage_AndShowsHash()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        Write("b.txt", "b2\n");
        Git(_repo, "add", "a.txt");
        var vm = await OpenAsync();

        Assert.False(vm.CommitCommand.CanExecute(null));   // no message yet
        vm.CommitMessage = "Update a\n\nWith a body";
        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.CommitMessage);
        Assert.StartsWith("Committed ", vm.StatusMessage);
        Assert.Contains("Update a", vm.StatusMessage);
        Assert.Equal("Update a\n\nWith a body", Git(_repo, "log", "-1", "--format=%B").output.Trim());
        Assert.Empty(vm.Staged);
        Assert.Equal(new[] { "b.txt" }, Names(vm.Unstaged)); // only the staged file was committed
    }

    [Fact]
    public async Task CommitAll_WhenNothingStaged_StagesEverythingIncludingNewFiles()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        Write("new.txt", "n\n");
        var vm = await OpenAsync();
        Assert.Equal("✓ Commit All", vm.CommitButtonText);

        vm.CommitMessage = "Everything";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.IsClean);
        Assert.Equal("a.txt\nnew.txt", Git(_repo, "show", "--name-only", "--format=", "HEAD").output.Trim().Replace("\r", ""));
    }

    [Fact]
    public async Task Commit_Failure_ShowsGitsMessage_AndKeepsTheTypedMessage()
    {
        if (!_gitAvailable) return;
        var hook = Path.Combine(_repo, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'blocked by hook' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Write("a.txt", "a2\n");
        var vm = await OpenAsync();

        vm.CommitMessage = "will fail";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("blocked by hook", vm.ErrorMessage);
        Assert.Equal("will fail", vm.CommitMessage);       // nothing is lost
        Assert.False(vm.IsBusy);
        Assert.Equal("init", Git(_repo, "log", "-1", "--format=%s").output.Trim());
    }

    [Fact]
    public async Task PrepareDiff_StagedIsReadOnlyHeadVsIndex_UnstagedIsEditableIndexVsDisk()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        Git(_repo, "add", "a.txt");
        Write("a.txt", "a3\n");
        Write("new.txt", "n\n");
        var vm = await OpenAsync();

        var staged = await vm.PrepareDiffAsync(vm.Staged.Single());
        Assert.Equal("a1\n", staged.Baseline);
        Assert.Equal("a2\n", staged.Current);              // the index, not the file on disk
        Assert.Equal("HEAD", staged.BaselineLabel);

        var unstaged = await vm.PrepareDiffAsync(vm.Unstaged.Single(e => e.FileName == "a.txt"));
        Assert.Equal("a2\n", unstaged.Baseline);           // last staged version
        Assert.Null(unstaged.Current);                     // right side = the file on disk, editable

        var fresh = await vm.PrepareDiffAsync(vm.Unstaged.Single(e => e.FileName == "new.txt"));
        Assert.Equal(string.Empty, fresh.Baseline);
        Assert.Contains("untracked", fresh.Notice);
    }

    [Fact]
    public async Task PrepareDiff_BinaryFile_ReportsInsteadOfDiffing()
    {
        if (!_gitAvailable) return;
        File.WriteAllBytes(Path.Combine(_repo, "image.bin"), new byte[] { 1, 0, 2, 0, 3 });
        var vm = await OpenAsync();

        var diff = await vm.PrepareDiffAsync(vm.Unstaged.Single());

        Assert.Contains("binary", diff.Error);
    }

    [Fact]
    public async Task DiffRequested_IsRaised_WhenARowIsClicked()
    {
        if (!_gitAvailable) return;
        Write("a.txt", "a2\n");
        var vm = await OpenAsync();
        SourceControlEntry? requested = null;
        vm.DiffRequested += e => requested = e;

        vm.OpenDiffCommand.Execute(vm.Unstaged.Single());

        Assert.Equal("a.txt", requested?.Change.RelativePath);
    }

    [Fact]
    public async Task SeveralRepositories_CanBePicked()
    {
        if (!_gitAvailable) return;
        var other = NewRepo(out _);
        File.WriteAllText(Path.Combine(other, "x.txt"), "x\n");
        Git(other, "add", "-A"); Git(other, "commit", "-q", "-m", "x");
        File.WriteAllText(Path.Combine(other, "x.txt"), "x2\n");
        Write("a.txt", "a2\n");

        var vm = await OpenAsync(_repo, other);

        Assert.True(vm.HasMultipleRepos);
        Assert.Equal(2, vm.Repos.Count);
        Assert.Equal(new[] { "a.txt" }, Names(vm.Unstaged));

        vm.SelectedRepo = vm.Repos.Single(r => r.Path.Equals(Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase));
        await vm.RefreshAsync();
        Assert.Equal(new[] { "x.txt" }, Names(vm.Unstaged));
    }

    [Fact]
    public async Task FolderThatIsNotARepository_ShowsHelpfulState()
    {
        var plain = Path.Combine(Path.GetTempPath(), "plain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plain);
        _dirs.Add(plain);

        var vm = await OpenAsync(plain);

        Assert.False(vm.IsGitRepository);
        Assert.True(vm.NoRepository);
        Assert.False(vm.CommitCommand.CanExecute(null));
    }
}
