using System;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Providers;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>The open setup wizard, or null. While set it covers the chat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupWizardOpen))]
    private SetupWizardViewModel? _setupWizard;

    public bool IsSetupWizardOpen => SetupWizard != null;

    /// <summary>On start: opens the wizard when nobody has set up a provider yet (see <see cref="FirstRunDetector"/>).</summary>
    public void StartFirstRunCheck() => _ = CheckFirstRunAsync();

    private async Task CheckFirstRunAsync()
    {
        try
        {
            if (_configurationService == null || SetupWizard != null) return;
            var probe = _serviceProvider.GetService<ILocalProviderProbe>() ?? new LocalProviderProbe();
            var show = await FirstRunDetector.ShouldShowAsync(
                _configurationService.Config, _configurationService.ConfigFileExisted, probe).ConfigureAwait(true);
            if (show) await OpenSetupWizardAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"First-run check failed: {ex.Message}");
        }
    }

    /// <summary>Command Palette → "Setup wizard": opens the three-step setup again.</summary>
    [RelayCommand]
    private async Task OpenSetupWizardAsync()
    {
        if (_configurationService == null || SetupWizard != null) return;

        var wizard = new SetupWizardViewModel(
            _configurationService,
            _serviceProvider.GetService<ILocalProviderProbe>() ?? new LocalProviderProbe(),
            _serviceProvider.GetService<IConnectionTester>() ?? new ConnectionTester(),
            pickFolder: PickProjectFolderAsync,
            createSampleProject: null); // "Try the sample project" appears once the sample exists (E3-S3)
        wizard.Completed += OnSetupWizardCompleted;
        wizard.Skipped += OnSetupWizardSkipped;

        CloseOverlays();
        SetupWizard = wizard;
        await wizard.InitializeAsync();
    }

    private async Task<string?> PickProjectFolderAsync()
    {
        if (_hostWindow == null) return null;
        var picked = await _hostWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a project folder",
            AllowMultiple = false
        });
        return picked.Count > 0 ? picked[0].Path.LocalPath : null;
    }

    private void OnSetupWizardCompleted(object? sender, SetupWizardResult result)
    {
        CloseSetupWizard();
        // Settings and the working folder were saved by the wizard; the saved-config handler moved the explorer.
        // The provider follows the saved settings by itself (ConfiguredAiProvider), so the first message already uses it.
        GetOrCreateChatViewModel().PermissionMode = result.PermissionMode;
        StatusText = $"Ready: {result.ProviderKey} · {result.Model}";
    }

    private void OnSetupWizardSkipped(object? sender, EventArgs e) => CloseSetupWizard();

    private void CloseSetupWizard()
    {
        if (SetupWizard == null) return;
        SetupWizard.Completed -= OnSetupWizardCompleted;
        SetupWizard.Skipped -= OnSetupWizardSkipped;
        SetupWizard = null;
    }
}
