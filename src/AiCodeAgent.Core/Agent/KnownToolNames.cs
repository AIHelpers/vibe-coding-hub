namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Tool names this build provides. Role presets and characters that list a name
/// outside this set are reported by <c>skills doctor</c>, and the name never runs.
/// Keep this list in step with the tools registered by AiCodeAgent.Tools; a test
/// checks that every built-in preset uses only names from it.
/// </summary>
public static class KnownToolNames
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "multi_edit", "apply_patch",
        "list_directory", "grep", "glob", "execute_command", "run_diagnostics",
        "verify_changes", "browser_check", "git", "get_diagnostics", "todo_write",
        "ask_user", "go_to_definition", "find_references", "web_fetch", "use_skill",
        "spawn_subagent", "send_message", "pipeline_stage", "scaffold_backend"
    };

    /// <summary>True for a known tool name, or for a pattern such as <c>mcp:github/*</c> that is not checked here.</summary>
    public static bool IsKnown(string name) => Names.Contains(name) || IsPattern(name);

    /// <summary>Names with <c>*</c> or <c>:</c> (MCP and skill wildcards) are resolved at run time.</summary>
    public static bool IsPattern(string name) => name.Contains('*') || name.Contains(':');

    /// <summary>The known tool a misspelt name most likely meant (<c>ReadFile</c> → <c>read_file</c>), or null.</summary>
    public static string? Suggest(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0) return null;
        return Names.FirstOrDefault(n => Normalize(n) == key);
    }

    /// <summary>The names that are neither known nor patterns, in their original order and spelling.</summary>
    public static IReadOnlyList<string> Unknown(IEnumerable<string>? names) =>
        (names ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n) && !IsKnown(n))
            .ToList();

    /// <summary>Plain text for unknown names: <c>unknown tools ReadFile → did you mean read_file?</c></summary>
    public static string Describe(IEnumerable<string> names) =>
        "unknown tools " + string.Join("; ", names.Select(n => Suggest(n) is { } s ? $"{n} → did you mean {s}?" : n));

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
