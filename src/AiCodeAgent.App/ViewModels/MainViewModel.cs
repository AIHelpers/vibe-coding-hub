using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace AiCodeAgent.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    private ChatViewModel? _chatViewModel;
    private readonly IServiceProvider _serviceProvider;

    public ChatViewModel? ChatViewModel => _chatViewModel;

    public MainViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Default to Chat view
        _currentViewModel ??= CreateChatViewModel();
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
        StatusText = "Chat Mode";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
        StatusText = "Settings Mode";
    }
}