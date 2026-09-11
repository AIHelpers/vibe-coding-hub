using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Lists checkpoints from CheckpointManager per session, grouped by turn.
/// Each row: timestamp, turn number, files touched, Restore button.
/// </summary>
public partial class CheckpointBrowserViewModel : ObservableObject
{
    private readonly ICheckpointManager _checkpointManager;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _sessionId = "default";

    public ObservableCollection<CheckpointGroupViewModel> Groups { get; } = new();

    /// <summary>
    /// Raised when the user asks to view the diff for a checkpointed file.
    /// MainViewModel subscribes to this to open the diff viewer panel —
    /// kept as an event rather than a direct reference so this view-model
    /// doesn't need to know about the diff viewer to be constructed.
    /// </summary>
    public event Action<CheckpointItemViewModel>? ViewDiffRequested;

    public CheckpointBrowserViewModel(ICheckpointManager checkpointManager)
    {
        _checkpointManager = checkpointManager;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Loading checkpoints...";
        Groups.Clear();

        try
        {
            var checkpoints = await _checkpointManager.GetCheckpointsForSessionAsync(SessionId);
            var grouped = checkpoints
                .GroupBy(c => c.TurnId)
                .OrderByDescending(g => g.Max(c => c.Timestamp));

            foreach (var group in grouped)
            {
                var groupVm = new CheckpointGroupViewModel
                {
                    TurnId = group.Key,
                    Timestamp = group.Max(c => c.Timestamp),
                    FileCount = group.Select(c => c.FilePath).Distinct().Count()
                };

                foreach (var checkpoint in group.OrderBy(c => c.Timestamp))
                {
                    groupVm.Checkpoints.Add(new CheckpointItemViewModel
                    {
                        CheckpointId = checkpoint.CheckpointId,
                        FilePath = checkpoint.FilePath,
                        FileName = Path.GetFileName(checkpoint.FilePath),
                        Timestamp = checkpoint.Timestamp,
                        OriginalContent = checkpoint.OriginalContent,
                        CanRestore = true
                    });
                }

                Groups.Add(groupVm);
            }

            StatusText = Groups.Count == 0
                ? "No checkpoints found for this session"
                : $"{Groups.Count} groups, {checkpoints.Count} checkpoints";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(CheckpointItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.CheckpointId))
            return;

        StatusText = "Restoring checkpoint...";
        var success = await _checkpointManager.RestoreCheckpointAsync(item.CheckpointId);
        StatusText = success ? "Checkpoint restored" : "Failed to restore checkpoint";
        await RefreshAsync();
    }

    [RelayCommand]
    private void SetSession(string sessionId)
    {
        SessionId = sessionId;
        _ = RefreshAsync();
    }

    /// <summary>Ask whoever hosts this panel (MainViewModel) to open a diff viewer for this checkpoint's file.</summary>
    [RelayCommand]
    private void ViewDiff(CheckpointItemViewModel item)
    {
        if (item == null) return;
        ViewDiffRequested?.Invoke(item);
    }
}

public partial class CheckpointGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _turnId = string.Empty;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<CheckpointItemViewModel> Checkpoints { get; } = new();
}

public partial class CheckpointItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _checkpointId = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private bool _canRestore;

    /// <summary>The file's content as of this checkpoint — used as the diff-viewer baseline; not shown directly in the list row.</summary>
    public string OriginalContent { get; init; } = string.Empty;
}