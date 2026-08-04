using AiCodeAgent.Indexing.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Indexing;

/// <summary>
/// Background service that maintains the workspace index. Performs an initial
/// full scan on start (respecting .gitignore), then applies incremental updates
/// via <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class WorkspaceIndexer : IDisposable
{
    private readonly WorkspaceIndexStore _store;
    private readonly ILogger<WorkspaceIndexer> _logger;
    private readonly string _workspaceRoot;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _watcherLock = new();
    private FileSystemWatcher? _activeWatcher;
    private bool _scanning;

    /// <summary>Raised after a full scan completes.</summary>
    public event EventHandler<IndexScanCompletedEventArgs>? ScanCompleted;

    /// <summary>Raised when the index state changes (files added/updated/removed).</summary>
    public event EventHandler<IndexChangedEventArgs>? IndexChanged;

    public string WorkspaceRoot => _workspaceRoot;
    public string WorkspaceId => _store.WorkspaceId;

    /// <summary>Whether a full scan is currently running.</summary>
    public bool IsScanning => _scanning;

    public WorkspaceIndexer(
        WorkspaceIndexStore store,
        ILogger<WorkspaceIndexer> logger,
        string workspaceRoot,
        bool enableWatcher = true)
    {
        _store = store;
        _logger = logger;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _store.Open();

        if (enableWatcher)
        {
            try
            {
                _watcher = new FileSystemWatcher(_workspaceRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime
                };
                _watcher.Created += OnWatcherCreated;
                _watcher.Changed += OnWatcherChanged;
                _watcher.Deleted += OnWatcherDeleted;
                _watcher.Renamed += OnWatcherRenamed;
                _watcher.Error += OnWatcherError;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create FileSystemWatcher for {Root}; incremental updates disabled", _workspaceRoot);
            }
        }
    }

    /// <summary>Start the background service: perform a full scan on a background thread.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => FullScanAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Perform a full scan of the workspace, replacing the file index. Skips
    /// directories that are hidden, are standard build outputs, or are matched
    /// by .gitignore rules.
    /// </summary>
    public async Task FullScanAsync(CancellationToken cancellationToken = default)
    {
        if (_scanning)
            return;
        _scanning = true;

        try
        {
            _logger.LogInformation("Starting full workspace index scan for {Root}", _workspaceRoot);

            var files = new List<IndexedFile>();
            var matcher = LoadGitignoreRules();

            await Task.Run(() => WalkTree(_workspaceRoot, files, matcher, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            await _store.ReplaceAllFilesAsync(files, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Workspace index scan complete: {Count} files indexed", files.Count);

            ScanCompleted?.Invoke(this, new IndexScanCompletedEventArgs(files.Count));
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.FullScan));

            // Start watching for file changes only after the initial scan.
            lock (_watcherLock)
            {
                if (_activeWatcher == null && _watcher != null)
                {
                    _watcher.EnableRaisingEvents = true;
                    _activeWatcher = _watcher;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Workspace index scan cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workspace index scan failed for {Root}", _workspaceRoot);
        }
        finally
        {
            _scanning = false;
        }
    }

    // ===== Incremental update handlers =====

    private void OnWatcherCreated(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (Directory.Exists(e.FullPath))
                {
                    // New directory: index its contents.
                    var dirPath = GetRelativePath(e.FullPath);
                    if (!IsIgnoredRelative(dirPath, isDirectory: true))
                    {
                        var files = new List<IndexedFile>();
                        await Task.Run(() => WalkTree(e.FullPath, files, LoadGitignoreRules(), CancellationToken.None));
                        foreach (var file in files)
                        {
                            await _store.UpsertFileAsync(file);
                        }
                        IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Added, files.Select(f => f.Path).ToList()));
                    }
                    return;
                }

                if (File.Exists(e.FullPath) && !IsIgnoredRelative(GetRelativePath(e.FullPath), isDirectory: false))
                {
                    var file = await IndexSingleFileAsync(e.FullPath);
                    if (file != null)
                    {
                        IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Added, new[] { file.Path }));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error handling file created event for {Path}", e.FullPath);
            }
        });
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(e.FullPath))
                    return;

                if (IsIgnoredRelative(GetRelativePath(e.FullPath), isDirectory: false))
                    return;

                var file = await IndexSingleFileAsync(e.FullPath, debounce: true);
                if (file != null)
                {
                    IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Updated, new[] { file.Path }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error handling file changed event for {Path}", e.FullPath);
            }
        });
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.DeleteFileAsync(e.FullPath);
                IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Removed, new[] { e.FullPath }));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error handling file deleted event for {Path}", e.FullPath);
            }
        });
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Remove the old entry and index the new one.
                await _store.DeleteFileAsync(e.OldFullPath);

                var rel = GetRelativePath(e.FullPath);
                if (File.Exists(e.FullPath) && !IsIgnoredRelative(rel, isDirectory: false))
                {
                    var file = await IndexSingleFileAsync(e.FullPath);
                    if (file != null)
                    {
                        IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Updated, new[] { file.Path }));
                        return;
                    }
                }
                else if (Directory.Exists(e.FullPath) && !IsIgnoredRelative(rel, isDirectory: true))
                {
                    var files = new List<IndexedFile>();
                    await Task.Run(() => WalkTree(e.FullPath, files, LoadGitignoreRules(), CancellationToken.None));
                    foreach (var f in files)
                    {
                        await _store.UpsertFileAsync(f);
                    }
                    IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Updated, files.Select(f => f.Path).ToList()));
                }

                IndexChanged?.Invoke(this, new IndexChangedEventArgs(IndexChangeKind.Removed, new[] { e.OldFullPath }));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error handling file renamed event from {Old} to {New}", e.OldFullPath, e.FullPath);
            }
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error for {Root}", _workspaceRoot);
        // The watcher may have lost events; rescan.
        _ = FullScanAsync();
    }

    // ===== Helpers =====

    private async Task<IndexedFile?> IndexSingleFileAsync(string path, bool debounce = false)
    {
        if (debounce)
        {
            // Give the file write to settle before hashing.
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;

                var lastWrite = fi.LastWriteTimeUtc;
                while (true)
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    fi.Refresh();
                    if (!fi.Exists) return null;
                    if (fi.LastWriteTimeUtc == lastWrite) break;
                    lastWrite = fi.LastWriteTimeUtc;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Debounce failed for {Path}", path);
            }
        }

        try
        {
            var indexedFile = new IndexedFile(
                Path: path,
                LastModified: FileHasher.GetLastModifiedUtc(path),
                Hash: FileHasher.ComputeHash(path));
            await _store.UpsertFileAsync(indexedFile);
            return indexedFile;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to index file {Path}", path);
            return null;
        }
    }

    private void WalkTree(string root, List<IndexedFile> results, GitignoreMatcher matcher, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !token.IsCancellationRequested)
        {
            var dir = pending.Pop();

            IEnumerable<string> subDirs;
            IEnumerable<string> filenames;
            try
            {
                var dirInfo = new DirectoryInfo(dir);
                subDirs = dirInfo.EnumerateDirectories()
                    .Where(d => !IsSkippableDirectory(d.Name))
                    .Select(d => d.FullName);
                filenames = dirInfo.EnumerateFiles()
                    .Where(f => !IsSkippableFile(f.Name))
                    .Select(f => f.FullName);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var subDir in subDirs)
            {
                var rel = GetRelativePath(subDir);
                if (!IsIgnoredRelative(rel, isDirectory: true, matcher))
                {
                    pending.Push(subDir);
                }
            }

            foreach (var file in filenames)
            {
                var rel = GetRelativePath(file);
                if (IsIgnoredRelative(rel, isDirectory: false, matcher))
                    continue;

                try
                {
                    results.Add(new IndexedFile(
                        Path: file,
                        LastModified: File.GetLastWriteTimeUtc(file),
                        Hash: FileHasher.ComputeHash(file)));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Skipping unreadable file {Path}", file);
                }
            }
        }
    }

    private static bool IsSkippableDirectory(string name)
    {
        if (name.StartsWith('.'))
            return true;

        return name switch
        {
            "node_modules" or "bin" or "obj" or "Debug" or "Release" or
            "__pycache__" or ".vs" or ".idea" or ".vscode" or "packages" or
            "artifacts" or ".artifacts" or "TestResults" or "coverage" => true,
            _ => false
        };
    }

    private static bool IsSkippableFile(string name)
    {
        if (name.StartsWith('.'))
            return true;

        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".dll" or ".exe" or ".pdb" or ".obj" or ".bin" or
            ".log" or ".cache" or ".suo" or ".user" or ".nupkg" or ".snupkg";
    }

    private GitignoreMatcher LoadGitignoreRules()
    {
        var matcher = new GitignoreMatcher();
        try
        {
            var gitignorePath = Path.Combine(_workspaceRoot, ".gitignore");
            if (File.Exists(gitignorePath))
            {
                matcher.LoadFromFile(gitignorePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load .gitignore for {Root}", _workspaceRoot);
        }
        return matcher;
    }

    private bool IsIgnoredRelative(string relativePath, bool isDirectory, GitignoreMatcher? matcher = null)
    {
        // Never index the .git directory itself.
        if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() == ".git")
            return true;

        if (isDirectory && IsSkippableDirectory(Path.GetFileName(relativePath.TrimEnd(Path.DirectorySeparatorChar))))
            return true;

        if (!isDirectory && IsSkippableFile(Path.GetFileName(relativePath)))
            return true;

        try
        {
            matcher ??= LoadGitignoreRules();
            return matcher.HasRules && matcher.IsIgnored(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
        }
        catch
        {
            return false;
        }
    }

    private string GetRelativePath(string fullPath) =>
        Path.GetRelativePath(_workspaceRoot, fullPath);

    public async Task CloseAsync()
    {
        lock (_watcherLock)
        {
            if (_activeWatcher != null)
            {
                _activeWatcher.EnableRaisingEvents = false;
                _activeWatcher = null;
            }
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            lock (_watcherLock)
            {
                if (_activeWatcher != null)
                {
                    _activeWatcher.EnableRaisingEvents = false;
                    _activeWatcher = null;
                }
            }
            _watcher?.Dispose();
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Kind of index change.</summary>
public enum IndexChangeKind
{
    Added,
    Updated,
    Removed,
    FullScan
}

/// <summary>Event args for index change notifications.</summary>
public class IndexChangedEventArgs : EventArgs
{
    public IndexChangeKind Kind { get; }
    public IReadOnlyList<string> Paths { get; }

    public IndexChangedEventArgs(IndexChangeKind kind, IReadOnlyList<string>? paths = null)
    {
        Kind = kind;
        Paths = paths ?? Array.Empty<string>();
    }
}

/// <summary>Event args for full-scan completions.</summary>
public class IndexScanCompletedEventArgs : EventArgs
{
    public int FileCount { get; }

    public IndexScanCompletedEventArgs(int fileCount)
    {
        FileCount = fileCount;
    }
}