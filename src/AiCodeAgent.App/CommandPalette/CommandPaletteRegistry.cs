namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// Central registry of command palette entries. Feature areas register
/// entries at startup. The palette view-model reads from this registry.
/// </summary>
public class CommandPaletteRegistry
{
    private readonly List<ICommandPaletteEntry> _entries = new();

    /// <summary>
    /// Registers a single entry. Duplicate Ids are ignored (first wins).
    /// </summary>
    public void Register(ICommandPaletteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entries.Any(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal)))
            return;
        _entries.Add(entry);
    }

    /// <summary>
    /// Registers many entries at once.
    /// </summary>
    public void Register(IEnumerable<ICommandPaletteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            Register(entry);
    }

    /// <summary>
    /// All registered entries.
    /// </summary>
    public IReadOnlyList<ICommandPaletteEntry> GetAllEntries() => _entries;
}