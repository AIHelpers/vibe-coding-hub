using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One installed pack in the Packs tab.</summary>
public sealed class PackListItem
{
    public required InstalledPack Pack { get; init; }
    public bool InUse { get; init; }
    public string Name => Pack.Name;
    public string Title => Pack.Name + (string.IsNullOrWhiteSpace(Pack.Version) ? "" : " " + Pack.Version);
    public string Subtitle => $"{Pack.RefLabel} · {Pack.Url}" + (Pack.Pinned ? "" : " · follows " + (Pack.Ref ?? "the default branch"));
    public string Summary { get; init; } = string.Empty;
    public string UseLabel => InUse ? "in use here" : "installed, not used in this project";
}

/// <summary>E6-S1: the Packs tab of Project Knowledge: install skills, characters and flows from a git URL.</summary>
public partial class ProjectKnowledgeViewModel
{
    private readonly PackInstaller? _packs;
    private readonly SdlcPipelineLoader? _flows;
    private PackPreview? _pendingPack;

    public ObservableCollection<PackListItem> InstalledPacks { get; } = new();

    public bool IsPacksAvailable => _packs != null;
    public bool HasInstalledPacks => InstalledPacks.Count > 0;
    public bool HasNoInstalledPacks => InstalledPacks.Count == 0;
    public bool IsPacksTabActive => ActiveTabIndex == 3;

    [ObservableProperty] private bool _isAddingPack;
    [ObservableProperty] private string _packUrl = string.Empty;
    [ObservableProperty] private string _packRef = string.Empty;
    [ObservableProperty] private string _packPreviewText = string.Empty;
    [ObservableProperty] private bool _packCanInstall;
    [ObservableProperty] private bool _isPackBusy;
    [ObservableProperty] private string _packsStatusText = string.Empty;

    public bool HasPackPreview => !string.IsNullOrEmpty(PackPreviewText);
    partial void OnPackPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPackPreview));
    public bool CanFetchPack => !IsPackBusy && !string.IsNullOrWhiteSpace(PackUrl);
    partial void OnPackUrlChanged(string value) => OnPropertyChanged(nameof(CanFetchPack));
    partial void OnIsPackBusyChanged(bool value) => OnPropertyChanged(nameof(CanFetchPack));

    [RelayCommand]
    private void ShowPacksTab()
    {
        ActiveTabIndex = 3;
        RefreshPacks();
    }

    /// <summary>"＋ Add pack": opens the Packs tab with the URL form.</summary>
    [RelayCommand]
    private void BeginAddPack()
    {
        ActiveTabIndex = 3;
        RefreshPacks();
        DiscardPending();
        PackUrl = string.Empty;
        PackRef = string.Empty;
        PackPreviewText = string.Empty;
        PackCanInstall = false;
        IsAddingPack = true;
        PacksStatusText = "Paste the git URL of a pack. Pin a tag or commit in Ref for a reproducible install.";
    }

    [RelayCommand]
    private void CancelAddPack()
    {
        DiscardPending();
        IsAddingPack = false;
        PackPreviewText = string.Empty;
        PackCanInstall = false;
        PacksStatusText = string.Empty;
    }

    /// <summary>Clones the repository into a staging folder and shows what it would install. Nothing is installed yet.</summary>
    [RelayCommand]
    private async Task FetchPackPreviewAsync()
    {
        if (_packs == null || string.IsNullOrWhiteSpace(PackUrl)) return;
        DiscardPending();
        IsPackBusy = true;
        PackCanInstall = false;
        PackPreviewText = string.Empty;
        PacksStatusText = "Fetching…";
        try
        {
            _pendingPack = await _packs.PrepareAsync(PackUrl, string.IsNullOrWhiteSpace(PackRef) ? null : PackRef);
            PackPreviewText = _pendingPack.Render();
            PackCanInstall = _pendingPack.CanInstall && _pendingPack.Incompatible == null;
            PacksStatusText = PackCanInstall
                ? "Read the file list and the notes above, then install. Hooks and MCP entries in a pack are never enabled."
                : "This pack cannot be installed; see the notes above.";
        }
        catch (PackException ex)
        {
            PacksStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            PacksStatusText = "Could not fetch the pack: " + ex.Message;
        }
        finally
        {
            IsPackBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallPackAsync()
    {
        if (_packs == null || _pendingPack == null || !PackCanInstall) return;
        try
        {
            var installed = _packs.Commit(_pendingPack, makeDefault: true);
            _pendingPack = null;
            IsAddingPack = false;
            PackPreviewText = string.Empty;
            PackCanInstall = false;
            PacksStatusText = $"Installed {installed.Name} {installed.RefLabel}. Its skills, characters and flows are available now.";
            await ReloadLibrariesAsync();
        }
        catch (PackException ex)
        {
            PacksStatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemovePackAsync(PackListItem? item)
    {
        if (_packs == null || item == null) return;
        var removed = _packs.Remove(item.Name, item.Pack.RefLabel);
        PacksStatusText = removed.Count == 0 ? $"{item.Name} was not installed." : $"Removed {item.Name} {item.Pack.RefLabel}; what it added is gone.";
        await ReloadLibrariesAsync();
    }

    /// <summary>Moves packs that follow a branch to its newest commit; pinned packs stay.</summary>
    [RelayCommand]
    private async Task UpdatePacksAsync()
    {
        if (_packs == null) return;
        IsPackBusy = true;
        PacksStatusText = "Checking for updates…";
        try
        {
            var results = await _packs.UpdateAsync();
            PacksStatusText = string.Join("  ", results.Select(r => r.Message));
            await ReloadLibrariesAsync();
        }
        finally
        {
            IsPackBusy = false;
        }
    }

    private void DiscardPending()
    {
        if (_pendingPack != null && _packs != null) _packs.Discard(_pendingPack);
        _pendingPack = null;
    }

    /// <summary>Re-reads the list of installed packs.</summary>
    public void RefreshPacks()
    {
        InstalledPacks.Clear();
        if (_packs == null) return;
        var inUse = _packs.Store.Sources(string.IsNullOrEmpty(WorkingDirectory) ? null : WorkingDirectory).Select(s => s.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in _packs.Store.List())
        {
            var dir = _packs.Store.DirectoryOf(pack);
            InstalledPacks.Add(new PackListItem { Pack = pack, InUse = inUse.Contains(dir), Summary = Describe(dir) });
        }
        OnPropertyChanged(nameof(HasInstalledPacks));
        OnPropertyChanged(nameof(HasNoInstalledPacks));
    }

    private static string Describe(string dir)
    {
        int Count(string sub, string pattern, bool dirs) =>
            System.IO.Directory.Exists(System.IO.Path.Combine(dir, sub))
                ? (dirs ? System.IO.Directory.GetDirectories(System.IO.Path.Combine(dir, sub)).Length : System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, sub), pattern).Length)
                : 0;
        return $"{Count("skills", "*", true)} skill(s) · {Count("characters", "*.md", false)} character(s) · {Count("pipelines", "*.json", false)} flow(s)";
    }

    /// <summary>After a pack was added, removed or updated: re-scan skills, characters and flows so the lists and the pickers match.</summary>
    private async Task ReloadLibrariesAsync()
    {
        RefreshPacks();
        _skillRegistry?.Refresh();
        _characterRegistry?.Refresh();
        _flows?.Reload();
        await RefreshSkillsAsync();
        await Characters.RefreshAsync();
    }
}
