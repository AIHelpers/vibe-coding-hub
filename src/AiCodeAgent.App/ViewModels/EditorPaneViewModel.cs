using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Manages open editor tabs: open/close/switch-tab, unsaved-changes prompts.
/// </summary>
public partial class EditorPaneViewModel : ObservableObject
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(300);

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isVisible = true;

    private readonly SharedChangeset? _changeset;
    private readonly LspDocumentService? _lspService;
    private CancellationTokenSource? _debounceCts;
    private string? _pendingChangeFile;

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>True when at least one editor tab is open (for pane visibility).</summary>
    public bool HasOpenTabs => Tabs.Count > 0;

    /// <summary>True when a file is currently active in the editor.</summary>
    public bool HasActiveTab => ActiveTab != null;

    public EditorPaneViewModel(
        SharedChangeset? changeset = null,
        LspDocumentService? lspService = null)
    {
        _changeset = changeset;
        _lspService = lspService;
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
    public async Task OpenFileAsync(string path, bool readOnly = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        // Check if already open
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new EditorTabViewModel();
        await tab.LoadAsync(path, readOnly);
        tab.Document.TextChanged += OnTabDocumentTextChanged;
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(HasOpenTabs));

        // Notify the language server that the document was opened
        if (_lspService != null && !readOnly && tab.Document.TextLength > 0)
        {
            _ = _lspService.OpenDocumentAsync(path, tab.Document.Text);
        }
    }

    /// <summary>Close a tab, prompting for unsaved changes.</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.IsDirty)
        {
            // TODO: Show unsaved-changes prompt (Save / Discard / Cancel)
            // For now, auto-save
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

    /// <summary>Save the active tab.</summary>
    public async Task<bool> SaveActiveAsync()
    {
        if (ActiveTab == null)
            return false;
        return await ActiveTab.SaveAsync();
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
    private async Task SaveAll()
    {
        await SaveAllAsync();
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