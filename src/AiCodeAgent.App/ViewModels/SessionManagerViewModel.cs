using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

    public SessionManagerViewModel(
        SessionExportService exportService,
        SessionImporter importer,
        IContextManager contextManager)
    {
        _exportService = exportService;
        _importer = importer;
        _contextManager = contextManager;
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