using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiCodeAgent.App.Services;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AiCodeAgent.App.ViewModels;

public partial class FileExplorerItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True for a folder the user added to the task in addition to the main working folder.</summary>
    [ObservableProperty]
    private bool _isExtraRoot;

    [ObservableProperty]
    private string _icon = "📄";

    /// <summary>Git state of this file relative to HEAD (None when clean or outside a repository).</summary>
    [ObservableProperty]
    private GitChangeKind _gitKind;

    /// <summary>For folders: true when something inside has uncommitted changes.</summary>
    [ObservableProperty]
    private bool _containsGitChanges;

    public bool HasGitChange => GitKind != GitChangeKind.None;
    public string GitBadge => GitKind.Badge();
    public string GitColor => GitKind.Color();
    public string GitTooltip => GitKind.Describe();
    public bool ShowGitFolderDot => IsDirectory && ContainsGitChanges;

    partial void OnGitKindChanged(GitChangeKind value)
    {
        OnPropertyChanged(nameof(HasGitChange));
        OnPropertyChanged(nameof(GitBadge));
        OnPropertyChanged(nameof(GitColor));
        OnPropertyChanged(nameof(GitTooltip));
    }

    partial void OnContainsGitChangesChanged(bool value) => OnPropertyChanged(nameof(ShowGitFolderDot));

    /// <summary>Raised after this folder's children were read from disk (first expand), so git decorations can be applied to them.</summary>
    public event EventHandler? ChildrenLoaded;

    /// <summary>Dummy child used so directory expanders appear before children are loaded.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>True when this item is a real file that can be opened in the editor.</summary>
    public bool CanOpen => !IsDirectory && !IsPlaceholder;

    public ObservableCollection<FileExplorerItem> Children { get; } = new();

    public bool IsInitiallyLoaded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !IsInitiallyLoaded)
        {
            _ = LoadChildrenAsync();
        }
    }

    public static FileExplorerItem CreateDirectory(string name, string fullPath)
    {
        var item = new FileExplorerItem
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = true,
            Icon = "📁"
        };
        item.Children.Add(CreatePlaceholder());
        return item;
    }

    public static FileExplorerItem CreateFile(string name, string fullPath)
    {
        return new FileExplorerItem
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = false,
            Icon = GetFileIcon(Path.GetExtension(name))
        };
    }

    public static FileExplorerItem CreatePlaceholder() => new()
    {
        Name = string.Empty,
        Icon = string.Empty,
        IsPlaceholder = true
    };

    public async Task LoadChildrenAsync()
    {
        if (!IsDirectory || IsPlaceholder || IsLoading || IsInitiallyLoaded)
            return;

        IsLoading = true;

        try
        {
            var path = FullPath;
            var items = await Task.Run(() => EnumerateChildren(path));

            Children.Clear();
            foreach (var child in items)
            {
                Children.Add(child);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading children: {ex.Message}");
        }
        finally
        {
            IsInitiallyLoaded = true;
            IsLoading = false;
        }

        ChildrenLoaded?.Invoke(this, EventArgs.Empty);
    }

    internal static List<FileExplorerItem> EnumerateChildren(string directoryPath)
    {
        var children = new List<FileExplorerItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            if (!dirInfo.Exists)
                return children;

            foreach (var dir in dirInfo.GetDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name))
            {
                children.Add(CreateDirectory(dir.Name, dir.FullName));
            }

            foreach (var file in dirInfo.GetFiles()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name))
            {
                children.Add(CreateFile(file.Name, file.FullName));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return children;
    }

    internal static string GetFileIcon(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => "🔷",
        ".xaml" or ".axaml" => "🟦",
        ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "📋",
        ".md" or ".txt" => "📝",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "🖼️",
        ".csproj" or ".sln" or ".slnx" => "📦",
        ".gitignore" or ".gitattributes" => "🔧",
        ".js" or ".ts" or ".jsx" or ".tsx" => "🟨",
        ".py" => "🐍",
        ".html" or ".css" or ".scss" => "🌐",
        ".sql" or ".db" => "🗄️",
        ".dll" or ".exe" => "⚙️",
        _ => "📄"
    };
}

