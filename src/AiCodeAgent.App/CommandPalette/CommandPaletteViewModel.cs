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
    private readonly CommandPaletteRegistry _registry;

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
        Close();
        entry.Action?.Invoke();
    }

    /// <summary>
    /// Rebuilds the filtered list from the registry using the current search text.
    /// Called automatically when <see cref="SearchText"/> changes and when the
    /// palette is opened.
    /// </summary>
    private void Refresh()
    {
        var query = SearchText.Trim();

        var results = _registry.GetAllEntries()
            .Select(e => new { Entry = e, Score = FuzzyMatcher.Score(e.SearchText, query) })
            .Where(x => x.Score > 0)
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
