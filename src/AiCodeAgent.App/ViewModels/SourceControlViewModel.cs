using AiCodeAgent.App.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AiCodeAgent.App.ViewModels;

/// <summary>A git repository found among the open project folders.</summary>
public sealed record RepoOption(string Path)
{
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/', '\\');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }
}

/// <summary>One row in the Staged Changes or Changes list.</summary>
public partial class SourceControlEntry : ObservableObject
{
    public SourceControlEntry(GitFileChange change, bool isStaged)
    {
        Change = change;
        IsStaged = isStaged;
    }

    public GitFileChange Change { get; }

    /// <summary>True for the Staged Changes list, false for the working-tree Changes list.</summary>
    public bool IsStaged { get; }

    public bool IsUnstaged => !IsStaged;

    /// <summary>What happened to this file in this list: index vs HEAD for staged rows, working tree vs index for the rest.</summary>
    public GitChangeKind Kind => IsStaged ? Change.IndexKind : Change.WorkTreeKind;

    public string Badge => Kind.Badge();
    public string Color => Kind.Color();
    public string Description => Kind.Describe();
    public string FileName => Path.GetFileName(Change.RelativePath);

    public string Directory
    {
        get
        {
            var dir = Path.GetDirectoryName(Change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(dir) ? string.Empty : dir.Replace('\\', '/');
        }
    }

    public string ToolTip => $"{Change.RelativePath} — {Description}";

    /// <summary>Discarding is offered for working-tree changes except merge conflicts.</summary>
    public bool CanDiscard => IsUnstaged && Kind != GitChangeKind.Conflicted;

    /// <summary>Paths to hand to git when (un)staging this row — a rename needs both its names.</summary>
    internal IReadOnlyList<string> GitPaths =>
        Change.OriginalRelativePath != null && Kind == GitChangeKind.Renamed
            ? new[] { Change.RelativePath, Change.OriginalRelativePath }
            : new[] { Change.RelativePath };

    /// <summary>First click on discard asks "sure?" in place; a second click does it.</summary>
    [ObservableProperty]
    private bool _confirmingDiscard;

    public bool ShowDiscardButton => CanDiscard && !ConfirmingDiscard;
    public bool ShowDiscardConfirm => CanDiscard && ConfirmingDiscard;

    partial void OnConfirmingDiscardChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDiscardButton));
        OnPropertyChanged(nameof(ShowDiscardConfirm));
    }
}

/// <summary>Everything the host needs to show one file's diff.</summary>
/// <param name="Current">Right-hand content; null means "the file on disk, editable".</param>
public sealed record SourceControlDiff(
    string FilePath,
    string Baseline,
    string? Current,
    string BaselineLabel,
    string CurrentLabel,
    string? Notice,
    string? Error);

/// <summary>
/// The Source Control panel (think VS Code's): staged and unstaged changes of a git repository,
/// stage / unstage / discard per file, a commit message box and a Commit button, and a diff for
/// every row. Everything that writes to the repository happens only on an explicit click.
/// </summary>
public partial class SourceControlViewModel : ObservableObject, IDisposable
{
    private readonly GitChangeService _git;
    private readonly FileExplorerViewModel _explorer;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _autoRefreshQueued;
    private bool _updatingRepos;
    private bool _disposed;

    public SourceControlViewModel(FileExplorerViewModel explorer, GitChangeService? git = null)
    {
        _explorer = explorer;
        _git = git ?? new GitChangeService();
        // The explorer already watches the disk; piggy-back on it so an open panel follows edits made by the agent, a terminal or another tool.
        _explorer.Changes.CollectionChanged += OnExplorerChangesChanged;
    }

    /// <summary>Show a file's diff — the host (MainViewModel) owns the diff viewer.</summary>
    public event Action<SourceControlEntry>? DiffRequested;

    /// <summary>Open a file in the editor.</summary>
    public event Action<string>? OpenFileRequested;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isGitRepository;

