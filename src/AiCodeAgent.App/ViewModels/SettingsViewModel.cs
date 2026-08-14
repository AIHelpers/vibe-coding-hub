using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Providers;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private IStorageProvider? _storageProvider;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Allows the host window to inject its storage provider after construction
    /// so the folder-picker button works (DI registers this VM with a null provider).
    /// </summary>
    public IStorageProvider? StorageProvider
    {
        get => _storageProvider;
        set => _storageProvider = value;
    }
    private CancellationTokenSource? _loadModelsCts;
    private bool _modelsLoadedForCurrentProvider;

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

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private string _modelsStatus = string.Empty;

    public ObservableCollection<string> AvailableProviders { get; } = new();

    public ObservableCollection<string> AvailableModels { get; } = new();

    public SettingsViewModel(
        ConfigurationService configurationService,
        IStorageProvider? storageProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        _configurationService = configurationService;
        _storageProvider = storageProvider;
        _loggerFactory = loggerFactory;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var config = _configurationService.Config;

        // Populate providers from configuration (supports custom local providers)
        AvailableProviders.Clear();
        foreach (var name in config.Providers.Keys)
            AvailableProviders.Add(name);

        SelectedProvider = config.DefaultProvider.ToLowerInvariant();
        LoadProviderSettings(config.DefaultProvider);

        WorkingDirectory = !string.IsNullOrWhiteSpace(config.Agent?.WorkingDirectory)
            ? config.Agent.WorkingDirectory
            : Directory.GetCurrentDirectory();
        AutoApprove = config.Agent?.AutoApprove ?? false;

        // Models are loaded lazily — only when the user opens the model
        // dropdown to choose a model (see EnsureModelsLoaded command).
        // This avoids reloading the list every time the Settings page opens.
        _modelsLoadedForCurrentProvider = false;
    }

    partial void OnSelectedProviderChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            // Invalidate the cached list when the provider changes; the list
            // will be (re)loaded lazily when the user opens the dropdown.
            _modelsLoadedForCurrentProvider = false;
            AvailableModels.Clear();
            ModelsStatus = string.Empty;
            LoadProviderSettings(value);
        }
    }

    /// <summary>
    /// Loads the model list for the current provider only if it hasn't been
    /// loaded yet. Intended to be invoked when the user opens the model
    /// dropdown to choose a model — avoids reloading on every Settings open.
    /// </summary>
    [RelayCommand]
    public Task EnsureModelsLoaded()
    {
        if (_modelsLoadedForCurrentProvider)
            return Task.CompletedTask;
        return LoadModelsAsync(SelectedProvider);
    }

    private void LoadProviderSettings(string providerName)
    {
        if (_configurationService.Config.Providers.TryGetValue(
                providerName.ToLowerInvariant(), out var provider))
        {
            Model = provider.DefaultModel ?? "gpt-4o";
            BaseUrl = provider.BaseUrl ?? string.Empty;
            VerifySsl = provider.VerifySsl;

            // Seed the model list with the currently-configured model so the
            // ComboBox can display the saved selection without triggering a
            // network load. The full list is fetched lazily when the user opens
            // the dropdown (see EnsureModelsLoaded).
            AvailableModels.Clear();
            if (!string.IsNullOrEmpty(Model))
                AvailableModels.Add(Model);
            _modelsLoadedForCurrentProvider = false;

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

    /// <summary>
    /// Queries the selected provider for its available models and populates
    /// the <see cref="AvailableModels"/> collection. Runs on a background
    /// thread; failures fall back to the provider's static SupportedModels.
    /// A 15-second timeout prevents long hangs when a local provider is offline.
    /// </summary>
    private async Task LoadModelsAsync(string providerName)
    {
        // Cancel any in-flight request
        _loadModelsCts?.Cancel();
        _loadModelsCts = new CancellationTokenSource();
        var token = _loadModelsCts.Token;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoadingModels = true;
            ModelsStatus = "Loading models…";
        });

        try
        {
            var config = _configurationService.Config;
            if (!config.Providers.TryGetValue(providerName.ToLowerInvariant(), out var providerConfig))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AvailableModels.Clear();
                    ModelsStatus = string.Empty;
                });
                return;
            }

            // Build a temporary provider instance to query models. We use
            // CreateOrFallback so a misconfigured provider doesn't throw.
            IAiProvider provider;
            ILoggerFactory lf;
            if (_loggerFactory != null)
            {
                lf = _loggerFactory;
                provider = ProviderFactory.CreateOrFallback(
                    providerName, providerConfig, _loggerFactory);
            }
            else
            {
                // Without a logger factory, create a minimal one on the fly.
                lf = LoggerFactory.Create(b => { });
                provider = ProviderFactory.CreateOrFallback(providerName, providerConfig, lf);
            }

            // Apply a 15-second timeout so unreachable local providers don't
            // leave the spinner running for minutes (default HttpClient timeout
            // can be 300s).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            string[] models;
            try
            {
                models = await provider.GetAvailableModelsAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timeout, not cancellation from a newer request — fall back.
                models = provider.SupportedModels;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ModelsStatus = "Timed out — showing fallback models");
            }

            if (token.IsCancellationRequested)
                return;

            // Marshal collection updates to the UI thread — Avalonia requires
            // ObservableCollection changes to happen on the dispatcher thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableModels.Clear();
                foreach (var m in models)
                    AvailableModels.Add(m);

                // Ensure the current Model is in the list; if not, prepend it so
                // the user can still see/keep their configured value.
                if (!string.IsNullOrEmpty(Model) && !AvailableModels.Contains(Model))
                    AvailableModels.Insert(0, Model);

                ModelsStatus = AvailableModels.Count > 0
                    ? $"{AvailableModels.Count} models available"
                    : "No models found";
                _modelsLoadedForCurrentProvider = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; ignore.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load models for {providerName}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
                ModelsStatus = "Failed to load models");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    IsLoadingModels = false);
            }
        }
    }

    [RelayCommand]
    private Task RefreshModels()
    {
        return LoadModelsAsync(SelectedProvider);
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

        // Normalize to lowercase so DefaultProvider always matches the provider key casing
        config.DefaultProvider = SelectedProvider.ToLowerInvariant();
        config.Agent ??= new AgentConfiguration();
        config.Agent.AutoApprove = AutoApprove;
        config.Agent.WorkingDirectory = WorkingDirectory;

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