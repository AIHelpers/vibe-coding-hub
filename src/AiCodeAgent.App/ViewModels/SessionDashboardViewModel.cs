using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiCodeAgent.Core.Agent;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Dashboard binding source for the multi-agent session dashboard (Feature 5).
/// Surfaces the <see cref="SessionManager"/> registry as observable lists and
/// provides commands to create/switch/pause/resume/cancel sessions.
/// </summary>
public partial class SessionDashboardViewModel : ObservableObject
{
    private readonly SessionManager _manager;

    /// <summary>Observable session list (mirrors <see cref="SessionManager.Sessions"/>).</summary>
    public ObservableCollection<AgentSession> Sessions => _manager.Sessions;

    [ObservableProperty]
    private AgentSession? _activeSession;

    [ObservableProperty]
    private string _newTask = string.Empty;

    [ObservableProperty]
    private string _newScope = string.Empty;

    public SessionDashboardViewModel(SessionManager manager)
    {
        _manager = manager;
        _manager.ActiveSessionChanged += OnActiveSessionChanged;
        // initialize active session
        ActiveSession = _manager.ActiveSession;
    }

    private void OnActiveSessionChanged(string? id)
    {
        ActiveSession = id == null ? null : _manager.GetSession(id);
    }

    /// <summary>Create a new session with the current task/scope and focus it.</summary>
    [RelayCommand]
    private void CreateSession()
    {
        var task = NewTask?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(task)) return;
        var session = _manager.CreateSession(task);

        // apply optional scope (newline- or semicolon-separated paths)
        if (!string.IsNullOrWhiteSpace(NewScope))
        {
            foreach (var p in NewScope.Split(';', '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                session.FileScope.AddPath(p);
        }

        _manager.SetActive(session.Id);
        NewTask = string.Empty;
        NewScope = string.Empty;
    }

    /// <summary>Switch focus to the given session.</summary>
    [RelayCommand]
    private void SelectSession(string? id)
    {
        if (!string.IsNullOrEmpty(id))
            _manager.SetActive(id);
    }

    /// <summary>Pause the given session.</summary>
    [RelayCommand]
    private void PauseSession(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _manager.Pause(id);
    }

    /// <summary>Resume the given session.</summary>
    [RelayCommand]
    private void ResumeSession(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _manager.Resume(id);
    }

    /// <summary>Cancel the given session.</summary>
    [RelayCommand]
    private void CancelSession(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _manager.Cancel(id);
    }

    /// <summary>Remove the given session from the dashboard.</summary>
    [RelayCommand]
    private void CloseSession(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _manager.RemoveSession(id);
    }

    /// <summary>Cancel all running/paused sessions.</summary>
    [RelayCommand]
    private void CancelAll()
    {
        _manager.CancelAll();
    }

    /// <summary>
    /// Refreshes the active session reference from the manager. The session
    /// list is already observable via <see cref="Sessions"/>, so this is a
    /// no-op sync hook used by the host when the dashboard is opened.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        ActiveSession = _manager.ActiveSession;
    }
}
