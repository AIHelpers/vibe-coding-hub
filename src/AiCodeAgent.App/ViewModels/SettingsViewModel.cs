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

    /// <summary>Command line of the shell the terminal starts; empty = the default for this computer.</summary>
    [ObservableProperty]
    private string _terminalShell = string.Empty;

    /// <summary>Says which shell is used while <see cref="TerminalShell"/> is empty.</summary>
    public string DefaultTerminalShell => "Default on this computer: " + AiCodeAgent.Core.Terminal.ShellLocator.Resolve(null).Display;

    /// <summary>Theme choices shown in Settings: "System", "Light" or "Dark".</summary>
    public string[] ThemeOptions { get; } = { "System", "Light", "Dark" };

    [ObservableProperty]
    private string _selectedTheme = "Dark";

    /// <summary>Requirements questions: "Off" never asks, "Auto" asks before a new app, "Always" asks before every build task.</summary>
    public string[] RequirementsClarifierOptions { get; } = { "Off", "Auto", "Always" };

    [ObservableProperty]
    private string _selectedRequirementsClarifier = "Auto";

    /// <summary>On start: "Ask" shows the Resume / New banner, "Always" continues the last session, "Never" starts empty.</summary>
    public string[] ResumeLastSessionOptions { get; } = { "Ask", "Always", "Never" };

    [ObservableProperty]
    private string _selectedResumeLastSession = "Ask";

    /// <summary>Look for a newer release when the app starts (at most once a day).</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    // Preview the theme immediately; it is persisted by Save.
    partial void OnSelectedThemeChanged(string value) => AiCodeAgent.App.Services.ThemeService.Apply(value);

    [ObservableProperty]
    private bool _verifySsl = true;

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private string _modelsStatus = string.Empty;

    public ObservableCollection<string> AvailableProviders { get; } = new();

    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>The models the picker shows: the account's list merged with <c>models.json</c>, retired ones greyed out.</summary>
    public ObservableCollection<ModelOption> ModelOptions { get; } = new();

    [ObservableProperty]
    private ModelOption? _selectedModelOption;

    /// <summary>Shown under the picker when the chosen model is retired (it would fail with API errors).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRetiredModelWarning))]
    private string _retiredModelWarning = string.Empty;

    public bool HasRetiredModelWarning => RetiredModelWarning.Length > 0;

    private bool _syncingModelSelection;

    private static ModelCatalog Catalog => ModelCatalogLoader.Default;

    /// <summary>Keeps <see cref="Model"/> and the picker's selected item in step and updates the retired warning.</summary>
    partial void OnModelChanged(string value)
    {
        UpdateRetiredWarning();
        if (_syncingModelSelection) return;
        _syncingModelSelection = true;
        try
        {
            SelectedModelOption = ModelOptions.FirstOrDefault(o => string.Equals(o.Id, value, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingModelSelection = false; }
    }

    partial void OnSelectedModelOptionChanged(ModelOption? value)
    {
        if (_syncingModelSelection || value == null) return;
        _syncingModelSelection = true;
        try { Model = value.Id; }
        finally { _syncingModelSelection = false; }
        UpdateRetiredWarning();
    }

    private void UpdateRetiredWarning() =>
        RetiredModelWarning = !string.IsNullOrEmpty(Model) && Catalog.IsRetired(SelectedProvider, Model)
            ? $"⚠ {Model} is retired and will fail with API errors. Pick another model."
            : string.Empty;

    /// <summary>Shows just the configured model until the real list is loaded.</summary>
    private void SeedModelOptions()
    {
        ModelOptions.Clear();
        if (!string.IsNullOrEmpty(Model))
        {
            var meta = Catalog.Lookup(SelectedProvider, Model);
            ModelOptions.Add(new ModelOption(Model, meta?.Tier, meta?.Retired ?? false, meta?.ContextWindow));
        }
        _syncingModelSelection = true;
        try { SelectedModelOption = ModelOptions.FirstOrDefault(); }
        finally { _syncingModelSelection = false; }
    }

    public SettingsViewModel(
        ConfigurationService configurationService,
        IStorageProvider? storageProvider = null,
        ILoggerFactory? loggerFactory = null,
        SettingsEnvironment? environment = null)
    {
        _env = environment ?? new SettingsEnvironment();
        _configurationService = configurationService;
        _storageProvider = storageProvider;
        _loggerFactory = loggerFactory;
        LoadSettings();
    }

    private static string ThemeLabel(string? mode) => AiCodeAgent.App.Services.ThemeService.Normalize(mode) switch
    {
        "light" => "Light",
        "system" => "System",
        _ => "Dark"
    };

    private void LoadSettings()
    {
        var config = _configurationService.Config;

        // Populate providers from configuration (supports custom local providers)
        AvailableProviders.Clear();
        foreach (var name in config.Providers.Keys)
            AvailableProviders.Add(name);
        // E8-S1: the scripted test provider is offered only when AIAGENT_ENABLE_SCRIPTED=1 (or in a Debug build).
        if (ScriptedProviderSupport.IsEnabled && !AvailableProviders.Contains(ScriptedProviderSupport.ProviderName, StringComparer.OrdinalIgnoreCase))
            AvailableProviders.Add(ScriptedProviderSupport.ProviderName);

        SelectedProvider = config.DefaultProvider.ToLowerInvariant();
        LoadProviderSettings(config.DefaultProvider);

        WorkingDirectory = !string.IsNullOrWhiteSpace(config.Agent?.WorkingDirectory)
            ? config.Agent.WorkingDirectory
            : Directory.GetCurrentDirectory();
        AutoApprove = config.Agent?.AutoApprove ?? false;
        SelectedRequirementsClarifier = (config.Agent?.RequirementsClarifier ?? RequirementsClarifierMode.Auto).ToString();
        SelectedResumeLastSession = (config.Ui?.ResumeLastSession ?? ResumeLastSessionMode.Ask).ToString();
        CheckForUpdates = config.Ui?.CheckForUpdates ?? true;
        TerminalShell = config.Ui?.TerminalShell ?? string.Empty;
        LoadAdvancedSettings();
        LoadNetworkSettings();
        // Intentional: set the backing field directly so the loaded value is not previewed/applied
        // via OnSelectedThemeChanged; the change notification is raised manually below.
#pragma warning disable MVVMTK0034
        _selectedTheme = ThemeLabel(config.Ui?.Theme);
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedTheme));

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
    /// <summary>Raised when the user asks to leave the settings page (Close button or Esc).</summary>
    public event System.EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, System.EventArgs.Empty);

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
            Model = provider.DefaultModel ?? Catalog.DefaultModel(providerName) ?? string.Empty;
            BaseUrl = provider.BaseUrl ?? string.Empty;
            VerifySsl = provider.VerifySsl;

            // Seed the model list with the currently-configured model so the
            // ComboBox can display the saved selection without triggering a
            // network load. The full list is fetched lazily when the user opens
            // the dropdown (see EnsureModelsLoaded).
            AvailableModels.Clear();
            if (!string.IsNullOrEmpty(Model))
                AvailableModels.Add(Model);
            SeedModelOptions();
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
            Model = Catalog.DefaultModel(providerName) ?? string.Empty;
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

            // Build a temporary provider from what is typed in the form (the key may not be saved yet).
            // CreateOrFallback means a misconfigured provider doesn't throw.
            var formConfig = new ProviderConfiguration
            {
                Name = providerConfig.Name,
                BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? providerConfig.BaseUrl : BaseUrl,
                DefaultModel = providerConfig.DefaultModel,
                ApiKey = !string.IsNullOrEmpty(ApiKey) ? ApiKey : providerConfig.ApiKey,
                TimeoutSeconds = providerConfig.TimeoutSeconds,
                VerifySsl = VerifySsl,
                Headers = providerConfig.Headers
            };
            var lf = _loggerFactory ?? LoggerFactory.Create(b => { });
            var provider = ProviderFactory.CreateOrFallback(providerName, formConfig, lf);

            // Ask the provider (10 s: a dead local server must not leave the spinner running) and merge the answer
            // with models.json; on failure the result is the built-in list plus a notice.
            var discovery = new ModelDiscovery(Catalog);
            var result = await discovery.DiscoverAsync(providerName.ToLowerInvariant(), provider, TimeSpan.FromSeconds(10), token);

            if (token.IsCancellationRequested)
                return;

            // Marshal collection updates to the UI thread — Avalonia requires
            // ObservableCollection changes to happen on the dispatcher thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var options = result.Options.ToList();

                // Never silently keep a retired default: a retired or empty current model is replaced by the
                // provider's balanced default among the models the account lists.
                var switchedFrom = (string?)null;
                if (result.DefaultModel != null && (string.IsNullOrEmpty(Model) || Catalog.IsRetired(providerName, Model)))
                {
                    switchedFrom = Model;
                    Model = result.DefaultModel;
                }

                // Keep a configured model that is not in the list (a custom or brand-new id) visible.
                if (!string.IsNullOrEmpty(Model) && !options.Any(o => string.Equals(o.Id, Model, StringComparison.OrdinalIgnoreCase)))
                {
                    var meta = Catalog.Lookup(providerName, Model);
                    options.Insert(0, new ModelOption(Model, meta?.Tier, meta?.Retired ?? false, meta?.ContextWindow));
                }

                ModelOptions.Clear();
                AvailableModels.Clear();
                foreach (var o in options)
                {
                    ModelOptions.Add(o);
                    AvailableModels.Add(o.Id);
                }
                _syncingModelSelection = true;
                try { SelectedModelOption = ModelOptions.FirstOrDefault(o => string.Equals(o.Id, Model, StringComparison.OrdinalIgnoreCase)); }
                finally { _syncingModelSelection = false; }
                UpdateRetiredWarning();

                var count = options.Count(o => !o.Retired);
                ModelsStatus = result.Notice
                    ?? (count > 0 ? $"{count} models available" : "No models found");
                if (switchedFrom != null)
                    ModelsStatus += string.IsNullOrEmpty(switchedFrom)
                        ? $" Selected {Model}."
                        : $" {switchedFrom} is retired, so {Model} was selected.";
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

    /// <summary>Folder where the app writes its log files (shown read-only in Settings).</summary>
    public string LogFolder { get; } = LogPathResolver.ResolveDefault();

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogFolder);
        // UseShellExecute opens a folder in the platform file manager (Explorer, Finder or the desktop default).
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFolder) { UseShellExecute = true });
    }

    [RelayCommand]
    private Task RefreshModels()
    {
        ModelCatalogLoader.Reload(); // picks up an edited ~/.aiagent/models.json too
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
        // Check the Agent, Safety, Cost and Integrations values first: nothing is written when one of them is wrong.
        SaveFailed = false;
        SaveStatus = string.Empty;
        if (!TryReadAdvancedSettings(out var advanced))
            return;

        var config = _configurationService.Config;

        // Normalize to lowercase so DefaultProvider always matches the provider key casing
        config.DefaultProvider = SelectedProvider.ToLowerInvariant();
        config.Agent ??= new AgentConfiguration();
        config.Agent.AutoApprove = AutoApprove;
        config.Agent.RequirementsClarifier = System.Enum.Parse<RequirementsClarifierMode>(SelectedRequirementsClarifier);
        config.Agent.WorkingDirectory = WorkingDirectory;
        config.Ui ??= new UiConfiguration();
        config.Ui.ResumeLastSession = System.Enum.Parse<ResumeLastSessionMode>(SelectedResumeLastSession);
        config.Ui.CheckForUpdates = CheckForUpdates;
        SaveNetworkSettings(config);
        config.Ui.TerminalShell = string.IsNullOrWhiteSpace(TerminalShell) ? null : TerminalShell.Trim();
        config.Ui.Theme = AiCodeAgent.App.Services.ThemeService.Normalize(SelectedTheme);

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

        try
        {
            ApplyAdvancedSettings(advanced);
        }
        catch (Exception ex)
        {
            await _configurationService.SaveAsync();
            SaveFailed = true;
            SaveStatus = "The settings were saved, but pricing.json could not be written: " + ex.Message;
            return;
        }

        await _configurationService.SaveAsync();
        SaveStatus = "Settings saved.";
    }
}