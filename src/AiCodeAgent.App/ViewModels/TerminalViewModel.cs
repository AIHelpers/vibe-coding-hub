using AiCodeAgent.Core.Terminal;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AiCodeAgent.App.ViewModels;

public partial class TerminalEntry
{
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Type { get; set; } = "info"; // info, command, output, error, system
}

/// <summary>
/// The terminal panel: any number of interactive shell tabs (each a real shell on a pseudo-terminal, started in the
/// working directory) and one fixed "Agent output" tab with the log of what the agent ran.
/// </summary>
public partial class TerminalViewModel : ObservableObject, IDisposable
{
    private const int MinHeight = 120;
    private const int MaxHeight = 800;

    private readonly Func<PtyStartOptions, IPtyProcess> _ptyFactory;
    private readonly Action<Action> _post;
    private Func<string?> _shellProvider = () => null;
    private Func<string> _directoryProvider = Directory.GetCurrentDirectory;
    private int _shellCounter;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private double _terminalHeight = 280;

    /// <summary>The agent is running a command right now (shown next to the tabs).</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedShell))]
    [NotifyPropertyChangedFor(nameof(IsShellSelected))]
    [NotifyPropertyChangedFor(nameof(IsAgentSelected))]
    private TerminalTabViewModel? _selectedTab;

    public TerminalViewModel() : this(PtyProcess.Start, action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <summary>For tests: a fake PTY factory and a synchronous "post to the UI thread".</summary>
    internal TerminalViewModel(Func<PtyStartOptions, IPtyProcess> ptyFactory, Action<Action> post)
    {
        _ptyFactory = ptyFactory;
        _post = post;
        AgentTab = new AgentOutputTabViewModel();
        Tabs.Add(AgentTab);
        _selectedTab = AgentTab;
        Entries.Add(new TerminalEntry
        {
            Text = "What the agent runs shows up here. Use the Shell tab to run your own commands.",
            Type = "system",
            Timestamp = DateTime.Now
        });
    }

    /// <summary>Shell tabs in the order they were opened, then the Agent output tab.</summary>
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    public AgentOutputTabViewModel AgentTab { get; }

    /// <summary>The log of the Agent output tab.</summary>
    public ObservableCollection<TerminalEntry> Entries { get; } = new();

    public ShellTabViewModel? SelectedShell => SelectedTab as ShellTabViewModel;

    public bool IsShellSelected => SelectedTab is ShellTabViewModel;

    public bool IsAgentSelected => SelectedTab is AgentOutputTabViewModel;

    /// <summary>Raised when the panel opens or a tab is selected, so the view can give the terminal the keyboard focus.</summary>
    public event Action? FocusRequested;

    /// <summary>The user wants the selected text in the chat input.</summary>
    public event Action<string>? SendToChatRequested;

    /// <summary>The user asks the agent about the shown output (an error).</summary>
    public event Action<string>? AskAgentRequested;

    /// <summary>Where the settings and the project folder come from: the configured shell command line and the working directory.</summary>
    public void Configure(Func<string?> shellProvider, Func<string> workingDirectoryProvider)
    {
        _shellProvider = shellProvider;
        _directoryProvider = workingDirectoryProvider;
    }

    [RelayCommand]
    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (!value) return;
        // The first time the panel opens it brings a shell with it.
        if (!Tabs.OfType<ShellTabViewModel>().Any()) NewShell();
        else FocusRequested?.Invoke();
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        if (value is ShellTabViewModel) FocusRequested?.Invoke();
    }

    /// <summary>Opens a new shell tab in the working directory and selects it.</summary>
    [RelayCommand]
    private void NewShell()
    {
        string directory;
        try { directory = _directoryProvider(); }
        catch { directory = Directory.GetCurrentDirectory(); }
        if (!Directory.Exists(directory)) directory = Directory.GetCurrentDirectory();

        var shell = ShellLocator.Resolve(_shellProvider());
        var name = Path.GetFileNameWithoutExtension(shell.FileName);
        if (string.IsNullOrEmpty(name)) name = "Shell";
        _shellCounter++;
        var tab = new ShellTabViewModel(_shellCounter == 1 ? name : name + " " + _shellCounter, shell, directory, 100, 28, _ptyFactory, _post);
        tab.Start();
        Tabs.Insert(Tabs.Count - 1, tab); // before the pinned Agent output tab
        SelectedTab = tab;
        if (!IsVisible) IsVisible = true;
    }

    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab is not ShellTabViewModel shell) return;
        var index = Tabs.IndexOf(shell);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        shell.Dispose();
        if (ReferenceEquals(SelectedTab, shell))
            SelectedTab = Tabs[Math.Max(0, index - 1)]; // the tab to the left, like a browser
    }

    [RelayCommand]
    private void Close() => IsVisible = false;

    [RelayCommand]
    private void Clear()
    {
        if (SelectedTab is ShellTabViewModel shell)
        {
            shell.ClearScreen();
            return;
        }

        Entries.Clear();
        Entries.Add(new TerminalEntry { Text = "Cleared.", Type = "system", Timestamp = DateTime.Now });
    }

    /// <summary>Drag handle of the panel: resizes within sane limits.</summary>
    public void ResizeBy(double delta) =>
        TerminalHeight = Math.Clamp(TerminalHeight + delta, MinHeight, MaxHeight);

    public void SendSelectionToChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SendToChatRequested?.Invoke(text.TrimEnd());
    }

    /// <summary>Asks the agent about the selected text; without a selection, about the last lines of the shell output.</summary>
    public void AskAgentAbout(string? selection)
    {
        var text = selection;
        if (string.IsNullOrWhiteSpace(text) && SelectedShell != null) text = SelectedShell.LastOutput();
        if (string.IsNullOrWhiteSpace(text)) return;
        AskAgentRequested?.Invoke(text.TrimEnd());
    }

    private const int MaxEntries = 2000;
    private const int MaxOutputChars = 4000;

    public void AppendOutput(string text, string type = "output", bool isError = false)
    {
        Entries.Add(new TerminalEntry { Text = text, Type = type, IsError = isError, Timestamp = DateTime.Now });
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    /// <summary>The agent starts a shell command: shown in the Agent output tab.</summary>
    public void AgentCommandStarted(string command)
    {
        AppendCommand(command);
        IsRunning = true;
    }

    /// <summary>The agent's command ended; the end of its output is kept in the log.</summary>
    public void AgentCommandFinished(string? output, bool isError)
    {
        IsRunning = false;
        if (string.IsNullOrWhiteSpace(output)) return;
        var text = output.TrimEnd();
        if (text.Length > MaxOutputChars) text = "\u2026" + text[^MaxOutputChars..];
        AppendOutput(text, isError ? "error" : "output", isError);
    }

    public void AppendCommand(string command)
    {
        Entries.Add(new TerminalEntry { Text = $"> {command}", Type = "command", Timestamp = DateTime.Now });
    }

    /// <summary>Ends every shell. Called when the app closes.</summary>
    public void Dispose()
    {
        foreach (var shell in Tabs.OfType<ShellTabViewModel>().ToList()) shell.Dispose();
        GC.SuppressFinalize(this);
    }
}
