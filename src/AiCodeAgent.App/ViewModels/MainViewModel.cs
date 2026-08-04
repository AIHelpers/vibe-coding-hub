using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.CommandPalette;

namespace AiCodeAgent.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private bool _isFileExplorerOpen = true;

    [ObservableProperty]
    private string _fileExplorerStatus = string.Empty;

    [ObservableProperty]
    private bool _isSettingsMode;

    [ObservableProperty]
    private bool _isCheckpointBrowserOpen;

    private ChatViewModel? _chatViewModel;
    private readonly IServiceProvider _serviceProvider;

    public ChatViewModel? ChatViewModel => _chatViewModel;
    public FileExplorerViewModel FileExplorer { get; }
    public TerminalViewModel Terminal { get; }
    public EditorPaneViewModel EditorPane { get; }
    public CheckpointBrowserViewModel CheckpointBrowser { get; }
    public CommandPaletteViewModel CommandPalette { get; }

    public MainViewModel(
        IServiceProvider serviceProvider,
        FileExplorerViewModel fileExplorer,
        TerminalViewModel terminal,
        EditorPaneViewModel editorPane,
        CheckpointBrowserViewModel checkpointBrowser,
        CommandPaletteViewModel commandPalette)
    {
        _serviceProvider = serviceProvider;
        FileExplorer = fileExplorer;
        Terminal = terminal;
        EditorPane = editorPane;
        CheckpointBrowser = checkpointBrowser;
        CommandPalette = commandPalette;

        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Default to Chat view (reuse the same instance, don't create a new one)
        _currentViewModel ??= GetOrCreateChatViewModel();

        // Register command palette entries (navigation + slash commands + editor actions)
        RegisterPaletteEntries();

        // Load file explorer
        FileExplorer.LoadCommand.Execute(null);

        // Load checkpoints
        CheckpointBrowser.RefreshCommand.Execute(null);

        // Update status
        UpdateFileExplorerStatus();

        // Wire file explorer file-click to editor
        FileExplorer.PropertyChanged += OnFileExplorerPropertyChanged;
    }

    /// <summary>
    /// Unsubscribes event handlers to prevent leaks when the view-model is no longer needed.
    /// </summary>
    public void Dispose()
    {
        FileExplorer.PropertyChanged -= OnFileExplorerPropertyChanged;
    }

    private void OnFileExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileExplorerViewModel.SelectedItem))
        {
            var selected = FileExplorer.SelectedItem;
            if (selected != null && !selected.IsDirectory)
            {
                SafeFireAndForget(EditorPane.OpenFileAsync(selected.FullPath), "OpenFile");
                IsCheckpointBrowserOpen = false;
            }
        }
    }

    [RelayCommand]
    private void NavigateToChat()
    {
        // Reuse the existing ChatViewModel instance instead of creating a new one,
        // so the conversation history is preserved across navigation.
        _chatViewModel ??= _serviceProvider.GetRequiredService<ChatViewModel>();
        CurrentViewModel = _chatViewModel;
        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        StatusText = "Chat Mode";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
        IsSettingsMode = true;
        IsCheckpointBrowserOpen = false;
        StatusText = "Settings Mode";
    }

    [RelayCommand]
    private void ToggleCommandPalette()
    {
        if (CommandPalette.IsOpen)
        {
            CommandPalette.Close();
        }
        else
        {
            CommandPalette.Open();
        }
    }

    [RelayCommand]
    private void ToggleFileExplorer()
    {
        IsFileExplorerOpen = !IsFileExplorerOpen;
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        Terminal.ToggleVisibilityCommand.Execute(null);
    }

    [RelayCommand]
    private void ToggleCheckpointBrowser()
    {
        IsCheckpointBrowserOpen = !IsCheckpointBrowserOpen;
        if (IsCheckpointBrowserOpen)
        {
            IsSettingsMode = false;
            CheckpointBrowser.RefreshCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void RefreshFileExplorer()
    {
        FileExplorer.RefreshCommand.Execute(null);
        UpdateFileExplorerStatus();
    }

    private void UpdateFileExplorerStatus()
    {
        var fileCount = FileExplorer.RootItems.Count > 0
            ? CountFiles(FileExplorer.RootItems[0])
            : 0;
        FileExplorerStatus = $"📁 {fileCount} files";
    }

    private static int CountFiles(FileExplorerItem item)
    {
        int count = 0;
        foreach (var child in item.Children)
        {
            if (!child.IsDirectory)
                count++;
            else
                count += CountFiles(child);
        }
        return count;
    }

    /// <summary>
    /// Populates the command palette registry with entries from each feature area.
    /// Slash-command entries invoke the same handlers the chat slash-command UI uses.
    /// </summary>
    private void RegisterPaletteEntries()
    {
        var registry = _serviceProvider.GetRequiredService<CommandPaletteRegistry>();
        var chat = GetOrCreateChatViewModel();

        var entries = new List<ICommandPaletteEntry>
        {
            // ---- Navigation ----
            new CommandPaletteEntry
            {
                Id = "nav.chat",
                Title = "Go to Chat",
                Category = "Navigation",
                Keywords = new[] { "chat", "conversation", "messages" },
                KeybindingHint = "",
                Action = NavigateToChat
            },
            new CommandPaletteEntry
            {
                Id = "nav.settings",
                Title = "Open Settings",
                Category = "Navigation",
                Keywords = new[] { "settings", "options", "configuration", "preferences" },
                KeybindingHint = "Ctrl+,",
                Action = NavigateToSettings
            },
            new CommandPaletteEntry
            {
                Id = "nav.checkpointBrowser",
                Title = "Toggle Checkpoint Browser",
                Category = "Navigation",
                Keywords = new[] { "checkpoint", "restore", "history", "snapshots" },
                KeybindingHint = "",
                Action = ToggleCheckpointBrowser
            },
            new CommandPaletteEntry
            {
                Id = "nav.fileExplorer",
                Title = "Toggle File Explorer",
                Category = "Navigation",
                Keywords = new[] { "files", "explorer", "sidebar", "tree" },
                KeybindingHint = "Ctrl+B",
                Action = ToggleFileExplorer
            },
            new CommandPaletteEntry
            {
                Id = "nav.terminal",
                Title = "Toggle Terminal",
                Category = "Navigation",
                Keywords = new[] { "terminal", "console", "shell", "command" },
                KeybindingHint = "Ctrl+`",
                Action = ToggleTerminal
            },

            // ---- Chat Slash Commands (invoke the same handlers as the input popup) ----
            CreateSlashCommandEntry("slash.edit", "/edit", "Edit a specific file", chat, "editing"),
            CreateSlashCommandEntry("slash.search", "/search", "Search the codebase", chat, "searching"),
            CreateSlashCommandEntry("slash.explain", "/explain", "Explain code logic", chat, "explanation"),
            CreateSlashCommandEntry("slash.test", "/test", "Generate tests", chat, "testing"),
            CreateSlashCommandEntry("slash.fix", "/fix", "Fix issues in code", chat, "fixing"),
            CreateSlashCommandEntry("slash.refactor", "/refactor", "Refactor code", chat, "refactoring"),
            CreateSlashCommandEntry("slash.help", "/help", "Show available commands", chat, "help", "commands"),

            // ---- Editor ----
            new CommandPaletteEntry
            {
                Id = "editor.save",
                Title = "Save File",
                Category = "Editor",
                Keywords = new[] { "save", "write", "persist" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.SaveActiveAsync(), "SaveActive")
            },
            new CommandPaletteEntry
            {
                Id = "editor.saveAll",
                Title = "Save All Files",
                Category = "Editor",
                Keywords = new[] { "save", "write", "all", "persist" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.SaveAllAsync(), "SaveAll")
            },
            new CommandPaletteEntry
            {
                Id = "editor.acceptAllHunks",
                Title = "Accept All Diff Hunks",
                Category = "Editor",
                Keywords = new[] { "diff", "accept", "hunks", "apply", "changes" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.AcceptAllAsync(), "AcceptAll")
            },
            new CommandPaletteEntry
            {
                Id = "editor.rejectAllHunks",
                Title = "Reject All Diff Hunks",
                Category = "Editor",
                Keywords = new[] { "diff", "reject", "hunks", "discard", "changes" },
                KeybindingHint = "",
                Action = () => EditorPane.RejectAll()
            },
            new CommandPaletteEntry
            {
                Id = "editor.closeActiveTab",
                Title = "Close Current File",
                Category = "Editor",
                Keywords = new[] { "close", "tab", "file" },
                KeybindingHint = "",
                Action = () =>
                {
                    var tab = EditorPane.ActiveTab;
                    if (tab != null)
                    {
                        SafeFireAndForget(EditorPane.CloseTabAsync(tab), "CloseTab");
                    }
                }
            }
        };

        registry.Register(entries);
    }

    /// <summary>
    /// Creates a command palette entry that navigates to the chat view and
    /// inserts the given slash command into the chat input — mirroring the
    /// behaviour of the in-chat slash-command popup.
    /// </summary>
    private CommandPaletteEntry CreateSlashCommandEntry(
        string id,
        string command,
        string description,
        ChatViewModel chat,
        params string[] keywords)
    {
        return new CommandPaletteEntry
        {
            Id = id,
            Title = $"Run {command}",
            Category = "Commands",
            Keywords = new[] { command[1..], description }
                .Concat(keywords)
                .ToArray(),
            KeybindingHint = command,
            Action = () =>
            {
                // Navigate to chat view first so the user sees the command.
                CurrentViewModel = chat;
                IsSettingsMode = false;
                IsCheckpointBrowserOpen = false;
                StatusText = "Chat Mode";

                // Insert the slash command into the input, exactly as the
                // chat slash-command popup does.
                chat.InputText = command + " ";
            }
        };
    }

    private ChatViewModel GetOrCreateChatViewModel()
    {
        if (_chatViewModel == null)
        {
            _chatViewModel = _serviceProvider.GetRequiredService<ChatViewModel>();
        }
        return _chatViewModel;
    }

    /// <summary>
    /// Awaits a fire-and-forget task, logging any unhandled exceptions so they
    /// are not silently swallowed by the task scheduler.
    /// </summary>
    private static void SafeFireAndForget(Task task, string operation)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Fire-and-forget operation '{operation}' failed: {t.Exception.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
    }
}
