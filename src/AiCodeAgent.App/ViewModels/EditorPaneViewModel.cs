using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.EditHistory;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Manages open editor tabs: open/close/switch-tab, unsaved-changes prompts.
/// </summary>
public partial class EditorPaneViewModel : ObservableObject
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PredictionDebounce = TimeSpan.FromMilliseconds(400);

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private NextEditPrediction? _currentPrediction;

    private readonly SharedChangeset? _changeset;
    private readonly LspDocumentService? _lspService;
    private readonly EditHistoryTracker? _editHistory;
    private readonly NextEditPredictor? _predictor;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _predictionCts;
    private string? _pendingChangeFile;

    /// <summary>
    /// Set by the View to open a Save-As file picker. Returns the chosen path, or
    /// <c>null</c> if the user cancels. When <c>null</c>, Save As falls back to plain Save.
    /// </summary>
    public Func<Task<string?>>? SaveAsPathPicker { get; set; }

    /// <summary>
    /// Set by the View to prompt the user about unsaved changes before closing a dirty
    /// tab (Save / Discard / Cancel, like VS Code). Returns <c>true</c> to proceed with
    /// closing, <c>false</c> to cancel. When <c>null</c>, dirty tabs are auto-saved.
    /// </summary>
    public Func<EditorTabViewModel, Task<bool>>? ConfirmSaveChangesAsync { get; set; }

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>True when at least one editor tab is open (for pane visibility).</summary>
    public bool HasOpenTabs => Tabs.Count > 0;

    /// <summary>True when a file is currently active in the editor.</summary>
    public bool HasActiveTab => ActiveTab != null;

    public EditorPaneViewModel(
        SharedChangeset? changeset = null,
        LspDocumentService? lspService = null,
        EditHistoryTracker? editHistory = null,
        NextEditPredictor? predictor = null)
    {
        _changeset = changeset;
        _lspService = lspService;
        _editHistory = editHistory;
        _predictor = predictor;
    }

    partial void OnActiveTabChanged(EditorTabViewModel? value)
    {
        // When switching tabs, refresh diff overlays
        if (value != null)
        {
            OnPropertyChanged(nameof(ActiveTabHunks));
        }
        OnPropertyChanged(nameof(HasActiveTab));
    }

    /// <summary>Notify the UI that hunks have changed (for diff overlay refresh).</summary>
    public void NotifyHunksChanged()
    {
        OnPropertyChanged(nameof(ActiveTabHunks));
    }

    /// <summary>Hunks for the currently active file (for diff overlay rendering).</summary>
    public IReadOnlyList<DiffHunk> ActiveTabHunks
    {
        get
        {
            if (ActiveTab == null || _changeset == null)
                return Array.Empty<DiffHunk>();

            return _changeset.GetHunksForFile(ActiveTab.FilePath).ToList();
        }
    }

    /// <summary>Open a file in the editor. If already open, switch to it.</summary>
    public async Task OpenFileAsync(string path, bool readOnly = false, bool isPreview = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        // Check if already open
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsPreview = false; // Pin it since it's being explicitly opened
            ActiveTab = existing;
            return;
        }

        EditorTabViewModel tab;

        if (isPreview)
        {
            // VS Code behavior: reuse the existing preview tab if it's not the one we are opening
            var previewTab = Tabs.FirstOrDefault(t => t.IsPreview);
            if (previewTab != null)
            {
                tab = previewTab;
                await tab.LoadAsync(path, readOnly);
            }
            else
            {
                tab = new EditorTabViewModel { IsPreview = true };
                await tab.LoadAsync(path, readOnly);
                tab.Document.TextChanged += OnTabDocumentTextChanged;
                Tabs.Add(tab);
            }
        }
        else
        {
            tab = new EditorTabViewModel { IsPreview = false };
            await tab.LoadAsync(path, readOnly);
            tab.Document.TextChanged += OnTabDocumentTextChanged;
            Tabs.Add(tab);
        }

        ActiveTab = tab;
        OnPropertyChanged(nameof(HasOpenTabs));

        // Notify the language server that the document was opened
        if (_lspService != null && !readOnly && tab.Document.TextLength > 0)
        {
            _ = _lspService.OpenDocumentAsync(path, tab.Document.Text);
        }
    }

    /// <summary>Make a tab permanent (not a preview).</summary>
    public void PinTab(EditorTabViewModel tab)
    {
        if (tab == null) return;
        tab.IsPreview = false;
    }

    /// <summary>Close a tab, prompting for unsaved changes (VS Code style).</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.IsDirty && ConfirmSaveChangesAsync != null)
        {
            // VS Code behavior: prompt Save / Discard / Cancel
            var proceed = await ConfirmSaveChangesAsync(tab);
            if (!proceed)
                return false;
        }
        else if (tab.IsDirty)
        {
            // Fallback: auto-save
            await tab.SaveAsync();
        }

        // Notify the language server that the document was closed
        if (_lspService != null && !string.IsNullOrEmpty(tab.FilePath))
        {
            _ = _lspService.CloseDocumentAsync(tab.FilePath);
        }

        tab.Document.TextChanged -= OnTabDocumentTextChanged;
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.LastOrDefault();
        }
        OnPropertyChanged(nameof(HasOpenTabs));
        return true;
    }

    /// <summary>Close all tabs.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            await CloseTabAsync(tab);
        }
    }

    /// <summary>
    /// Handles text changes with a debounce (~300ms) before notifying the
    /// language server so its diagnostics stay live while typing.
    /// </summary>
    private void OnTabDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_lspService == null)
            return;

        var tab = Tabs.FirstOrDefault(t => t.Document == sender);
        if (tab == null || string.IsNullOrEmpty(tab.FilePath))
            return;

        _pendingChangeFile = tab.FilePath;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Delay(ChangeDebounce, token)
            .ContinueWith(async _ =>
            {
                if (token.IsCancellationRequested || _pendingChangeFile == null)
                    return;

                var filePath = _pendingChangeFile;
                _pendingChangeFile = null;

                var target = Tabs.FirstOrDefault(t =>
                    string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return;

                await _lspService.UpdateDocumentAsync(filePath, target.Document.Text);
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Trigger a next-edit prediction for the active tab (debounced).</summary>
    public void RequestPrediction()
    {
        if (_predictor == null || _editHistory == null || ActiveTab == null)
            return;

        _predictionCts?.Cancel();
        _predictionCts?.Dispose();
        _predictionCts = new CancellationTokenSource();
        var token = _predictionCts.Token;

        _ = Task.Delay(PredictionDebounce, token)
            .ContinueWith(async _ =>
            {
                if (token.IsCancellationRequested || ActiveTab == null)
                    return;

                var filePath = ActiveTab.FilePath;
                var content = ActiveTab.Document.Text;
                var line = ActiveTab.CaretLine;
                var col = ActiveTab.CaretColumn;
                var recent = _editHistory.GetRecent(20);

                try
                {
                    var prediction = await _predictor.PredictAsync(
                        filePath, content, line, col, recent, token);
                    if (!token.IsCancellationRequested)
                        CurrentPrediction = prediction;
                }
                catch (OperationCanceledException) { /* ignored */ }
                catch { /* swallow prediction errors */ }
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Accept the current prediction: apply its spans to the document.</summary>
    [RelayCommand]
    public async Task AcceptPredictionAsync()
    {
        if (CurrentPrediction == null || ActiveTab == null)
            return;

        var doc = ActiveTab.Document;
        var lines = doc.Lines;

        foreach (var span in CurrentPrediction.Spans)
        {
            var startLine = Math.Max(1, span.StartLine);
            if (startLine > lines.Count + 1)
                continue;

            var offset = startLine <= lines.Count
                ? lines[startLine - 1].Offset
                : doc.TextLength;

            if (span.IsInsert)
            {
                doc.Insert(offset, span.NewText + "\n");
            }
            else
            {
                var endLine = Math.Min(span.EndLine, lines.Count);
                var endOffset = endLine <= lines.Count
                    ? lines[endLine - 1].Offset + lines[endLine - 1].Length
                    : doc.TextLength;
                doc.Replace(offset, endOffset - offset, span.NewText);
            }

            // Record the accepted edit in history
            _editHistory?.Record(new EditRecord
            {
                FilePath = ActiveTab.FilePath,
                Before = string.Empty,
                After = span.NewText,
                StartLine = span.StartLine,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        CurrentPrediction = null;
        await Task.CompletedTask;
    }

    /// <summary>Dismiss the current prediction without applying it.</summary>
    [RelayCommand]
    public void DismissPrediction()
    {
        CurrentPrediction = null;
    }

    /// <summary>Save the active tab.</summary>
    public async Task<bool> SaveActiveAsync()
    {
        if (ActiveTab == null)
            return false;
        return await ActiveTab.SaveAsync();
    }

    /// <summary>Save the active tab to a new location (Save As).</summary>
    public async Task<bool> SaveActiveAsAsync()
    {
        if (ActiveTab == null)
            return false;

        if (SaveAsPathPicker != null)
        {
            var path = await SaveAsPathPicker();
            if (string.IsNullOrEmpty(path))
                return false; // Cancelled
            return await ActiveTab.SaveToAsync(path);
        }

        // No picker wired up; fall back to plain save
        return await ActiveTab.SaveAsync();
    }

    /// <summary>Close the active tab (VS Code-style unsaved-changes prompt).</summary>
    public async Task<bool> CloseActiveTabAsync()
    {
        if (ActiveTab == null)
            return false;
        return await CloseTabAsync(ActiveTab);
    }

    /// <summary>Switch to the next tab (cycling).</summary>
    public void NextTab()
    {
        if (Tabs.Count == 0) return;

        var idx = ActiveTab != null ? Tabs.IndexOf(ActiveTab) : -1;
        ActiveTab = Tabs[(idx + 1) % Tabs.Count];
    }

    /// <summary>Switch to the previous tab (cycling).</summary>
    public void PreviousTab()
    {
        if (Tabs.Count == 0) return;

        var idx = ActiveTab != null ? Tabs.IndexOf(ActiveTab) : 0;
        ActiveTab = Tabs[(idx - 1 + Tabs.Count) % Tabs.Count];
    }

    /// <summary>Save all dirty tabs.</summary>
    public async Task SaveAllAsync()
    {
        foreach (var tab in Tabs.Where(t => t.IsDirty))
        {
            await tab.SaveAsync();
        }
    }

    /// <summary>Apply a hunk to the active file buffer (accept).</summary>
    public async Task<bool> AcceptHunkAsync(string hunkId)
    {
        if (ActiveTab == null || _changeset == null)
            return false;

        var hunk = _changeset.Hunks.FirstOrDefault(h => h.HunkId == hunkId);
        if (hunk == null || hunk.Status != HunkStatus.Pending)
            return false;

        // Apply the hunk's added lines to the document
        var doc = ActiveTab.Document;
        var lines = doc.Lines;
        var insertOffset = 0;

        // Find the position in the document where the hunk applies
        // For simplicity, apply at the NewStartLine position
        if (hunk.NewStartLine > 0 && hunk.NewStartLine <= lines.Count)
        {
            var line = lines[hunk.NewStartLine - 1];
            insertOffset = line.Offset;
        }

        // Build the text to insert (added lines only)
        var addedText = string.Join("\n", hunk.Lines
            .Where(l => l.Kind == DiffLineKind.Added)
            .Select(l => l.Text));

        if (!string.IsNullOrEmpty(addedText))
        {
            doc.Insert(insertOffset, addedText + "\n");
        }

        _changeset.UpdateHunkStatus(hunkId, HunkStatus.Accepted);
        OnPropertyChanged(nameof(ActiveTabHunks));
        return true;
    }

    /// <summary>Reject a hunk (discard it).</summary>
    public bool RejectHunk(string hunkId)
    {
        if (_changeset == null)
            return false;

        var result = _changeset.UpdateHunkStatus(hunkId, HunkStatus.Rejected);
        if (result)
        {
            OnPropertyChanged(nameof(ActiveTabHunks));
        }
        return result;
    }

    /// <summary>Accept all pending hunks for the active file.</summary>
    public async Task AcceptAllAsync()
    {
        if (ActiveTab == null || _changeset == null)
            return;

        var pendingHunks = _changeset.GetHunksForFile(ActiveTab.FilePath)
            .Where(h => h.Status == HunkStatus.Pending)
            .ToList();

        foreach (var hunk in pendingHunks)
        {
            await AcceptHunkAsync(hunk.HunkId);
        }
    }

    /// <summary>Reject all pending hunks for the active file.</summary>
    public void RejectAll()
    {
        if (ActiveTab == null || _changeset == null)
            return;

        var pendingHunks = _changeset.GetHunksForFile(ActiveTab.FilePath)
            .Where(h => h.Status == HunkStatus.Pending)
            .ToList();

        foreach (var hunk in pendingHunks)
        {
            _changeset.UpdateHunkStatus(hunk.HunkId, HunkStatus.Rejected);
        }
        OnPropertyChanged(nameof(ActiveTabHunks));
    }

    [RelayCommand]
    private void ActivateTab(EditorTabViewModel tab)
    {
        if (tab != null && Tabs.Contains(tab))
            ActiveTab = tab;
    }

    [RelayCommand]
    private async Task OpenFile(string path)
    {
        await OpenFileAsync(path);
    }

    [RelayCommand]
    private async Task CloseTab(EditorTabViewModel tab)
    {
        await CloseTabAsync(tab);
    }

    [RelayCommand]
    private async Task SaveActive()
    {
        await SaveActiveAsync();
    }

    [RelayCommand]
    private async Task SaveActiveAs()
    {
        await SaveActiveAsAsync();
    }

    [RelayCommand]
    private async Task SaveAll()
    {
        await SaveAllAsync();
    }

    [RelayCommand]
    private async Task CloseActiveTab()
    {
        await CloseActiveTabAsync();
    }

    [RelayCommand]
    private void NextTabCommand()
    {
        NextTab();
    }

    [RelayCommand]
    private void PreviousTabCommand()
    {
        PreviousTab();
    }

    [RelayCommand]
    private async Task AcceptHunkCommand(string hunkId)
    {
        await AcceptHunkAsync(hunkId);
    }

    [RelayCommand]
    private void RejectHunkCommand(string hunkId)
    {
        RejectHunk(hunkId);
    }

    [RelayCommand]
    private async Task AcceptAllCommand()
    {
        await AcceptAllAsync();
    }

    [RelayCommand]
    private void RejectAllCommand()
    {
        RejectAll();
    }
}