using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
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

/// <summary>A project (folder) a background task can be fanned out to, with a checkbox for the "run in several projects" picker.</summary>
public partial class TaskProjectOption : ObservableObject
{
    public TaskProjectOption(string path, bool isSelected = false)
    {
        Path = path;
        _isSelected = isSelected;
    }

    /// <summary>Absolute folder path — becomes the task's working directory.</summary>
    public string Path { get; }

    /// <summary>Folder name shown in the picker and on the task card.</summary>
    public string Name => ProjectNameOf(Path);

    [ObservableProperty]
    private bool _isSelected;

    internal static string ProjectNameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

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

    /// <summary>Folder this task runs in (empty when unknown).</summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>Short project name for the task card — empty hides the badge.</summary>
    public string ProjectName => string.IsNullOrEmpty(ProjectPath) ? string.Empty : TaskProjectOption.ProjectNameOf(ProjectPath);

    public bool HasProject => !string.IsNullOrEmpty(ProjectPath);

    /// <summary>Shared by all tasks launched together from one prompt (one per project); empty for a single launch.</summary>
    public string BatchId { get; init; } = string.Empty;

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

    /// <summary>Projects the new-task prompt can be sent to. Tick several to create one task in each, all running in parallel.</summary>
    public ObservableCollection<TaskProjectOption> Projects { get; } = new();

    /// <summary>Raised when the user asks to add another project folder; the host (MainViewModel) shows the folder picker and calls <see cref="SetProjects"/> again.</summary>
    public event Action? AddProjectRequested;

    /// <summary>True when there is more than one project to choose from — hides the picker for single-project users.</summary>
    public bool HasMultipleProjects => Projects.Count > 1;

    /// <summary>Number of ticked projects.</summary>
    public int SelectedProjectCount => Projects.Count(p => p.IsSelected);

    /// <summary>Label for the start button, e.g. "▶ Start" or "▶ Start in 2 projects".</summary>
    public string StartButtonText => SelectedProjectCount > 1 ? $"▶ Start in {SelectedProjectCount} projects" : "▶ Start";

    /// <summary>
    /// Rebuilds the project list from the main working directory plus any extra folders.
    /// The main working directory is ticked by default; selections the user already made
    /// for folders that are still present are kept.
    /// </summary>
    public void SetProjects(string mainDirectory, IEnumerable<string>? extraFolders)
    {
        var previouslySelected = Projects.Where(p => p.IsSelected)
            .Select(p => p.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadSelection = previouslySelected.Count > 0;

        foreach (var existing in Projects)
            existing.PropertyChanged -= OnProjectOptionChanged;
        Projects.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(mainDirectory))
            candidates.Add(mainDirectory);
        if (extraFolders != null)
            candidates.AddRange(extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)));

        foreach (var path in candidates)
        {
            var normalized = path.TrimEnd('/', '\\');
            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;

            var selected = hadSelection
                ? previouslySelected.Contains(path) || previouslySelected.Contains(normalized)
                : string.Equals(path, mainDirectory, StringComparison.OrdinalIgnoreCase);
            var option = new TaskProjectOption(path, selected);
            option.PropertyChanged += OnProjectOptionChanged;
            Projects.Add(option);
        }

        // Never leave the picker with nothing ticked.
        if (Projects.Count > 0 && Projects.All(p => !p.IsSelected))
            Projects[0].IsSelected = true;

        OnPropertyChanged(nameof(HasMultipleProjects));
        OnPropertyChanged(nameof(SelectedProjectCount));
        OnPropertyChanged(nameof(StartButtonText));
    }

    private void OnProjectOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskProjectOption.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedProjectCount));
            OnPropertyChanged(nameof(StartButtonText));
        }
    }

    /// <summary>Working directories a new task will be started in: every ticked project, or the panel's own directory when the picker is empty.</summary>
    public IReadOnlyList<string> GetSelectedProjectPaths()
    {
        var selected = Projects.Where(p => p.IsSelected).Select(p => p.Path).ToList();
        if (selected.Count == 0 && !string.IsNullOrWhiteSpace(WorkingDirectory))
            selected.Add(WorkingDirectory);
        return selected;
    }

    [RelayCommand]
    private void AddProject() => AddProjectRequested?.Invoke();

    [RelayCommand]
    private void SelectAllProjects()
    {
        foreach (var p in Projects) p.IsSelected = true;
    }

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

        StartTaskInProjects(prompt, GetSelectedProjectPaths());
        NewTaskPrompt = string.Empty;
    }

    /// <summary>
    /// Creates one task per project for the same <paramref name="prompt"/> and starts them all
    /// at once. Each task is a fully independent session confined to its own project folder, so
    /// they run in parallel and one failing or being cancelled never affects the others.
    /// </summary>
    public IReadOnlyList<BackgroundTaskItemViewModel> StartTaskInProjects(string prompt, IEnumerable<string> projectPaths)
    {
        var paths = projectPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var batchId = paths.Count > 1 ? Guid.NewGuid().ToString("N")[..8] : string.Empty;

        var started = new List<BackgroundTaskItemViewModel>(paths.Count);
        foreach (var path in paths)
        {
            started.Add(StartTask(prompt, new AgentOptions
            {
                WorkingDirectory = path,
                // Confine the task to its own project so a parallel task can't touch another project's files.
                AllowedPaths = new List<string> { path },
                // Background tasks have no one watching to answer an approval
                // prompt, so "Ask" would just hang forever. AutoEdit lets the
                // task actually make file edits while still not auto-running
                // arbitrary shell commands unattended.
                PermissionMode = AiCodeAgent.Core.Models.PermissionMode.AutoEdit
            }, batchId));
        }
        return started;
    }

    /// <summary>
    /// Kicks off <paramref name="prompt"/> as a detached task: returns
    /// immediately, the task runs (and can be watched or cancelled) in the
    /// panel. <paramref name="baseOptions"/> supplies working directory,
    /// permission mode, etc. — the same options the caller would otherwise
    /// pass to a normal foreground chat turn.
    /// </summary>
    public BackgroundTaskItemViewModel StartTask(string prompt, AgentOptions baseOptions, string batchId = "")
    {
        var sessionId = "bg-" + Guid.NewGuid().ToString("N")[..8];
        var item = new BackgroundTaskItemViewModel
        {
            SessionId = sessionId,
            Prompt = prompt,
            ProjectPath = baseOptions.WorkingDirectory ?? string.Empty,
            BatchId = batchId
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
