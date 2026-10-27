using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// ViewModel for the command palette overlay. Supports fuzzy search,
/// arrow-key navigation, Enter to execute, and Esc to dismiss.
/// Reads entries live from <see cref="CommandPaletteRegistry"/> so
/// feature areas can register new entries at any time.
/// </summary>
public partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxRecent = 20;

    private readonly CommandPaletteRegistry _registry;

    // Most recently executed entry ids, newest first. Session-local.
    private readonly List<string> _recentIds = new();

    [ObservableProperty]
    private bool _isOpen;

    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _selectedIndex = -1;

    /// <summary>Filtered, scored list of entries shown to the user.</summary>
    public ObservableCollection<ICommandPaletteEntry> FilteredEntries { get; } = new();

    /// <summary>Search text entered by the user in the palette input box.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                Refresh();
        }
    }

    public CommandPaletteViewModel(CommandPaletteRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Refresh();
    }

    /// <summary>Open the palette, resetting search and selection.</summary>
    public void Open()
    {
        IsOpen = true;
        SearchText = string.Empty;
        Refresh();
    }

    /// <summary>Dismiss the palette.</summary>
    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>Move the selection highlight down (wrap around).</summary>
    public void SelectNext()
    {
        if (FilteredEntries.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % FilteredEntries.Count;
    }

    /// <summary>Move the selection highlight up (wrap around).</summary>
    public void SelectPrevious()
    {
        if (FilteredEntries.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + FilteredEntries.Count) % FilteredEntries.Count;
    }

    /// <summary>Execute the currently selected entry and dismiss the palette.</summary>
    public void ExecuteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= FilteredEntries.Count)
            return;

        var entry = FilteredEntries[SelectedIndex];
        MarkUsed(entry.Id);
        Close();
        entry.Action?.Invoke();
    }

    /// <summary>Remembers an entry as recently used so it ranks higher next time.</summary>
    internal void MarkUsed(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;
        _recentIds.Remove(id);
        _recentIds.Insert(0, id);
        if (_recentIds.Count > MaxRecent)
            _recentIds.RemoveAt(_recentIds.Count - 1);
    }

    /// <summary>Up to +300 for the newest entry. Smaller than the gap between ranking tiers.</summary>
    private int RecencyBonus(string id)
    {
        var index = _recentIds.IndexOf(id);
        return index < 0 ? 0 : Math.Max(0, 300 - index * 15);
    }

    /// <summary>
    /// Rebuilds the filtered list from the registry using the current search text.
    /// Called automatically when <see cref="SearchText"/> changes and when the
    /// palette is opened.
    /// </summary>
    private void Refresh()
    {
        var query = SearchText.Trim();

        var scored = _registry.GetAllEntries()
            .Select(e => new { Entry = e, Score = FuzzyMatcher.ScoreEntry(e, query) })
            .Where(x => x.Score > 0)
            .ToList();

        // Scattered letter matches only appear when nothing matches better.
        if (query.Length > 0 && scored.Any(x => x.Score >= FuzzyMatcher.StrongMatchThreshold))
            scored.RemoveAll(x => x.Score < FuzzyMatcher.StrongMatchThreshold);

        var results = scored
            .Select(x => new { x.Entry, Score = x.Score + RecencyBonus(x.Entry.Id) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();

        FilteredEntries.Clear();
        foreach (var entry in results)
            FilteredEntries.Add(entry);

        SelectedIndex = FilteredEntries.Count > 0 ? 0 : -1;
    }
}
