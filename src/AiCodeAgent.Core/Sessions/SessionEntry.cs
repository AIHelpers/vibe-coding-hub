using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// One append-only record in the JSONL session log.
/// </summary>
public sealed class SessionEntry
{
    public string Type { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Payload object — shape varies by <see cref="Type"/>.</summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, object?> Payload { get; init; } = new();
}

/// <summary>Well-known <see cref="SessionEntry.Type"/> values.</summary>
public static class SessionEntryTypes
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    public const string ToolResult = "tool_result";
    public const string Status = "status";
    public const string Usage = "usage";
    public const string Finished = "finished";
}