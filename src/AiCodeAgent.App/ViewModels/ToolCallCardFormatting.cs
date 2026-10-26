using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Pure formatting helpers for tool call cards: friendly tool labels, readable durations and the
/// one-line subtitle (the path for file tools, the command for shell tools). Kept free of UI types
/// so it is trivially unit-testable.
/// </summary>
public static class ToolCallCardFormatting
{
    private static readonly IReadOnlyDictionary<string, string> FriendlyNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_file"] = "Read file",
            ["write_file"] = "Write file",
            ["edit_file"] = "Edit file",
            ["multi_edit"] = "Edit file (multiple)",
            ["apply_patch"] = "Apply patch",
            ["list_directory"] = "List folder",
            ["glob"] = "Find files",
            ["grep"] = "Search code",
            ["execute_command"] = "Run command",
            ["git"] = "Git",
            ["web_fetch"] = "Fetch web page",
            ["browser_check"] = "Check in browser",
            ["ask_user"] = "Ask you",
            ["spawn_subagent"] = "Run sub-agent",
            ["verify_changes"] = "Verify (build + test)",
            ["run_diagnostics"] = "Check for errors",
            ["get_diagnostics"] = "Get diagnostics",
            ["find_references"] = "Find references",
            ["go_to_definition"] = "Go to definition",
            ["todo_write"] = "Update to-do list",
            ["scaffold_backend"] = "Scaffold backend",
        };

    // Argument keys that carry the "what" of a call, in priority order.
    private static readonly string[] PathKeys = { "path", "file_path", "filePath", "file", "target_file", "directory", "dir" };
    private static readonly string[] CommandKeys = { "command", "cmd" };
    private static readonly string[] OtherKeys = { "pattern", "query", "url", "task", "prompt" };

    public const int SubtitleMaxLength = 80;

    /// <summary>"edit_file" → "Edit file"; unknown snake_case names become sentence case.</summary>
    public static string GetFriendlyName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return "Tool";
        if (FriendlyNames.TryGetValue(toolName, out var friendly)) return friendly;

        var words = toolName.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return toolName;
        var text = string.Join(' ', words).ToLowerInvariant();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// Under a minute: one decimal of seconds ("0.3 s", "11.8 s"); a minute or more: "1 m 05 s".
    /// Null means the call has not finished and gives an empty string.
    /// </summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d) return string.Empty;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        if (d.TotalSeconds < 60)
        {
            // Whole tenths from ticks: exact, culture-free, and truncated so 59.96 s never renders as "60.0 s".
            var tenths = d.Ticks / (TimeSpan.TicksPerSecond / 10);
            return string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10} s");
        }

        var minutes = (long)d.TotalMinutes;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes} m {d.Seconds:00} s");
    }

    /// <summary>The most telling argument as one short line: path, then command, then pattern/query/url, then the first argument.</summary>
    public static string ExtractSubtitle(IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return string.Empty;

        var value = FirstValue(args, PathKeys)
                    ?? FirstValue(args, CommandKeys)
                    ?? FirstValue(args, OtherKeys)
                    ?? args.Values.Select(v => v?.ToString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        return value == null ? string.Empty : OneLine(value, SubtitleMaxLength);
    }

    public static string FormatArguments(IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return "{}";
        return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public static string TruncateOutput(string? output, int maxLength)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxLength)
            return output ?? string.Empty;
        return output[..maxLength] + "\n...[truncated]";
    }

    private static string? FirstValue(IReadOnlyDictionary<string, object?> args, string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var v) && v?.ToString() is { } s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    private static string OneLine(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
