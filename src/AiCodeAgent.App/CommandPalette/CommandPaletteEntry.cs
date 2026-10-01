namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// Default mutable implementation of <see cref="ICommandPaletteEntry"/>.
/// </summary>
public sealed class CommandPaletteEntry : ICommandPaletteEntry
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string KeybindingHint { get; init; } = string.Empty;
    public Action? Action { get; init; }

    /// <summary>
    /// Searchable text combining title, category, and keywords.
    /// </summary>
    public string SearchText => string.Join(
        " ",
        new[] { Title, Category }
            .Concat(Keywords)
            .Where(s => !string.IsNullOrEmpty(s)));
}