using System;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.App.ViewModels;

/// <summary>The in-app update check (E3-S5): a daily look at GitHub's latest release and a banner when it is newer.</summary>
public partial class MainViewModel
{
    /// <summary>Seconds to wait after start before the automatic check, so it never competes with start-up work.</summary>
    private const int UpdateCheckDelaySeconds = 4;

    private bool _updateCheckRunning;

    /// <summary>The "update available" banner, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBannerOpen))]
    private UpdateBannerViewModel? _updateBanner;

    public bool IsUpdateBannerOpen => UpdateBanner != null;

    /// <summary>On start: checks for a newer release when that is on and due (see <see cref="UpdateService"/>).</summary>
    public void StartUpdateCheck() => _ = RunUpdateCheckAsync(manual: false);

    /// <summary>Command Palette → "Check for updates": looks right now, whatever the setting and the last check say.</summary>
    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (_updateCheckRunning || _configurationService == null) return;

        // Builds without a checker (tests, embedding) never touch the network.
        var checker = _serviceProvider.GetService<IUpdateChecker>();
        if (checker == null)
        {
            if (manual) StatusText = "Update check isn't available in this build.";
            return;
        }

        // Offline mode: no automatic look at GitHub (the policy would refuse it anyway). A manual check says why it failed.
        if (!manual && AiCodeAgent.Core.Network.NetworkPolicy.Shared.OfflineMode) return;

        var ui = _configurationService.Config.Ui;
        // Switched off, or checked less than a day ago: no delay, no request.
        if (!manual && (!ui.CheckForUpdates || !UpdateService.IsDue(ui, DateTimeOffset.UtcNow))) return;

        _updateCheckRunning = true;
        try
        {
            if (manual) StatusText = "Checking for updates…";
            else await Task.Delay(TimeSpan.FromSeconds(UpdateCheckDelaySeconds)).ConfigureAwait(true);

            var current = AppInfo.CurrentVersion;
            var outcome = await new UpdateService(checker)
                .CheckAsync(ui, current, UpdatePlatform.Current(), manual).ConfigureAwait(true);

            if (outcome.Requested) await SaveConfigurationQuietlyAsync();
            if (outcome.Offer != null)
            {
                ShowUpdateBanner(outcome.Offer, current);
                if (manual) StatusText = $"Version {outcome.Offer.Version} is available.";
            }
            else if (manual)
            {
                StatusText = outcome.Result?.Status == UpdateStatus.Failed
                    ? "Couldn't check for updates. Are you online?"
                    : $"You're up to date (version {current}).";
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the app.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            if (manual) StatusText = "Couldn't check for updates.";
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private void ShowUpdateBanner(ReleaseInfo release, SemanticVersion current)
    {
        CloseUpdateBanner();
        var launcher = _serviceProvider.GetService<IUrlLauncher>() ?? new SystemUrlLauncher();
        var banner = new UpdateBannerViewModel(release, current, launcher.Open);
        banner.DismissRequested += OnUpdateDismissed;
        banner.Closed += OnUpdateBannerClosed;
        UpdateBanner = banner;
    }

    private void OnUpdateDismissed(object? sender, EventArgs e)
    {
        if (UpdateBanner is { } banner && _configurationService != null)
        {
            // Not this version again; a newer one is announced.
            UpdateService.Dismiss(_configurationService.Config.Ui, banner.Release);
            _ = SaveConfigurationQuietlyAsync();
        }
        CloseUpdateBanner();
    }

    private void OnUpdateBannerClosed(object? sender, EventArgs e) => CloseUpdateBanner();

    private void CloseUpdateBanner()
    {
        var banner = UpdateBanner;
        if (banner == null) return;
        banner.DismissRequested -= OnUpdateDismissed;
        banner.Closed -= OnUpdateBannerClosed;
        UpdateBanner = null;
    }

    private async Task SaveConfigurationQuietlyAsync()
    {
        try
        {
            if (_configurationService == null) return;
            await _configSaveLock.WaitAsync();
            try { await _configurationService.SaveAsync(); }
            finally { _configSaveLock.Release(); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Saving the update settings failed: {ex.Message}");
        }
    }
}