    [ObservableProperty]
    private string _branch = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [ObservableProperty]
    private string _commitMessage = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Last success message, e.g. "Committed 4f2a9c1".</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Last git error, shown in red until the next action.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private RepoOption? _selectedRepo;

    public ObservableCollection<RepoOption> Repos { get; } = new();
    public ObservableCollection<SourceControlEntry> Staged { get; } = new();
    public ObservableCollection<SourceControlEntry> Unstaged { get; } = new();

    public bool HasMultipleRepos => Repos.Count > 1;
    public bool HasStaged => Staged.Count > 0;
    public bool HasUnstaged => Unstaged.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public bool NoRepository => !IsGitRepository;
    public bool IsClean => IsGitRepository && Staged.Count == 0 && Unstaged.Count == 0;
    public string StagedHeader => $"Staged Changes ({Staged.Count})";
    public string UnstagedHeader => $"Changes ({Unstaged.Count})";

    /// <summary>With nothing staged the button commits everything (like VS Code's smart commit).</summary>
    public string CommitButtonText => Staged.Count > 0 ? "✓ Commit" : "✓ Commit All";

    public string CommitButtonTip => Staged.Count > 0
        ? "Commit the staged changes (Ctrl+Enter in the message box)"
        : "Nothing is staged: stages ALL changes, including new files, and commits them (Ctrl+Enter)";

    public bool CanCommit => !IsBusy && !string.IsNullOrWhiteSpace(CommitMessage) && (Staged.Count > 0 || Unstaged.Count > 0) && SelectedRepo != null;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsGitRepositoryChanged(bool value)
    {
        OnPropertyChanged(nameof(NoRepository));
        OnPropertyChanged(nameof(IsClean));
    }

    partial void OnSelectedRepoChanged(RepoOption? value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        if (!_updatingRepos && value != null)
            _ = RefreshAsync();
    }

    // ----- show / hide -----

    [RelayCommand]
    private void Toggle()
    {
        if (IsVisible) Close(); else Open();
    }

    public void Open()
    {
        IsVisible = true;
        ErrorMessage = string.Empty;
        _ = RefreshAsync();
    }

    public void Close() => IsVisible = false;

