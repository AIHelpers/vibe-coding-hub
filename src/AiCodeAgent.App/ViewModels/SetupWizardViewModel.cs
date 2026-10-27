using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

public enum WizardStep { Source = 0, Connect = 1, Project = 2 }

/// <summary>One choice on step 1: a local server or a cloud provider.</summary>
public sealed class WizardSource
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>A server on this computer: free and private, needs no key.</summary>
    public bool IsLocal { get; init; }
    /// <summary>The probe found it running.</summary>
    public bool IsDetected { get; init; }
    /// <summary>"Custom OpenAI-compatible": the user types the URL.</summary>
    public bool IsCustom { get; init; }
    public string BaseUrl { get; init; } = string.Empty;

    public string Badge => IsDetected ? "Detected ✓ (free, private)"
        : IsLocal ? "Not running" : string.Empty;
    public bool HasBadge => Badge.Length > 0;
}

/// <summary>A permission mode with its one-line explanation.</summary>
public sealed record PermissionChoice(string Mode, string Title, string Description);

/// <summary>What the wizard decided; the window applies the parts that need live objects.</summary>
public sealed record SetupWizardResult(string ProviderKey, string Model, string? ProjectPath, string PermissionMode);

/// <summary>
/// The first-run wizard: 1 choose a model source, 2 connect (key, test, model), 3 open a project and pick a
/// permission mode. Finishing writes <c>~/.aiagent/config.json</c> (API keys are encrypted there, DPAPI on Windows);
/// nothing is sent anywhere except the test request to the provider the user chose.
/// </summary>
public partial class SetupWizardViewModel : ObservableObject
{
    private readonly ConfigurationService _config;
    private readonly ILocalProviderProbe _probe;
    private readonly IConnectionTester _tester;
    private readonly Func<Task<string?>>? _pickFolder;
    private readonly Func<Task<string?>>? _createSample;
    private CancellationTokenSource? _testCts;

    public SetupWizardViewModel(
        ConfigurationService config,
        ILocalProviderProbe probe,
        IConnectionTester tester,
        Func<Task<string?>>? pickFolder = null,
        Func<Task<string?>>? createSampleProject = null)
    {
        _config = config;
        _probe = probe;
        _tester = tester;
        _pickFolder = pickFolder;
        _createSample = createSampleProject;

        PermissionChoices = new[]
        {
            new PermissionChoice("Ask", "Ask", "Asks you before every file change and command. The safe default."),
            new PermissionChoice("AutoEdit", "Auto-edit", "Edits files on its own, but asks before running commands."),
            new PermissionChoice("Plan", "Plan only", "Reads your code and proposes a plan; changes nothing."),
            new PermissionChoice("FullAuto", "Full auto", "Does everything without asking. Use it only on projects you can restore.")
        };
        SelectedPermission = PermissionChoices[0];

        foreach (var folder in RecentFolderList.Existing(_config.Config.Ui?.RecentFolders))
            RecentFolders.Add(folder);
        var current = _config.Config.Agent?.WorkingDirectory;
        ProjectPath = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current! : string.Empty;
    }

    // ----- results -----

    /// <summary>Raised after the settings were saved by <see cref="FinishCommand"/>.</summary>
    public event EventHandler<SetupWizardResult>? Completed;

    /// <summary>Raised after <see cref="SkipCommand"/> (the wizard will not open by itself again).</summary>
    public event EventHandler? Skipped;

    // ----- state -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStep), nameof(IsConnectStep), nameof(IsProjectStep), nameof(StepTitle), nameof(CanGoBack), nameof(IsLastStep))]
    private WizardStep _step = WizardStep.Source;

    public bool IsSourceStep => Step == WizardStep.Source;
    public bool IsConnectStep => Step == WizardStep.Connect;
    public bool IsProjectStep => Step == WizardStep.Project;
    public bool CanGoBack => Step != WizardStep.Source;
    public bool IsLastStep => Step == WizardStep.Project;

    public string StepTitle => Step switch
    {
        WizardStep.Source => "Step 1 of 3 · Choose where the AI runs",
        WizardStep.Connect => "Step 2 of 3 · Connect",
        _ => "Step 3 of 3 · Open a project"
    };

    [ObservableProperty]
    private bool _isDetecting;

