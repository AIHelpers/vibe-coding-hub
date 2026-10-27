using System;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Turns the raw events of an agent run into Markdown that is meant to be read: the body keeps the answer,
/// tool calls and real warnings; internal progress lines such as "[Done] Agent completed" never reach it
/// (the run state is shown as a badge instead). Used by the flow step output and the Background Tasks panel.
/// </summary>
public static class AgentOutputText
{
    private static readonly Regex InternalLine = new(
        @"^[ \t]*\[(?:Done|Processing)\][^\n]*(?:\n|$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Status updates that only narrate the loop ("Done", "Processing", "Executing x", "Requesting approval…").</summary>
    public static bool IsInternalStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status.Equals("Done", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Processing", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Executing ", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Requesting approval", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes "[Done] …" / "[Processing] …" lines from text that may contain them. Everything else is kept as is.</summary>
    public static string StripInternalLines(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : InternalLine.Replace(text, string.Empty);

    /// <summary>A Markdown note for a status update the reader cares about, or null for internal ones.</summary>
    public static string? StatusNote(StatusUpdateEvent update)
    {
        if (IsInternalStatus(update.Status))
            return null;
        return Quote($"**{update.Status}**" + (string.IsNullOrWhiteSpace(update.Detail) ? string.Empty : " " + update.Detail.Trim()));
    }

    public static string ToolNote(string toolName) => $"\n\n*Tool:* `{toolName}`\n\n";

    public static string ApprovalNote(string toolName) => Quote($"⏸ Waiting for approval: `{toolName}`");

    public static string ErrorNote(string message) => Quote($"⚠ **Error:** {message.Trim()}");

    public static string FailureNote(string state, string detail) => Quote($"⚠ **{state}:** {detail.Trim()}");

    public static string RoundHeader(int round) => $"*— round {round} —*\n\n";

    private static string Quote(string text) =>
        "\n\n> " + text.Replace("\r\n", "\n").Replace("\n", "\n> ") + "\n\n";
}