    private void OnExplorerChangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsVisible || _autoRefreshQueued || _disposed)
            return;

        _autoRefreshQueued = true;
        // Posted so a burst of add/remove events from one explorer refresh becomes a single panel refresh.
        Dispatcher.UIThread.Post(() =>
        {
            _autoRefreshQueued = false;
            if (IsVisible && !IsBusy && !_disposed)
                _ = RefreshAsync();
        });
    }

    // ----- reading state -----

    /// <summary>
    /// Re-read repositories and status. Calls made while a refresh is running are coalesced
    /// into one more pass, and the returned task completes only after that pass, so callers
    /// that await it always see up-to-date lists.
    /// </summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshing)
        {
            _refreshAgain = true;
            return _currentRefresh;
        }

        _refreshing = true;
        _currentRefresh = RunRefreshLoopAsync();
        return _currentRefresh;
    }

    private Task _currentRefresh = Task.CompletedTask;

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshOnceAsync();
            } while (_refreshAgain && !_disposed);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read git status: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(_explorer.RootPath)) roots.Add(_explorer.RootPath);
        roots.AddRange(_explorer.ExtraFolders);
        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var found = await Task.WhenAll(roots.Select(r => _git.FindRepoRootAsync(r)));
        var repos = found.Where(r => r != null).Select(r => new RepoOption(r!)).Distinct().ToList();

        _updatingRepos = true;
        try
        {
            if (!Repos.SequenceEqual(repos))
            {
                Repos.Clear();
                foreach (var r in repos) Repos.Add(r);
            }
            SelectedRepo = repos.FirstOrDefault(r => SelectedRepo != null && string.Equals(r.Path, SelectedRepo.Path, StringComparison.OrdinalIgnoreCase))
                           ?? repos.FirstOrDefault();
        }
        finally
        {
            _updatingRepos = false;
        }

        OnPropertyChanged(nameof(HasMultipleRepos));
        IsGitRepository = repos.Count > 0;

        GitRepoStatus? status = null;
        if (SelectedRepo != null)
            status = await _git.GetStatusAsync(SelectedRepo.Path);

        Branch = status?.Branch ?? string.Empty;
        var changes = status?.Changes ?? Array.Empty<GitFileChange>();
        Replace(Staged, changes.Where(c => c.HasStagedPart).OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SourceControlEntry(c, isStaged: true)));
        Replace(Unstaged, changes.Where(c => c.HasUnstagedPart).OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SourceControlEntry(c, isStaged: false)));

        RaiseListProperties();
    }

    /// <summary>Swap in new rows only when something actually changed, so scroll position and an open "discard?" prompt survive no-op refreshes.</summary>
    private static void Replace(ObservableCollection<SourceControlEntry> target, IEnumerable<SourceControlEntry> fresh)
    {
        var list = fresh.ToList();
        if (target.Count == list.Count && target.Zip(list).All(p => p.First.Change == p.Second.Change))
            return;

        target.Clear();
        foreach (var e in list) target.Add(e);
    }

    private void RaiseListProperties()
    {
        OnPropertyChanged(nameof(HasStaged));
        OnPropertyChanged(nameof(HasUnstaged));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(StagedHeader));
        OnPropertyChanged(nameof(UnstagedHeader));
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CommitButtonTip));
        CommitCommand.NotifyCanExecuteChanged();
    }

    // ----- actions -----

    /// <summary>Runs one git write, shows its outcome, then re-reads the repository and refreshes the Explorer badges.</summary>
    private async Task<bool> RunAsync(Func<string, Task<GitOpResult>> operation, string? successMessage = null)
    {
        if (IsBusy || SelectedRepo == null)
            return false;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        try
        {
            var result = await operation(SelectedRepo.Path);
            if (result.Success)
            {
                if (successMessage != null) StatusMessage = successMessage;
            }
            else
            {
                ErrorMessage = Truncate(result.Output);
            }

            await RefreshAsync();
            _ = _explorer.RefreshGitStatusAsync();
            return result.Success;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Truncate(string text) => text.Length <= 800 ? text : text[..800] + "…";

    [RelayCommand]
    private Task Stage(SourceControlEntry? entry)
        => entry == null ? Task.CompletedTask : RunAsync(repo => _git.StageAsync(repo, entry.GitPaths));

    [RelayCommand]
    private Task Unstage(SourceControlEntry? entry)
        => entry == null ? Task.CompletedTask : RunAsync(repo => _git.UnstageAsync(repo, entry.GitPaths));

    [RelayCommand]
    private Task StageAll() => RunAsync(repo => _git.StageAllAsync(repo));

    [RelayCommand]
    private Task UnstageAll() => RunAsync(repo => _git.UnstageAllAsync(repo));

    /// <summary>Step one of discarding: ask for confirmation in the row itself.</summary>
    [RelayCommand]
    private void Discard(SourceControlEntry? entry)
    {
        if (entry is not { CanDiscard: true }) return;
        foreach (var other in Unstaged) other.ConfirmingDiscard = false;
        entry.ConfirmingDiscard = true;
    }

    [RelayCommand]
    private void CancelDiscard(SourceControlEntry? entry)
    {
        if (entry != null) entry.ConfirmingDiscard = false;
    }

    /// <summary>Step two: really throw the working-tree changes away (tracked files are restored, new files deleted).</summary>
    [RelayCommand]
    private async Task ConfirmDiscard(SourceControlEntry? entry)
    {
        if (entry is not { CanDiscard: true, ConfirmingDiscard: true }) return;
        var untracked = entry.Kind == GitChangeKind.Untracked;
        var name = entry.FileName;
        entry.ConfirmingDiscard = false;
        await RunAsync(repo => _git.DiscardAsync(repo, new[] { entry.Change.RelativePath }, untracked),
            untracked ? $"Deleted {name}" : $"Discarded changes to {name}");
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        var message = CommitMessage.Trim();
        if (message.Length == 0) return;

        var stageEverything = Staged.Count == 0;
        string? hash = null;

        var ok = await RunAsync(async repo =>
        {
            if (stageEverything)
            {
                var staged = await _git.StageAllAsync(repo);
                if (!staged.Success) return staged;
            }

            var committed = await _git.CommitAsync(repo, message);
            if (committed.Success) hash = committed.Output;
            return committed;
        });

        if (ok)
        {
            CommitMessage = string.Empty;
            var subject = message.Split('\n')[0].Trim();
            StatusMessage = string.IsNullOrEmpty(hash) ? $"Committed: {subject}" : $"Committed {hash}: {subject}";
        }
    }

    [RelayCommand]
    private void OpenDiff(SourceControlEntry? entry)
    {
        if (entry != null) DiffRequested?.Invoke(entry);
    }

    [RelayCommand]
    private void OpenFile(SourceControlEntry? entry)
    {
        if (entry == null) return;
        if (entry.Kind == GitChangeKind.Deleted)
        {
            StatusMessage = $"{entry.FileName} was deleted — nothing to open";
            return;
        }
        OpenFileRequested?.Invoke(entry.Change.FullPath);
    }

    // ----- diff content -----

    /// <summary>
    /// Works out what to compare for <paramref name="entry"/>. Staged rows compare HEAD with the index
    /// (read-only); working-tree rows compare the index with the file on disk (editable). New files
    /// compare against an empty baseline.
    /// </summary>
    public async Task<SourceControlDiff> PrepareDiffAsync(SourceControlEntry entry)
    {
        var change = entry.Change;
        var path = change.FullPath;

        try
        {
            if (entry.IsStaged)
            {
                var head = await _git.GetHeadContentAsync(change.RepoRoot, change.OriginalRelativePath ?? change.RelativePath) ?? string.Empty;
                var index = entry.Kind == GitChangeKind.Deleted
                    ? string.Empty
                    : await _git.GetIndexContentAsync(change.RepoRoot, change.RelativePath) ?? string.Empty;

                if (head.Contains('\0') || index.Contains('\0'))
                    return Binary(path);

                var notice = entry.Kind switch
                {
                    GitChangeKind.Added => "New file (staged)",
                    GitChangeKind.Deleted => "Deleted (staged)",
                    GitChangeKind.Renamed => $"Renamed from {change.OriginalRelativePath}",
                    _ => null
                };
                return new SourceControlDiff(path, head, index, "HEAD", "Staged (index)", notice, null);
            }

            string baseline;
            string label;
            string? note = null;
            switch (entry.Kind)
            {
                case GitChangeKind.Untracked:
                    baseline = string.Empty;
                    label = "Empty (new file)";
                    note = "New file (untracked)";
                    break;
                case GitChangeKind.Conflicted:
                    baseline = string.Empty;
                    label = "Empty";
                    note = "Merge conflict — resolve the <<<<<<< markers, then stage the file";
                    break;
                default:
                    baseline = await _git.GetIndexContentAsync(change.RepoRoot, change.RelativePath) ?? string.Empty;
                    label = "Index";
                    if (entry.Kind == GitChangeKind.Deleted) note = "Deleted in working tree";
                    break;
            }

            if (baseline.Contains('\0') || LooksBinary(path))
                return Binary(path);

            return new SourceControlDiff(path, baseline, null, label, "Working tree (editable)", note, null);
        }
        catch (Exception ex)
        {
            return new SourceControlDiff(path, string.Empty, null, string.Empty, string.Empty, null, $"Could not build the diff: {ex.Message}");
        }
    }

    private static SourceControlDiff Binary(string path)
        => new(path, string.Empty, null, string.Empty, string.Empty, null, $"{Path.GetFileName(path)} is a binary file — no text diff to show");

    private static bool LooksBinary(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var buffer = new byte[8000];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorer.Changes.CollectionChanged -= OnExplorerChangesChanged;
    }
}
