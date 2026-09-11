using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

public enum BackgroundTaskStatus { Running, Completed, Failed, Cancelled }

/// <summary>
/// One delegated task: an agent turn running in its own isolated session,
/// independent of whatever the user is doing in the main chat. Output
/// accumulates live as the task's <see cref="SessionScopedEvent"/>s arrive.
/// </summary>
public partial class BackgroundTaskItemViewModel : ObservableObject
{
    /// <summary>The isolated session id this task's agent turn runs under — how events are matched back to this item.</summary>
    public required string SessionId { get; init; }

    public required string Prompt { get; init; }

    public DateTime StartedAt { get; init; } = DateTime.Now;

    [ObservableProperty]
    private BackgroundTaskStatus _status = BackgroundTaskStatus.Running;

    [ObservableProperty]
    private string _output = string.Empty;

    [ObservableProperty]
    private int _toolCallCount;

    [ObservableProperty]
    private DateTime? _completedAt;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True while this task is still running — avoids an XAML converter for the Cancel button's visibility.</summary>
    public bool IsRunning => Status == BackgroundTaskStatus.Running;

    /// <summary>Short human-readable status for display.</summary>
    public string StatusLabel => Status switch
    {
        BackgroundTaskStatus.Running => "● Running",
        BackgroundTaskStatus.Completed => "✓ Completed",
        BackgroundTaskStatus.Failed => "✗ Failed",
        BackgroundTaskStatus.Cancelled => "○ Cancelled",
        _ => Status.ToString()
    };

    /// <summary>Status-appropriate color for <see cref="StatusLabel"/> — a whole-TextBlock Foreground binding, not a Run, so it's safe (see DiffViewerView's history).</summary>
    public string StatusColor => Status switch
    {
        BackgroundTaskStatus.Running => "#4A9EFF",
        BackgroundTaskStatus.Completed => "#4CAF50",
        BackgroundTaskStatus.Failed => "#E05555",
        BackgroundTaskStatus.Cancelled => "#9AA0A6",
        _ => "#9AA0A6"
    };

    public bool HasOutput => !string.IsNullOrEmpty(Output);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnStatusChanged(BackgroundTaskStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusColor));
    }

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Cancels this task's own event-listening loop once its agent turn finishes — never shared with any other task.</summary>
    internal CancellationTokenSource ListenCts { get; } = new();
}

/// <summary>
/// Runs agent prompts in the background, detached from the main chat —
/// Cowork's "delegate a task, keep working, get notified when it's done"
/// model. Each task gets its own session id, so its progress (text, tool
/// calls, completion) is tracked independently and never interleaves with
/// the main chat or with any other background task, even though they all
/// share the same underlying event bus (see <see cref="SessionScopedEvent"/>
/// and <see cref="AgentService"/>'s per-session cancellation).
/// </summary>
public partial class BackgroundTaskManagerViewModel : ObservableObject
{
    private readonly AgentService _agentService;
    private readonly IAgentEventBus _eventBus;
    private readonly ILogger<BackgroundTaskManagerViewModel>? _logger;

    public ObservableCollection<BackgroundTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _newTaskPrompt = string.Empty;

    /// <summary>Working directory to run new tasks in — set by the host (MainViewModel) whenever it opens this panel, so tasks started here use the same directory as the main chat.</summary>
    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    /// <summary>Number of tasks still running — for a toolbar badge.</summary>
    public int RunningCount => Tasks.Count(t => t.Status == BackgroundTaskStatus.Running);

    /// <summary>True when at least one task is running — avoids binding an int directly to a bool IsVisible.</summary>
    public bool HasRunningTasks => RunningCount > 0;

