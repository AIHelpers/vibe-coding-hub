using System;

namespace AiCodeAgent.App.EditHistory;

/// <summary>
/// A single recorded edit in a file's edit history ring buffer.
/// Captures the before/after text of a localized change and its timestamp.
/// </summary>
public sealed record EditRecord
{
    public string FilePath { get; init; } = string.Empty;
    public string Before { get; init; } = string.Empty;
    public string After { get; init; } = string.Empty;
    public int? StartLine { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}