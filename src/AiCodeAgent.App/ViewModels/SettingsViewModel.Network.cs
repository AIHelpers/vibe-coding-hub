using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Network;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One host this session talked to (or tried to), as Settings → Network activity lists it.</summary>
public sealed class NetworkHostRowViewModel
{
    public NetworkHostRowViewModel(NetworkHostSummary summary)
    {
        Host = summary.Host;
        IsLocal = summary.IsLocal;
        Requests = summary.Requests;
        Blocked = summary.Blocked;
        Purposes = summary.PurposesText;
        Last = summary.Last.ToLocalTime().ToString("HH:mm:ss");
    }

    public string Host { get; }
    public bool IsLocal { get; }
    public string Where => IsLocal ? "this computer" : "internet";
    public int Requests { get; }
    public int Blocked { get; }
    public string Purposes { get; }
    public string Last { get; }
    public string Counts => Blocked > 0 ? $"{Requests} sent · {Blocked} blocked" : $"{Requests} sent";
    public bool HasBlocked => Blocked > 0;
}

/// <summary>E7-S4: Settings → Advanced → Network activity (the live host list) and the Offline mode switch.</summary>
public partial class SettingsViewModel
{
    private NetworkPolicy _network = NetworkPolicy.Shared;
    private int _networkRefreshQueued;
    private EventHandler? _networkHandler;

    /// <summary>Block every host except localhost and the configured model provider(s). Takes effect when settings are saved.</summary>
    [ObservableProperty]
    private bool _offlineMode;

    /// <summary>Hosts contacted this session, most recent first.</summary>
    public ObservableCollection<NetworkHostRowViewModel> NetworkRows { get; } = new();

    [ObservableProperty]
    private string _networkSummary = string.Empty;

    public bool HasNetworkRows => NetworkRows.Count > 0;

    /// <summary>Set when AIAGENT_OFFLINE forces offline mode on.</summary>
    public string OfflineModeNote => _network.OfflineLockedByEnvironment
        ? $"{NetworkPolicy.OfflineEnvironmentVariable} is set, so offline mode is on whatever this switch says."
        : string.Empty;

    public bool HasOfflineModeNote => OfflineModeNote.Length > 0;

    /// <summary>Test hook: use another policy than the process-wide one.</summary>
    public void UseNetworkPolicy(NetworkPolicy policy)
    {
        if (_networkHandler != null) _network.Changed -= _networkHandler;
        _networkHandler = null;
        _network = policy;
        LoadNetworkSettings();
    }

    private void LoadNetworkSettings()
    {
        OfflineMode = _configurationService.Config.Network?.OfflineMode ?? false;
        if (_networkHandler == null)
        {
            // Weak: the view model is transient and the policy lives as long as the process.
            var weak = new WeakReference<SettingsViewModel>(this);
            var policy = _network;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (weak.TryGetTarget(out var vm)) vm.OnNetworkChanged();
                else policy.Changed -= handler;
            };
            _networkHandler = handler;
            policy.Changed += handler;
        }
        OnPropertyChanged(nameof(OfflineModeNote));
        OnPropertyChanged(nameof(HasOfflineModeNote));
        RefreshNetwork();
    }

    private void SaveNetworkSettings(AppConfiguration config)
    {
        config.Network ??= new NetworkConfiguration();
        config.Network.OfflineMode = OfflineMode;
    }

    private void ResetNetworkSettings(AppConfiguration config)
    {
        config.Network = new NetworkConfiguration();
    }

    // The policy raises Changed from whatever thread made a request; fold bursts into one refresh on the UI thread.
    private void OnNetworkChanged()
    {
        if (Interlocked.Exchange(ref _networkRefreshQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _networkRefreshQueued, 0);
            RefreshNetwork();
        });
    }

    [RelayCommand]
    private void RefreshNetwork()
    {
        var hosts = _network.Hosts();
        NetworkRows.Clear();
        foreach (var h in hosts) NetworkRows.Add(new NetworkHostRowViewModel(h));
        OnPropertyChanged(nameof(HasNetworkRows));

        var sent = hosts.Sum(h => h.Requests);
        var blocked = hosts.Sum(h => h.Blocked);
        var outside = hosts.Where(h => !h.IsLocal).Sum(h => h.Requests);
        NetworkSummary = hosts.Count == 0
            ? "Nothing has left this app yet this session."
            : $"{hosts.Count} host(s), {sent} request(s) sent, {outside} of them outside this computer" +
              (blocked > 0 ? $", {blocked} blocked" : "") + ".";
        if (_network.OfflineMode)
            NetworkSummary += " Offline mode is on: only localhost" +
                              (_network.ProviderHosts.Count > 0 ? " and " + string.Join(", ", _network.ProviderHosts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) : "") +
                              " can be reached.";
    }

    [RelayCommand]
    private void ClearNetworkLog()
    {
        _network.Clear();
        RefreshNetwork();
    }
}
