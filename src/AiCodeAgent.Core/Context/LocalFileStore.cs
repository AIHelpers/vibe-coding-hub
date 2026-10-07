using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Context;

/// <summary>Shared helpers for the Markdown-backed skill and character registries.</summary>
public static partial class LocalFileStore
{
    /// <summary>Max length of a skill/character id.</summary>
    public const int MaxNameLength = 64;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabRegex();

    /// <summary>True for lowercase kebab-case names such as <c>release-notes</c>.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && KebabRegex().IsMatch(name);

    /// <summary>Error message for an invalid name, or null when valid.</summary>
    public static string? ValidateName(string? name, string what = "Name")
    {
        if (string.IsNullOrWhiteSpace(name)) return $"{what} is required.";
        if (name.Length > MaxNameLength) return $"{what} '{name}' is longer than {MaxNameLength} characters.";
        if (!IsValidName(name)) return $"{what} '{name}' must be lowercase kebab-case (letters, digits and single dashes, e.g. 'release-notes').";
        return null;
    }

    /// <summary>Best-effort conversion of free text to a kebab-case name.</summary>
    public static string ToKebab(string text)
    {
        var s = Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length > MaxNameLength ? s[..MaxNameLength].TrimEnd('-') : s;
    }

    /// <summary>
    /// Write a file atomically: write a temp file next to it, then move it over
    /// the target, so a crash or a concurrent reader never sees a half-written file.
    /// </summary>
    public static async Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir ?? ".", "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await File.WriteAllTextAsync(tmp, content, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Approximate token count (chars / 4).</summary>
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}

/// <summary>
/// Watches one or more directories and raises a single debounced callback after
/// a burst of file changes (editors often write a file several times per save).
/// Temp files written by <see cref="LocalFileStore.WriteAllTextAtomicAsync"/> are ignored.
/// </summary>
public sealed class DebouncedDirectoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Action _onChanged;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public DebouncedDirectoryWatcher(IEnumerable<string> directories, Action onChanged, TimeSpan? delay = null)
    {
        _onChanged = onChanged;
        _delay = delay ?? TimeSpan.FromMilliseconds(300);
        foreach (var dir in directories.Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                w.Changed += OnEvent;
                w.Created += OnEvent;
                w.Deleted += OnEvent;
                w.Renamed += OnEvent;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch
            {
                // Watching is a convenience; registries still work with manual Refresh().
            }
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate) { if (_disposed) return; }
        try { _onChanged(); } catch { /* never crash the watcher thread */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
        }
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
