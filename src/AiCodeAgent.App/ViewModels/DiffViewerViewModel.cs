using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Powers a standalone "diff viewer" panel: given a file and a baseline
/// version of its content (a checkpoint, a git ref, or any other snapshot),
/// shows the two sides and the parsed hunks between them, and lets the user
/// edit the "current" side directly in the view, save it to disk, or revert
/// a single hunk back to the baseline without touching the rest of the file.
///
/// Unlike <see cref="EditorPaneViewModel"/>'s inline hunk widgets — which
/// only surface hunks an agent is actively proposing during a live turn —
/// this is for browsing and hand-editing a diff on demand, independent of
/// any in-flight agent turn (e.g. "what changed since this checkpoint?").
/// </summary>
public partial class DiffViewerViewModel : ObservableObject
{
    private readonly ICheckpointManager? _checkpointManager;
    private readonly ILogger<DiffViewerViewModel>? _logger;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>Short label for the left/baseline side, e.g. "Checkpoint @ 14:32" or "Git HEAD".</summary>
    [ObservableProperty]
    private string _baselineLabel = string.Empty;

    /// <summary>Read-only baseline (left/"before") content.</summary>
    [ObservableProperty]
    private string _baselineContent = string.Empty;

    /// <summary>Editable current ("after") content — bound to the right-hand editor.</summary>
    [ObservableProperty]
    private string _currentContent = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True for comparisons of two fixed snapshots (e.g. HEAD vs staged) where nothing may be edited, saved or reverted.</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    public bool IsEditable => !IsReadOnly;

