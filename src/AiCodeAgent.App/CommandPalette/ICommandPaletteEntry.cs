namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// A single executable entry in the command palette. Feature areas register
/// entries into <see cref="CommandPaletteRegistry"/> at startup.
/// </summary>
public interface ICommandPaletteEntry
{
    /// <summary>Stable unique identifier (e.g. "nav.settings", "slash.edit").</summary>
    string Id { get; }

    /// <summary>Display title shown in the palette list.</summary>
    string Title { get; }

    /// <summary>Group/category shown as a badge (e.g. "Navigation", "Chat Commands").</summary>
    string Category { get; }

    /// <summary>Additional searchable aliases.</summary>
    string[] Keywords { get; }

    /// <summary>
    /// Combined searchable text (title, category, keywords). Used by the
    /// command palette's fuzzy matcher — see <see cref="CommandPaletteViewModel"/>.
    /// </summary>
    string SearchText { get; }

    /// <summary>Optional keybinding hint shown on the right edge.</summary>
    string KeybindingHint { get; }

    /// <summary>Invoked when the entry is executed from the palette.</summary>
    Action? Action { get; }
}