public partial class FileExplorerViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private FileExplorerItem? _selectedItem;

    public ObservableCollection<FileExplorerItem> RootItems { get; } = new();

    /// <summary>Extra folders added to the task besides <see cref="RootPath"/>.</summary>
    public ObservableCollection<string> ExtraFolders { get; } = new();

    /// <summary>Short text for the explorer footer, e.g. "+2 extra folders in this task".</summary>
    public string ExtraFoldersSummary => ExtraFolders.Count switch
    {
        0 => "Main folder only. Use ➕ to add more folders to the task.",
        1 => "+1 extra folder in this task",
        var n => $"+{n} extra folders in this task"
    };

    /// <summary>Replaces the extra folder list (used at startup from saved settings).</summary>
    public void SetExtraFolders(IEnumerable<string> folders)
    {
        ExtraFolders.Clear();
        foreach (var f in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            ExtraFolders.Add(f);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
    }

    /// <summary>Adds a folder to the task. Returns a reason when it was not added, or null on success.</summary>
    public string? AddExtraFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return "That folder does not exist.";
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsSameOrInside(full, RootPath)) return "That folder is already part of the main folder.";
        if (ExtraFolders.Any(e => IsSameOrInside(full, e))) return "That folder is already in the task.";
        // A new parent folder swallows any extras it contains.
        foreach (var inner in ExtraFolders.Where(e => IsSameOrInside(e, full)).ToList()) ExtraFolders.Remove(inner);
        ExtraFolders.Add(full);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
        return null;
    }

    public bool RemoveExtraFolder(string path)
    {
        var existing = ExtraFolders.FirstOrDefault(e => string.Equals(e, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;
        ExtraFolders.Remove(existing);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
        return true;
    }

    private static bool IsSameOrInside(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private readonly GitChangeService _git;

    public FileExplorerViewModel(GitChangeService? git = null)
    {
        _git = git ?? new GitChangeService();
        RootPath = Directory.GetCurrentDirectory();
        _debounce = new Timer(_ => FlushPendingChanges(), null, Timeout.Infinite, Timeout.Infinite);
        _gitDebounce = new Timer(_ => Dispatcher.UIThread.Post(() => _ = RefreshGitStatusAsync()), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ----- Git: files changed since HEAD, shown as badges in the tree and as a Changes list -----

    private const int GitDebounceMs = 800;
    private readonly Timer _gitDebounce;
    private bool _gitBusy;
    private bool _gitRerun;
    private Dictionary<string, GitFileChange> _gitByPath = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _gitFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Show git status/diffs for folders that are git repositories. Tests turn this off.</summary>
    public bool EnableGit { get; set; } = true;

    /// <summary>Every uncommitted change in the open folders (empty when none are git repositories).</summary>
    public ObservableCollection<GitFileChange> Changes { get; } = new();

    [ObservableProperty]
    private bool _isGitRepository;

    /// <summary>E.g. "main · 3 changed files", or "main · no uncommitted changes". Empty when not a repository.</summary>
    [ObservableProperty]
    private string _gitSummary = string.Empty;

    public bool HasGitChanges => Changes.Count > 0;

    /// <summary>Header for the Changes list, e.g. "Changes (3) · main".</summary>
    public string ChangesHeader => string.IsNullOrEmpty(GitSummary) ? "Changes" : GitSummary;

    partial void OnGitSummaryChanged(string value) => OnPropertyChanged(nameof(ChangesHeader));

    public GitFileChange? FindGitChange(string fullPath)
        => _gitByPath.TryGetValue(NormalizePath(fullPath), out var change) ? change : null;

    /// <summary>The file as it was at HEAD (empty for new/untracked files), or null when it can't be read.</summary>
    public async Task<string?> GetGitBaselineAsync(GitFileChange change)
    {
        if (change.Kind is GitChangeKind.Untracked)
            return string.Empty;

        var content = await _git.GetHeadContentAsync(change.RepoRoot, change.OriginalRelativePath ?? change.RelativePath)
            .ConfigureAwait(true);
        // A file that isn't at HEAD yet (staged as new) simply has an empty baseline.
        return content ?? string.Empty;
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void QueueGit()
    {
        if (_disposed || !EnableGit) return;
        try { _gitDebounce.Change(GitDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    /// <summary>Re-reads git status for every open folder and updates the badges and the Changes list. Safe to call often: overlapping calls collapse into one re-run.</summary>
    public async Task RefreshGitStatusAsync()
    {
        if (!EnableGit || _disposed) return;
        if (_gitBusy) { _gitRerun = true; return; }

        _gitBusy = true;
        try
        {
            do
            {
                _gitRerun = false;
                await RefreshGitOnceAsync();
            } while (_gitRerun && !_disposed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Git status refresh failed: {ex.Message}");
        }
        finally
        {
            _gitBusy = false;
        }
    }

    private async Task RefreshGitOnceAsync()
    {
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(RootPath)) roots.Add(RootPath);
        roots.AddRange(ExtraFolders);
        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var statuses = await Task.WhenAll(roots.Select(r => Task.Run(() => _git.GetStatusAsync(r))));
        var repos = statuses.Where(st => st != null).Select(st => st!).ToList();

        var merged = new Dictionary<string, GitFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
            foreach (var change in repo.Changes)
                merged.TryAdd(NormalizePath(change.FullPath), change);

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in merged.Keys)
        {
            // Mark every ancestor folder (up to its project root) so collapsed folders show a dot.
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir) && folders.Add(dir))
                dir = Path.GetDirectoryName(dir);
        }

        var ordered = merged.Values.OrderBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
        _gitByPath = merged;
        _gitFolders = folders;

        if (!Changes.SequenceEqual(ordered))
        {
            Changes.Clear();
            foreach (var c in ordered) Changes.Add(c);
        }

        IsGitRepository = repos.Count > 0;
        var branch = repos.Count == 1 ? repos[0].Branch : repos.Count > 1 ? $"{repos.Count} repos" : string.Empty;
        GitSummary = repos.Count == 0
            ? string.Empty
            : ordered.Count == 0
                ? $"{branch} · no uncommitted changes"
                : $"Changes ({ordered.Count}) · {branch}";
        OnPropertyChanged(nameof(HasGitChanges));

        foreach (var root in RootItems)
            DecorateSubtree(root, includeSelf: true);
    }

    /// <summary>Applies git badges to <paramref name="node"/> (optionally) and everything loaded below it, and hooks lazy folders so their children get badges when first opened.</summary>
    private void DecorateSubtree(FileExplorerItem node, bool includeSelf)
    {
        if (includeSelf)
            Decorate(node);

        if (!node.IsDirectory)
            return;

        // Re-hook idempotently: a folder that is still unopened will decorate its children once loaded.
        node.ChildrenLoaded -= OnFolderChildrenLoaded;
        node.ChildrenLoaded += OnFolderChildrenLoaded;

        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder) continue;
            DecorateSubtree(child, includeSelf: true);
        }
    }

    private void OnFolderChildrenLoaded(object? sender, EventArgs e)
    {
        if (sender is not FileExplorerItem folder)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            DecorateSubtree(folder, includeSelf: false);
        else
            Dispatcher.UIThread.Post(() => DecorateSubtree(folder, includeSelf: false));
    }

    private void Decorate(FileExplorerItem item)
    {
        if (string.IsNullOrEmpty(item.FullPath)) return;
        var key = NormalizePath(item.FullPath);
        if (item.IsDirectory)
        {
            item.GitKind = GitChangeKind.None;
            item.ContainsGitChanges = _gitFolders.Contains(key);
        }
        else
        {
            item.GitKind = _gitByPath.TryGetValue(key, out var change) ? change.Kind : GitChangeKind.None;
            item.ContainsGitChanges = false;
        }
    }

    // ----- Live updates: new/deleted/renamed files and folders appear without a manual refresh -----

    private const int WatchDebounceMs = 250;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, byte> _pendingDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private bool _disposed;

    /// <summary>Watch the open folders on disk and update the tree when files change. Tests turn this off.</summary>
    public bool EnableWatching { get; set; } = true;

    private void RestartWatchers()
    {
        StopWatchers();
        if (!EnableWatching || _disposed)
            return;

        var roots = new List<string>();
        if (!string.IsNullOrEmpty(RootPath)) roots.Add(RootPath);
        roots.AddRange(ExtraFolders);

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    // LastWrite: edits to existing files don't change the tree but do change git status/diffs.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += (_, e) => OnWatchedChange(e.FullPath, structural: true);
                watcher.Deleted += (_, e) => OnWatchedChange(e.FullPath, structural: true);
                watcher.Changed += (_, e) => OnWatchedChange(e.FullPath, structural: false);
                watcher.Renamed += (_, e) =>
                {
                    OnWatchedChange(e.OldFullPath, structural: true);
                    OnWatchedChange(e.FullPath, structural: true);
                };
                // Buffer overflow (e.g. a huge build or checkout): fall back to re-syncing the whole open tree.
                watcher.Error += (_, _) => { Queue(root); QueueGit(); };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Cannot watch {root}: {ex.Message}");
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* already gone */ }
        }
        _watchers.Clear();
        _pendingDirs.Clear();
    }

    private void OnWatchedChange(string fullPath, bool structural)
    {
        if (IsInsideGitDirectory(fullPath))
        {
            // Commits, checkouts and staging rewrite these — that's when status changes without any file edit.
            // Everything else under .git (locks, objects, logs) is noise.
            var name = Path.GetFileName(fullPath);
            if (name is "index" or "HEAD")
                QueueGit();
            return;
        }

        if (structural)
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Queue(parent);
        }
        QueueGit();
    }

    private static bool IsInsideGitDirectory(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var alt = Path.AltDirectorySeparatorChar;
        return path.Contains($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{alt}.git{alt}", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith($"{sep}.git", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith($"{alt}.git", StringComparison.OrdinalIgnoreCase);
    }

    private void Queue(string directory)
    {
        if (_disposed) return;
        _pendingDirs[directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = 0;
        // Bursts (build output, git checkout, an agent writing many files) collapse into a single refresh.
        try { _debounce.Change(WatchDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void FlushPendingChanges()
    {
        if (_disposed) return;
        var dirs = _pendingDirs.Keys.ToList();
        foreach (var d in dirs) _pendingDirs.TryRemove(d, out _);
        if (dirs.Count == 0) return;

        Dispatcher.UIThread.Post(async () =>
        {
            foreach (var dir in dirs)
            {
                try { await SyncDirectoryAsync(dir); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Explorer sync failed for {dir}: {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Brings the already-loaded tree node for <paramref name="directory"/> in line with the disk:
    /// adds new entries (in sorted position), removes deleted ones, and leaves existing nodes untouched
    /// so expanded folders stay expanded. Folders that were never opened are skipped — they read the
    /// disk fresh when expanded. Must run on the UI thread.
    /// </summary>
    public async Task SyncDirectoryAsync(string directory)
    {
        var node = FindLoadedDirectory(directory);
        if (node == null)
            return;

        var path = node.FullPath;
        var fresh = await Task.Run(() => FileExplorerItem.EnumerateChildren(path));

        // The tree may have been rebuilt while we were reading the disk.
        node = FindLoadedDirectory(directory);
        if (node == null)
            return;

        var freshPaths = fresh.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (!child.IsPlaceholder && !freshPaths.Contains(child.FullPath))
                node.Children.RemoveAt(i);
        }

        var existing = node.Children.Select(c => c.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fresh.Where(f => !existing.Contains(f.FullPath)))
        {
            var index = 0;
            while (index < node.Children.Count && CompareEntries(node.Children[index], item) <= 0)
                index++;
            node.Children.Insert(index, item);
        }

        // New files/folders need their git badges too.
        DecorateSubtree(node, includeSelf: false);
    }

    /// <summary>Same order as the explorer's initial listing: folders first, then names.</summary>
    private static int CompareEntries(FileExplorerItem a, FileExplorerItem b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        return StringComparer.CurrentCulture.Compare(a.Name, b.Name);
    }

    private FileExplorerItem? FindLoadedDirectory(string directory)
    {
        var target = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var root in RootItems)
        {
            var found = FindLoadedDirectory(root, target);
            if (found != null)
                return found;
        }
        return null;
    }

    private static FileExplorerItem? FindLoadedDirectory(FileExplorerItem node, string target)
    {
        if (!node.IsDirectory || node.IsPlaceholder)
            return null;

        var nodePath = node.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(nodePath, target, StringComparison.OrdinalIgnoreCase))
            return node;

        if (!node.IsInitiallyLoaded ||
            !target.StartsWith(nodePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(nodePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var child in node.Children)
        {
            var found = FindLoadedDirectory(child, target);
            if (found != null)
                return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatchers();
        _debounce.Dispose();
        _gitDebounce.Dispose();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(RootPath) || !Directory.Exists(RootPath))
            return;

        IsLoading = true;
        RootItems.Clear();

        try
        {
            var rootDir = new DirectoryInfo(RootPath);
            var rootItem = FileExplorerItem.CreateDirectory(rootDir.Name, rootDir.FullName);

            var items = await Task.Run(() => FileExplorerItem.EnumerateChildren(rootDir.FullName));

            rootItem.Children.Clear();
            foreach (var child in items)
            {
                rootItem.Children.Add(child);
            }

            rootItem.IsInitiallyLoaded = true;
            RootItems.Add(rootItem);
            rootItem.IsExpanded = true;

            // Folders the user added to the task, listed after the main folder (collapsed until opened).
            foreach (var extra in ExtraFolders.ToList())
            {
                if (!Directory.Exists(extra)) continue;
                var info = new DirectoryInfo(extra);
                var item = FileExplorerItem.CreateDirectory(info.Name.Length > 0 ? info.Name : extra, info.FullName);
                item.IsExtraRoot = true;
                item.Icon = "📌";
                RootItems.Add(item);
            }
        }
        finally
        {
            IsLoading = false;
        }

        RestartWatchers();
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = LoadAsync();
    }

    [RelayCommand]
    private void SetRootPath(string path)
    {
        RootPath = path;
        _ = LoadAsync();
    }

    public List<string> GetFilesRecursive(string? directory = null, int maxDepth = 3)
    {
        var files = new List<string>();
        var dir = directory ?? RootPath;

        if (!Directory.Exists(dir)) return files;

        try
        {
            foreach (var file in Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Take(50))
            {
                files.Add(file);
            }

            if (maxDepth > 0)
            {
                foreach (var subDir in Directory.GetDirectories(dir)
                    .Where(d => !Path.GetFileName(d).StartsWith('.')))
                {
                    files.AddRange(GetFilesRecursive(subDir, maxDepth - 1));
                }
            }
        }
        catch (UnauthorizedAccessException) { }

        return files;
    }
}