    /// <summary>Raises change notifications for both RunningCount and the derived HasRunningTasks — call whenever a task's Status changes.</summary>
    private void NotifyRunningCountChanged()
    {
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(HasRunningTasks));
    }

    public BackgroundTaskManagerViewModel(
        AgentService agentService,
        IAgentEventBus eventBus,
        ILogger<BackgroundTaskManagerViewModel>? logger = null)
    {
        _agentService = agentService;
        _eventBus = eventBus;
        _logger = logger;
    }

    [RelayCommand]
    private void Toggle() => IsVisible = !IsVisible;

    public void Close() => IsVisible = false;

    /// <summary>
    /// Starts <see cref="NewTaskPrompt"/> as a detached task using the
    /// panel's own input box, then clears it — the self-service entry point
    /// for the Background Tasks panel (as opposed to <see cref="StartTask"/>,
    /// which other view-models can call directly with their own options).
    /// </summary>
    [RelayCommand]
    private void StartNewTask()
    {
        var prompt = NewTaskPrompt.Trim();
        if (string.IsNullOrEmpty(prompt))
            return;

        StartTask(prompt, new AgentOptions
        {
            WorkingDirectory = WorkingDirectory,
            // Background tasks have no one watching to answer an approval
            // prompt, so "Ask" would just hang forever. AutoEdit lets the
            // task actually make file edits while still not auto-running
            // arbitrary shell commands unattended.
            PermissionMode = AiCodeAgent.Core.Models.PermissionMode.AutoEdit
        });
        NewTaskPrompt = string.Empty;
    }

    /// <summary>
    /// Kicks off <paramref name="prompt"/> as a detached task: returns
    /// immediately, the task runs (and can be watched or cancelled) in the
    /// panel. <paramref name="baseOptions"/> supplies working directory,
    /// permission mode, etc. — the same options the caller would otherwise
    /// pass to a normal foreground chat turn.
    /// </summary>
    public BackgroundTaskItemViewModel StartTask(string prompt, AgentOptions baseOptions)
    {
        var sessionId = "bg-" + Guid.NewGuid().ToString("N")[..8];
        var item = new BackgroundTaskItemViewModel
        {
            SessionId = sessionId,
            Prompt = prompt
        };
        Tasks.Insert(0, item);
        NotifyRunningCountChanged();

        _ = RunTaskAsync(item, prompt, baseOptions);
        return item;
    }

    private async Task RunTaskAsync(BackgroundTaskItemViewModel item, string prompt, AgentOptions options)
    {
        var listenTask = ListenAsync(item);
        try
        {
            await _agentService.StreamMessageAsync(prompt, item.SessionId, options, item.ListenCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Background task {SessionId} failed to start", item.SessionId);
        }
        finally
        {
            // The agent turn is done (or threw before even starting) — stop
            // this task's own listener loop. A well-behaved run already set
            // Status via the AgentFinishedEvent/AgentErrorEvent branches
            // below; this is a safety net for the case neither ever arrives.
            item.ListenCts.Cancel();
            try { await listenTask.ConfigureAwait(false); } catch { /* expected on cancel */ }

            if (item.Status == BackgroundTaskStatus.Running)
            {
                item.Status = BackgroundTaskStatus.Completed;
                item.CompletedAt = DateTime.Now;
                NotifyRunningCountChanged();
            }
        }
    }

    /// <summary>
    /// Reads the shared event bus (a fresh independent broadcast channel —
    /// see AgentEventBus) filtering for just this task's session id, and
    /// updates its live output/status until it finishes or is cancelled.
    /// </summary>
    private async Task ListenAsync(BackgroundTaskItemViewModel item)
    {
        try
        {
            await foreach (var raw in _eventBus.GetEventsAsync(item.ListenCts.Token))
            {
                if (raw is not SessionScopedEvent scoped ||
                    !string.Equals(scoped.SessionId, item.SessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (scoped.Inner)
                {
                    case TextDeltaEvent delta:
                        item.Output += delta.Delta;
                        break;

                    case ToolCallStartEvent:
                        item.ToolCallCount++;
                        break;

                    case AgentFinishedEvent finished:
                        item.Status = finished.Response.WasCancelled
                            ? BackgroundTaskStatus.Cancelled
                            : BackgroundTaskStatus.Completed;
                        item.CompletedAt = DateTime.Now;
                        NotifyRunningCountChanged();
                        return;

                    case AgentErrorEvent error:
                        item.Status = BackgroundTaskStatus.Failed;
                        item.ErrorMessage = error.Error.Message;
                        item.CompletedAt = DateTime.Now;
                        NotifyRunningCountChanged();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path once the task's own turn completes.
        }
    }

    /// <summary>Cancels a specific running task without affecting any other task or the main chat.</summary>
    [RelayCommand]
    private void CancelTask(BackgroundTaskItemViewModel? item)
    {
        if (item == null || item.Status != BackgroundTaskStatus.Running)
            return;

        _agentService.Cancel(item.SessionId);
    }

    /// <summary>Removes every finished (non-running) task from the list.</summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        for (var i = Tasks.Count - 1; i >= 0; i--)
        {
            if (Tasks[i].Status != BackgroundTaskStatus.Running)
                Tasks.RemoveAt(i);
        }
    }
}
