using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

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

    public MainViewModel(
        IServiceProvider serviceProvider,
        FileExplorerViewModel fileExplorer,
        TerminalViewModel terminal,
        EditorPaneViewModel editorPane,
        CheckpointBrowserViewModel checkpointBrowser)
    {
        _serviceProvider = serviceProvider;
        FileExplorer = fileExplorer;
        Terminal = terminal;
        EditorPane = editorPane;
        CheckpointBrowser = checkpointBrowser;

        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Default to Chat view
        _currentViewModel ??= CreateChatViewModel();

        // Load file explorer
        FileExplorer.LoadCommand.Execute(null);

        // Load checkpoints
        CheckpointBrowser.RefreshCommand.Execute(null);

        // Update status
        UpdateFileExplorerStatus();

        // Wire file explorer file-click to editor
        FileExplorer.PropertyChanged += OnFileExplorerPropertyChanged;
    }

    private void OnFileExplorerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileExplorerViewModel.SelectedItem))
        {
            var selected = FileExplorer.SelectedItem;
            if (selected != null && !selected.IsDirectory)
            {
                _ = EditorPane.OpenFileAsync(selected.FullPath);
                IsCheckpointBrowserOpen = false;
            }
        }
    }

    private ChatViewModel CreateChatViewModel()
    {
        _chatViewModel = _serviceProvider.GetRequiredService<ChatViewModel>();
        return _chatViewModel;
    }

    [RelayCommand]
    private void NavigateToChat()
    {
        _chatViewModel = _serviceProvider.GetRequiredService<ChatViewModel>();
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
}