    /// <summary>Header for the right-hand side.</summary>
    [ObservableProperty]
    private string _currentLabel = "Current (editable)";

    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(IsEditable));

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Parsed hunks between <see cref="BaselineContent"/> and <see cref="CurrentContent"/>, recomputed on demand.</summary>
    public ObservableCollection<DiffHunk> Hunks { get; } = new();

    /// <summary>
    /// Rows of the GitHub-style split view: old file on the left, new file on the right, aligned,
    /// with hunk headers and collapsed "unchanged lines" gaps. Replaced as a whole on every recompute
    /// (one change notification instead of thousands).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<DiffRowViewModel> _rows = System.Array.Empty<DiffRowViewModel>();

    /// <summary>False (default): the read-only split diff. True: the two raw text panes, with the right one editable.</summary>
    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>The split view is what's shown (not the raw text editor).</summary>
    public bool IsSplitView => !IsEditMode;

    private SideBySideDiffResult? _lastDiff;
    private bool _suppressRecompute;

    /// <summary>True when the two versions have no line differences (shows a "No differences" message instead of an empty grid).</summary>
    public bool HasNoDifferences => Rows.Count == 0;

    partial void OnRowsChanged(IReadOnlyList<DiffRowViewModel> value) => OnPropertyChanged(nameof(HasNoDifferences));

    /// <summary>Suffix for the status line when a giant file was cut off.</summary>
    private string TruncationNote => _lastDiff?.Truncated == true
        ? $" · showing the first {SideBySideDiff.MaxRows:N0} rows"
        : string.Empty;

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSplitView));
        // Back from editing: show what the edits changed.
        if (!value && !_suppressRecompute)
            RecomputeDiff();
    }

    public int AddedLineCount { get; private set; }
    public int RemovedLineCount { get; private set; }

    /// <summary>Loaded checkpoint id, if the baseline came from one (used for context only — restore stays in the checkpoint browser).</summary>
    public string? SourceCheckpointId { get; private set; }

    public DiffViewerViewModel(
        ICheckpointManager? checkpointManager = null,
        ILogger<DiffViewerViewModel>? logger = null)
    {
        _checkpointManager = checkpointManager;
        _logger = logger;
    }

    /// <summary>
    /// Open the viewer for <paramref name="filePath"/>, comparing
    /// <paramref name="baselineContent"/> against the file's current
    /// on-disk content (read fresh so edits made outside the app are seen).
    /// </summary>
    public void Load(string filePath, string baselineContent, string baselineLabel, string? sourceCheckpointId = null)
    {
        _suppressRecompute = true;
        IsEditMode = false;
        _suppressRecompute = false;
        IsReadOnly = false;
        CurrentLabel = "Current (editable)";
        _diskSnapshot = null; // belongs to the previous file
        FilePath = filePath;
        BaselineLabel = baselineLabel;
        BaselineContent = baselineContent ?? string.Empty;
        SourceCheckpointId = sourceCheckpointId;

        try
        {
            CurrentContent = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read {FilePath} for diff viewer", filePath);
            CurrentContent = string.Empty;
        }

        IsDirty = false;
        IsVisible = true;
        RecomputeDiff();
        StatusText = Hunks.Count == 0
            ? "No differences"
            : $"{Hunks.Count} hunk(s), +{AddedLineCount}/-{RemovedLineCount}{TruncationNote}";
    }

    /// <summary>
    /// Show a read-only comparison of two snapshots that are not the file on disk —
    /// e.g. the staged diff (HEAD on the left, the index on the right).
    /// </summary>
    public void LoadComparison(string filePath, string baselineContent, string currentContent, string baselineLabel, string currentLabel)
    {
        _suppressRecompute = true;
        IsEditMode = false;
        _suppressRecompute = false;
        IsReadOnly = true;
        CurrentLabel = currentLabel;
        FilePath = filePath;
        BaselineLabel = baselineLabel;
        BaselineContent = baselineContent ?? string.Empty;
        SourceCheckpointId = null;
        CurrentContent = currentContent ?? string.Empty;

        IsDirty = false;
        IsVisible = true;
        RecomputeDiff();
        StatusText = Hunks.Count == 0
            ? "No differences"
            : $"{Hunks.Count} hunk(s), +{AddedLineCount}/-{RemovedLineCount}{TruncationNote} · read-only";
    }

    /// <summary>Re-run the diff between BaselineContent and CurrentContent and refresh Hunks.</summary>
    [RelayCommand]
    public void RecomputeDiff()
    {
        var diff = SideBySideDiff.Build(BaselineContent, CurrentContent, FilePath);
        _lastDiff = diff;

        Hunks.Clear();
        foreach (var hunk in diff.Hunks)
            Hunks.Add(hunk);

        AddedLineCount = diff.AddedLines;
        RemovedLineCount = diff.RemovedLines;
        Rows = diff.Rows.Select(r => new DiffRowViewModel(r)).ToList();

        OnPropertyChanged(nameof(AddedLineCount));
        OnPropertyChanged(nameof(RemovedLineCount));
    }

    /// <summary>Reveal the unchanged lines hidden behind a "Show N unchanged lines" row.</summary>
    [RelayCommand]
    public void ExpandGap(DiffRowViewModel? gap)
    {
        if (gap is not { IsGap: true } || _lastDiff == null)
            return;

        var rows = new List<DiffRowViewModel>(Rows);
        var at = rows.IndexOf(gap);
        if (at < 0)
            return;

        var revealed = new List<DiffRowViewModel>(gap.GapCount);
        for (var i = 0; i < gap.GapCount; i++)
        {
            var oldNo = gap.GapOldStart + i;
            var newNo = gap.GapNewStart + i;
            revealed.Add(new DiffRowViewModel(SideBySideRow.Context(oldNo, newNo, _lastDiff.NewLines[newNo - 1])));
        }

        rows.RemoveAt(at);
        rows.InsertRange(at, revealed);
        Rows = rows;
    }

    partial void OnCurrentContentChanged(string value)
    {
        if (IsReadOnly)
        {
            IsDirty = false;
            return;
        }

        IsDirty = !string.Equals(value, ReadDiskSnapshot(), StringComparison.Ordinal);
    }

    private string? _diskSnapshot;
    private string ReadDiskSnapshot() => _diskSnapshot ??= SafeReadFile(FilePath);

    private string SafeReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Revert a single hunk: replace its span in <see cref="CurrentContent"/>
    /// with the baseline's lines for that same region, leaving every other
    /// change in the file untouched. Recomputes the diff afterward.
    /// </summary>
    [RelayCommand]
    public void RevertHunk(DiffHunk hunk)
    {
        if (hunk == null || IsReadOnly) return;

        var currentLines = SplitPreserving(CurrentContent);
        var baselineLines = SplitPreserving(BaselineContent);

        // The hunk's "new" span (NewStartLine/NewLineCount) is where it
        // currently sits in CurrentContent; the "old" span is the
        // corresponding region in BaselineContent to restore.
        var newStartIdx = Math.Max(0, hunk.NewStartLine - 1);
        var newCount = Math.Max(0, hunk.NewLineCount);
        var oldStartIdx = Math.Max(0, hunk.OriginalStartLine - 1);
        var oldCount = Math.Max(0, hunk.OriginalLineCount);

        if (newStartIdx > currentLines.Count) newStartIdx = currentLines.Count;
        var removeCount = Math.Min(newCount, currentLines.Count - newStartIdx);

        var replacement = oldStartIdx < baselineLines.Count
            ? baselineLines.GetRange(oldStartIdx, Math.Min(oldCount, baselineLines.Count - oldStartIdx))
            : new System.Collections.Generic.List<string>();

        currentLines.RemoveRange(newStartIdx, Math.Max(0, removeCount));
        currentLines.InsertRange(newStartIdx, replacement);

        CurrentContent = string.Join("", currentLines);
        RecomputeDiff();
        StatusText = $"Reverted hunk — {Hunks.Count} hunk(s) remaining";
    }

    /// <summary>Discard every pending edit, resetting CurrentContent back to what's on disk.</summary>
    [RelayCommand]
    public void ResetToDisk()
    {
        if (IsReadOnly) return;

        _diskSnapshot = null;
        CurrentContent = ReadDiskSnapshot();
        RecomputeDiff();
        StatusText = "Reset to on-disk content";
    }

    /// <summary>
    /// Write <see cref="CurrentContent"/> to disk. Takes a checkpoint first
    /// (when a checkpoint manager is available) so a save made from the diff
    /// viewer is itself undoable, consistent with agent-driven edits.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(FilePath) || IsReadOnly)
            return;

        try
        {
            if (_checkpointManager != null && File.Exists(FilePath))
            {
                await _checkpointManager.CreateCheckpointAsync(FilePath, turnId: "diff-viewer-edit");
            }

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(FilePath, CurrentContent);
            _diskSnapshot = CurrentContent;
            IsDirty = false;
            StatusText = $"Saved {Path.GetFileName(FilePath)}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save {FilePath} from diff viewer", FilePath);
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void Close()
    {
        IsVisible = false;
    }

    /// <summary>Splits text into lines, keeping each line's trailing newline so re-joining is exact.</summary>
    private static System.Collections.Generic.List<string> SplitPreserving(string text)
    {
        var result = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                result.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }
        if (start < text.Length)
            result.Add(text.Substring(start));
        return result;
    }
}
