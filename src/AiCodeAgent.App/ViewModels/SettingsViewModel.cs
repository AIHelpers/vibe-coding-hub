using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly IStorageProvider? _storageProvider;

    [ObservableProperty]
    private string _selectedProvider = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private bool _autoApprove;

    public ObservableCollection<string> AvailableProviders { get; } = new()
    {
        "OpenAI",
        "Anthropic",
        "AzureOpenAI",
        "GoogleGemini",
        "Ollama"
    };

    public SettingsViewModel(ConfigurationService configurationService, IStorageProvider? storageProvider = null)
    {
        _configurationService = configurationService;
        _storageProvider = storageProvider;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var config = _configurationService.Config;

        SelectedProvider = config.DefaultProvider;
        
        // Get model from provider config
        if (config.Providers.TryGetValue(config.DefaultProvider.ToLower(), out var provider))
        {
            Model = provider.DefaultModel ?? "gpt-4o";
        }
        else
        {
            Model = "gpt-4o";
        }
        
        WorkingDirectory = Directory.GetCurrentDirectory();
        AutoApprove = config.Agent?.AutoApprove ?? false;

        // Try to get API key from environment or config
        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    }

    [RelayCommand]
    private async Task BrowseDirectory()
    {
        if (_storageProvider == null) return;

        var result = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Working Directory",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            WorkingDirectory = result[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        var config = _configurationService.Config;

        config.DefaultProvider = SelectedProvider;
        config.Agent ??= new AgentConfiguration();
        config.Agent.AutoApprove = AutoApprove;

        // Update provider config with model using with expression for record
        var providerKey = SelectedProvider.ToLower();
        if (config.Providers.TryGetValue(providerKey, out var existingProvider))
        {
            config.Providers[providerKey] = existingProvider with { DefaultModel = Model };
        }
        else
        {
            config.Providers[providerKey] = new ProviderConfiguration 
            { 
                Name = providerKey, 
                DefaultModel = Model 
            };
        }

        if (!string.IsNullOrEmpty(ApiKey))
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", ApiKey);
        }

        await _configurationService.SaveAsync();
    }
}
