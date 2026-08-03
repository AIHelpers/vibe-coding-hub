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
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private bool _autoApprove;

    [ObservableProperty]
    private bool _verifySsl = true;

    public ObservableCollection<string> AvailableProviders { get; } = new();

    public SettingsViewModel(ConfigurationService configurationService, IStorageProvider? storageProvider = null)
    {
        _configurationService = configurationService;
        _storageProvider = storageProvider;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var config = _configurationService.Config;

        // Populate providers from configuration (supports custom local providers)
        AvailableProviders.Clear();
        foreach (var name in config.Providers.Keys)
            AvailableProviders.Add(name);

        SelectedProvider = config.DefaultProvider;
        LoadProviderSettings(config.DefaultProvider);
        
        WorkingDirectory = Directory.GetCurrentDirectory();
        AutoApprove = config.Agent?.AutoApprove ?? false;
    }

    partial void OnSelectedProviderChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            LoadProviderSettings(value);
    }

    private void LoadProviderSettings(string providerName)
    {
        if (_configurationService.Config.Providers.TryGetValue(
                providerName.ToLowerInvariant(), out var provider))
        {
            Model = provider.DefaultModel ?? "gpt-4o";
            BaseUrl = provider.BaseUrl ?? string.Empty;
            VerifySsl = provider.VerifySsl;

            // Try to get API key from environment or config
            var envKeyNew = $"AI_CODE_AGENT_{providerName.ToUpperInvariant()}_API_KEY";
            var envKeyLegacy = $"AIAGENT_{providerName.ToUpperInvariant()}_API_KEY";
            ApiKey = Environment.GetEnvironmentVariable(envKeyNew)
                  ?? Environment.GetEnvironmentVariable(envKeyLegacy)
                  ?? provider.ApiKey ?? string.Empty;
        }
        else
        {
            Model = "gpt-4o";
            BaseUrl = string.Empty;
            ApiKey = string.Empty;
            VerifySsl = true;
        }
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

        // Update provider config with model, URL, and API key
        var providerKey = SelectedProvider.ToLowerInvariant();
        var existing = config.Providers.TryGetValue(providerKey, out var current)
            ? current
            : null;

        config.Providers[providerKey] = new ProviderConfiguration
        {
            Name = providerKey,
            BaseUrl = string.IsNullOrEmpty(BaseUrl) && existing != null
                ? existing.BaseUrl
                : BaseUrl,
            DefaultModel = Model,
            ApiKey = !string.IsNullOrEmpty(ApiKey) ? ApiKey : existing?.ApiKey,
            TimeoutSeconds = existing?.TimeoutSeconds ?? 300,
            VerifySsl = VerifySsl,
            Headers = existing?.Headers ?? new()
        };

        await _configurationService.SaveAsync();
    }
}