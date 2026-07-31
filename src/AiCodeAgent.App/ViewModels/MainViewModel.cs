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

    private readonly IServiceProvider _serviceProvider;

    public MainViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Default to Chat view - use lazy initialization to avoid eager DI resolution
        // that can crash the app if dependencies aren't ready
        _currentViewModel ??= CreateChatViewModel();
    }

    private ChatViewModel CreateChatViewModel()
    {
        return _serviceProvider.GetRequiredService<ChatViewModel>();
    }

    [RelayCommand]
    private void NavigateToChat()
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<ChatViewModel>();
        StatusText = "Chat Mode";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
        StatusText = "Settings Mode";
    }
}