    public ObservableCollection<WizardSource> Sources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsApiKey), nameof(NeedsUrl), nameof(IsLocalSource), nameof(SourceTitle))]
    private WizardSource? _selectedSource;

    public bool NeedsApiKey => SelectedSource is { IsLocal: false };
    public bool NeedsUrl => SelectedSource is { IsCustom: true };
    public bool IsLocalSource => SelectedSource is { IsLocal: true };
    public string SourceTitle => SelectedSource?.Title ?? string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _customUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    public bool HasMessage => Message.Length > 0;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModels))]
    private bool _testSucceeded;

    public bool HasModels => TestSucceeded && Models.Count > 0;

    public ObservableCollection<ModelOption> Models { get; } = new();

    [ObservableProperty]
    private ModelOption? _selectedModel;

    // ----- step 3 -----

    public ObservableCollection<string> RecentFolders { get; } = new();

    public bool HasRecentFolders => RecentFolders.Count > 0;

    [ObservableProperty]
    private string _projectPath = string.Empty;

    /// <summary>The sample project (E3-S3) is offered only when the app can create one.</summary>
    public bool CanTrySample => _createSample != null;

    public PermissionChoice[] PermissionChoices { get; }

    [ObservableProperty]
    private PermissionChoice _selectedPermission = null!;

    // ----- start -----

    /// <summary>
    /// Looks for local servers (in parallel, one second each) and builds the list of sources: detected ones first
    /// and preselected, then the cloud providers, then local servers that did not answer.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsDetecting = true;
        IReadOnlyList<DetectedProvider> detected;
        try
        {
            detected = await _probe.ProbeAsync(null, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { detected = Array.Empty<DetectedProvider>(); }
        finally { IsDetecting = false; }

        Sources.Clear();
        foreach (var d in detected)
            Sources.Add(new WizardSource
            {
                Key = d.Key, Title = d.DisplayName, IsLocal = true, IsDetected = true, BaseUrl = d.BaseUrl,
                Description = d.Models.Count > 0
                    ? $"Running on this computer with {d.Models.Count} model{(d.Models.Count == 1 ? "" : "s")}. Nothing leaves your machine."
                    : "Running on this computer, but no model is installed yet."
            });

        Sources.Add(new WizardSource { Key = "openai", Title = "OpenAI", BaseUrl = "https://api.openai.com", Description = "GPT models. Needs an API key from platform.openai.com." });
        Sources.Add(new WizardSource { Key = "anthropic", Title = "Anthropic", BaseUrl = "https://api.anthropic.com", Description = "Claude models. Needs an API key from console.anthropic.com." });
        Sources.Add(new WizardSource { Key = "custom", Title = "Custom OpenAI-compatible", IsCustom = true, Description = "Any server that speaks the OpenAI API: vLLM, LocalAI, Groq, Together… You type its URL." });

        foreach (var target in LocalProviderProbe.DefaultTargets)
            if (detected.All(d => d.Key != target.Key))
                Sources.Add(new WizardSource
                {
                    Key = target.Key, Title = target.DisplayName, IsLocal = true, BaseUrl = target.BaseUrl,
                    Description = "Free and private, but not running right now. Start it, then reopen this wizard."
                });

        SelectedSource = Sources.FirstOrDefault(s => s.IsDetected) ?? Sources.FirstOrDefault(s => s.Key == "openai");
    }

    partial void OnSelectedSourceChanged(WizardSource? value) => ResetTest();
    partial void OnApiKeyChanged(string value) => ResetTest();
    partial void OnCustomUrlChanged(string value) => ResetTest();

    private void ResetTest()
    {
        _testCts?.Cancel();
        IsTesting = false;
        TestSucceeded = false;
        Models.Clear();
        SelectedModel = null;
        Message = string.Empty;
        OnPropertyChanged(nameof(HasModels));
        NextCommand.NotifyCanExecuteChanged();
    }

    // ----- navigation -----

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case WizardStep.Source:
                Step = WizardStep.Connect;
                Message = string.Empty;
                // A server on this computer needs no key: check it right away so the model list is ready.
                if (SelectedSource is { IsLocal: true, IsDetected: true })
                    await TestConnectionAsync();
                break;

            case WizardStep.Connect:
                Step = WizardStep.Project;
                Message = string.Empty;
                break;

            case WizardStep.Project:
                await FinishAsync();
                break;
        }
        NextCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoNext() => Step switch
    {
        WizardStep.Source => SelectedSource != null,
        WizardStep.Connect => TestSucceeded && SelectedModel is { Retired: false },
        _ => ProjectPath.Length == 0 || Directory.Exists(ProjectPath)
    };

    partial void OnTestSucceededChanged(bool value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnSelectedModelChanged(ModelOption? value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnProjectPathChanged(string value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnStepChanged(WizardStep value) => NextCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Back()
    {
        if (Step == WizardStep.Source) return;
        Step = Step == WizardStep.Project ? WizardStep.Connect : WizardStep.Source;
        Message = Step == WizardStep.Connect && TestSucceeded ? "Connected." : string.Empty;
    }

    /// <summary>Leaves without changing the provider; the wizard stays closed from now on (Command Palette → Setup wizard reopens it).</summary>
    [RelayCommand]
    private async Task SkipAsync()
    {
        _testCts?.Cancel();
        _config.Config.Ui.SetupWizardDone = true;
        await _config.SaveAsync();
        Skipped?.Invoke(this, EventArgs.Empty);
    }

    // ----- step 2 -----

    /// <summary>Builds the settings for the chosen source from what is typed, or returns why it cannot be built.</summary>
    internal (ProviderConfiguration? Config, string? Problem) BuildProviderConfiguration()
    {
        var source = SelectedSource;
        if (source == null) return (null, "Choose where the AI runs first.");

        string baseUrl;
        if (source.IsCustom)
        {
            if (!TryNormalizeUrl(CustomUrl, out baseUrl))
                return (null, "Enter the server address, for example http://localhost:1234.");
        }
        else baseUrl = source.BaseUrl;

        if (!source.IsLocal && string.IsNullOrWhiteSpace(ApiKey) && !source.IsCustom)
            return (null, $"Paste your {source.Title} API key first.");

        var existing = _config.Config.Providers.TryGetValue(KeyOf(source), out var e) ? e : null;
        var cfg = (existing ?? new ProviderConfiguration { TimeoutSeconds = source.IsLocal || source.IsCustom ? 300 : 120 }) with
        {
            Name = KeyOf(source),
            BaseUrl = baseUrl,
            ApiKey = source.IsLocal ? existing?.ApiKey : ApiKey.Trim(),
            DefaultModel = existing?.DefaultModel ?? string.Empty
        };
        return (cfg, null);
    }

    private static string KeyOf(WizardSource source) => source.IsCustom ? "custom" : source.Key;

    /// <summary>"http://host:1234/v1/" → "http://host:1234". Only http(s) addresses are accepted.</summary>
    internal static bool TryNormalizeUrl(string? text, out string url)
    {
        url = string.Empty;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        var text2 = trimmed.TrimEnd('/');
        if (text2.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) text2 = text2[..^3];
        url = text2;
        return true;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var (cfg, problem) = BuildProviderConfiguration();
        if (cfg == null)
        {
            Message = problem ?? string.Empty;
            return;
        }

        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        IsTesting = true;
        Message = "Testing…";
        try
        {
            var result = await _tester.TestAsync(cfg.Name, cfg, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return; // the form changed while waiting

            Models.Clear();
            SelectedModel = null;
            if (result.Success)
            {
                foreach (var m in result.Models) Models.Add(m);
                SelectedModel = Models.FirstOrDefault(m => m.Id == result.DefaultModel) ?? Models.FirstOrDefault(m => !m.Retired);
            }
            TestSucceeded = result.Success;
            Message = result.Message;
            OnPropertyChanged(nameof(HasModels));
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer test or by a change in the form
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts)) IsTesting = false;
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    // ----- step 3 -----

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_pickFolder == null) return;
        var picked = await _pickFolder();
        if (!string.IsNullOrWhiteSpace(picked)) ProjectPath = picked!;
    }

    [RelayCommand]
    private void UseRecent(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) ProjectPath = folder!;
    }

    [RelayCommand]
    private async Task TrySampleAsync()
    {
        if (_createSample == null) return;
        try
        {
            var path = await _createSample();
            if (string.IsNullOrWhiteSpace(path)) return;
            ProjectPath = path!;
        }
        catch (Exception ex)
        {
            Message = $"Couldn't create the sample project: {ex.Message}";
            return;
        }

        // "Try the sample project" opens it right away: the model and permission chosen so far are saved with it.
        await FinishAsync();
    }

    // ----- finish -----

    /// <summary>Writes the choices into config.json and reports them.</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        var (cfg, problem) = BuildProviderConfiguration();
        if (cfg == null || SelectedModel == null)
        {
            Message = problem ?? "Test the connection and choose a model first.";
            return;
        }
        if (ProjectPath.Length > 0 && !Directory.Exists(ProjectPath))
        {
            Message = "That folder doesn't exist.";
            return;
        }

        var config = _config.Config;
        config.Providers[cfg.Name] = cfg with { DefaultModel = SelectedModel.Id };
        config.DefaultProvider = cfg.Name;
        config.Ui.SetupWizardDone = true;
        config.Agent.DefaultPermissionMode = SelectedPermission.Mode;
        if (ProjectPath.Length > 0)
        {
            config.Agent.WorkingDirectory = ProjectPath;
            RecentFolderList.Push(config.Ui.RecentFolders, ProjectPath);
        }

        await _config.SaveAsync();
        Completed?.Invoke(this, new SetupWizardResult(cfg.Name, SelectedModel.Id, ProjectPath.Length > 0 ? ProjectPath : null, SelectedPermission.Mode));
    }
}
