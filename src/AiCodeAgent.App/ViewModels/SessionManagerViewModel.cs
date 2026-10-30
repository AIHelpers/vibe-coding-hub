using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Sessions;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// GUI layer for session export/import (Priority 5). Wraps the core
/// <see cref="SessionExportService"/> and <see cref="SessionImporter"/> and
/// exposes commands wired to the command palette and session history panel.
/// </summary>
public partial class SessionManagerViewModel : ObservableObject
{
    private readonly SessionExportService _exportService;
    private readonly SessionImporter _importer;
    private readonly IContextManager _contextManager;
    private Window? _hostWindow;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> ExportedSessions { get; } = new();
    public ObservableCollection<string> SessionHistory { get; } = new();

    /// <summary>The stored chat sessions of the current project, newest first (title, date, messages, cost).</summary>
    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = new();

    private readonly ISessionCatalog? _catalog;

    /// <summary>The session the chat is in right now; it is marked in the list and cannot be deleted.</summary>
    [ObservableProperty]
    private string _currentSessionId = string.Empty;

    /// <summary>Raised by "Open": the host switches the chat to that session.</summary>
    public event Action<string>? OpenSessionRequested;

    public SessionManagerViewModel(
        SessionExportService exportService,
        SessionImporter importer,
        IContextManager contextManager,
        ISessionCatalog? catalog = null)
    {
        _exportService = exportService;
        _importer = importer;
        _contextManager = contextManager;
        _catalog = catalog;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSessions));
    }

    /// <summary>True when the list is empty (shows the "no stored sessions" hint).</summary>
    public bool HasNoSessions => Sessions.Count == 0;

    [RelayCommand]
    private void OpenSession(SessionListItemViewModel? item)
    {
        if (item != null) OpenSessionRequested?.Invoke(item.SessionId);
    }

    [RelayCommand]
    private void BeginRename(SessionListItemViewModel? item)
    {
        if (item == null) return;
        item.RenameText = item.Title;
        item.IsRenaming = true;
        item.IsConfirmingDelete = false;
    }

    [RelayCommand]
    private void CancelRename(SessionListItemViewModel? item)
    {
        if (item != null) item.IsRenaming = false;
    }

    [RelayCommand]
    private async Task CommitRenameAsync(SessionListItemViewModel? item)
    {
        if (item == null || _catalog == null) return;
        var title = item.RenameText.Trim();
        item.IsRenaming = false;
        if (title.Length == 0 || title == item.Title) return;

        try
        {
            await _catalog.RenameAsync(item.SessionId, title);
            item.Title = title;
        }
        catch (Exception ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BeginDelete(SessionListItemViewModel? item)
    {
        if (item == null || item.SessionId == CurrentSessionId) return;
        item.IsRenaming = false;
        item.IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete(SessionListItemViewModel? item)
    {
        if (item != null) item.IsConfirmingDelete = false;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync(SessionListItemViewModel? item)
    {
        if (item == null || _catalog == null || item.SessionId == CurrentSessionId) return;
        try
        {
            await _catalog.DeleteAsync(item.SessionId);
            Sessions.Remove(item);
            StatusText = $"Deleted \"{item.Title}\"";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Attach the host window so native file pickers can be shown.
    /// Called once the main window is constructed (Window isn't DI-constructible).
    /// </summary>
    public void AttachHostWindow(Window window)
    {
        _hostWindow = window;
    }

    [RelayCommand]
    private async Task ExportSessionAsync(string? sessionId = null)
    {
        // Fall back to the "default" chat session when no explicit ID is provided
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;

        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "Exporting session...";
        try
        {
            var filePath = await PickSavePathAsync("session.agentsession");
            if (string.IsNullOrEmpty(filePath))
                return;

            await _exportService.ExportAsync(sessionId, filePath);
            StatusText = $"Exported session to {filePath}";
            ExportedSessions.Add(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// E7-S2: saves the audit trail of every task run in a session (who, what, when, how much — hash-chained, secrets removed)
    /// as a zip (one folder per task, with diffs), Markdown or JSON.
    /// </summary>
    [RelayCommand]
    private async Task ExportAuditAsync(string? sessionId = null)
    {
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        if (IsBusy)
            return;

        var files = new AiCodeAgent.Core.Audit.AuditLocator().ForSession(sessionId);
        if (files.Count == 0)
        {
            StatusText = "This session has no audit trail yet (it is written for every task run from now on).";
            return;
        }

        IsBusy = true;
        StatusText = "Exporting audit trail...";
        try
        {
            if (_hostWindow == null)
            {
                StatusText = string.Empty;
                return;
            }
            var picked = await _hostWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the audit trail",
                SuggestedFileName = "session.audit.zip",
                DefaultExtension = "zip",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Zip with diffs (.zip)") { Patterns = new[] { "*.zip" } },
                    new FilePickerFileType("Markdown (.md)") { Patterns = new[] { "*.md" } },
                    new FilePickerFileType("JSON (.json)") { Patterns = new[] { "*.json" } }
                }
            });
            var path = picked?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                StatusText = string.Empty;
                return;
            }

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var format = ext == ".md" ? AiCodeAgent.Core.Audit.AuditExportFormat.Markdown
                : ext == ".json" ? AiCodeAgent.Core.Audit.AuditExportFormat.Json
                : AiCodeAgent.Core.Audit.AuditExportFormat.Zip;
            var results = await Task.Run(() => AiCodeAgent.Core.Audit.AuditExporter.ExportMany(files, path, format));
            var broken = results.Count(r => !r.Ok);
            StatusText = broken == 0
                ? $"Exported the audit trail of {files.Count} task(s) to {path}"
                : $"Exported to {path}, but {broken} audit file(s) fail the hash check.";
        }
        catch (Exception ex)
        {
            StatusText = $"Audit export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportSessionAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "Importing session...";
        try
        {
            var filePath = await PickOpenPathAsync();
            if (string.IsNullOrEmpty(filePath))
                return;

            await ImportSessionFromPathAsync(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Import a session bundle from a file path. Used by both the file-picker
    /// flow and drag-and-drop of a `.agentsession` file onto the window.
    /// </summary>
    public async Task ImportSessionFromPathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var result = await _importer.ImportAsync(filePath);
        StatusText = $"Imported session '{result.SessionId}': {result.MessageCount} messages, " +
                     $"{result.ToolCallCount} tool calls, {result.HunkCount} hunks, " +
                     $"{result.CheckpointCount} checkpoints";

        if (result.HasConflicts)
        {
            StatusText += $" — {result.Conflicts.Count} conflicts (see messages)";
        }

        await RefreshSessionHistoryAsync();
    }

    /// <summary>Refresh the session history list from the context manager.</summary>
    [RelayCommand]
    private async Task RefreshSessionHistoryAsync()
    {
        try
        {
            var sessions = await _contextManager.GetSessionsAsync();
            SessionHistory.Clear();
            foreach (var sessionId in sessions.OrderByDescending(s => s))
            {
                SessionHistory.Add(sessionId);
            }

            Sessions.Clear();
            if (_catalog != null)
            {
                foreach (var info in await _catalog.ListAsync())
                    Sessions.Add(new SessionListItemViewModel(info) { IsCurrent = info.SessionId == CurrentSessionId });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load session history: {ex.Message}";
        }
    }

    /// <summary>Clear transient session-history state when the panel is closed.</summary>
    public void CloseSessionHistory()
    {
        StatusText = string.Empty;
    }

    private async Task<string?> PickSavePathAsync(string defaultName)
    {
        if (_hostWindow == null)
            return defaultName; // headless fallback

        var result = await _hostWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Session",
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Session bundle (.agentsession)") { Patterns = new[] { "*.agentsession" } },
                new FilePickerFileType("Session JSON (.json)") { Patterns = new[] { "*.json" } }
            }
        });

        return result?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenPathAsync()
    {
        if (_hostWindow == null)
            return null;

        var result = await _hostWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Session",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Session bundles") { Patterns = new[] { "*.agentsession", "*.json" } }
            }
        });

        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}

/// <summary>One row of the session list: title, date, message count and cost, plus its inline rename / delete state.</summary>
public partial class SessionListItemViewModel : ObservableObject
{
    public SessionListItemViewModel(ChatSessionInfo info)
    {
        SessionId = info.SessionId;
        Title = info.Title;
        UpdatedAt = info.UpdatedAt;
        MessageCount = info.MessageCount;
        CostUsd = info.CostUsd;
    }

    public string SessionId { get; }
    public DateTime UpdatedAt { get; }
    public int MessageCount { get; }
    public decimal CostUsd { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>"Oct 27, 14:32 · 12 messages · $0.42" (the cost is left out when nothing was priced).</summary>
    public string Details
    {
        get
        {
            var local = LocalTime.ToLocal(UpdatedAt);
            var text = $"{local:d} {LocalTime.FormatShortTime(UpdatedAt)} · {MessageCount} message{(MessageCount == 1 ? string.Empty : "s")}";
            return CostUsd > 0 ? $"{text} · ${CostUsd:0.00##}" : text;
        }
    }

    /// <summary>The current session cannot be deleted from under the chat.</summary>
    public bool CanDelete => !IsCurrent;

    /// <summary>True while neither the rename box nor the delete confirmation is showing.</summary>
    public bool IsBrowsing => !IsRenaming && !IsConfirmingDelete;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(IsBrowsing));
    partial void OnIsConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(IsBrowsing));
}
