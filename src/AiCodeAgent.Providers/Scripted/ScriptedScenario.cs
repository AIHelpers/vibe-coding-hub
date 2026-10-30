using System.Text.RegularExpressions;

namespace AiCodeAgent.Providers.Scripted;

/// <summary>
/// Which conversation a script applies to. Every field that is set must hold. "System" is the system prompt (for a flow step:
/// the character's text), "first user" is the first user message (the task, or a flow step's prompt).
/// </summary>
public sealed record ScenarioMatch
{
    public string? FirstUserContains { get; init; }
    public string? FirstUserStartsWith { get; init; }
    public string? FirstUserRegex { get; init; }
    public string? SystemContains { get; init; }
    public string? SystemStartsWith { get; init; }
    public string? SystemRegex { get; init; }

    public bool IsEmpty => FirstUserContains == null && FirstUserStartsWith == null && FirstUserRegex == null
                           && SystemContains == null && SystemStartsWith == null && SystemRegex == null;

    /// <summary>Text comparisons ignore case.</summary>
    public bool IsMatch(string? system, string? firstUser)
    {
        system ??= string.Empty;
        firstUser ??= string.Empty;
        return Contains(firstUser, FirstUserContains) && StartsWith(firstUser, FirstUserStartsWith) && Rx(firstUser, FirstUserRegex)
               && Contains(system, SystemContains) && StartsWith(system, SystemStartsWith) && Rx(system, SystemRegex);
    }

    private static bool Contains(string text, string? needle) => needle == null || text.Contains(needle, StringComparison.OrdinalIgnoreCase);
    private static bool StartsWith(string text, string? prefix) => prefix == null || text.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    private static bool Rx(string text, string? pattern) => pattern == null || Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>A tool call the model makes. <see cref="Args"/> are passed as the model would send them (JSON values).</summary>
public sealed record ScriptedToolCall
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, object?> Args { get; init; } = new();
}

/// <summary>Token counts reported for a turn. Without them the provider estimates (about four characters per token).</summary>
public sealed record ScriptedUsage
{
    public int Prompt { get; init; }
    public int Completion { get; init; }
}

/// <summary>
/// One model response: text, tool calls or both. A turn with <see cref="IfResultContains"/> is used only when the previous
/// tool result contains that text (ignoring case); otherwise <see cref="Else"/> is used, and without an <c>else</c> the
/// provider answers with an error that names the scenario and the turn, so a script that drifted from reality is easy to find.
/// </summary>
public sealed record ScriptedTurn
{
    public string? Text { get; init; }
    public ScriptedToolCall? ToolCall { get; init; }
    public List<ScriptedToolCall>? ToolCalls { get; init; }
    /// <summary>Wait this long before the first chunk (scaled by <c>AIAGENT_SCRIPTED_DELAY_SCALE</c>).</summary>
    public int DelayMs { get; init; }
    /// <summary>Wait this long between streamed text chunks.</summary>
    public int ChunkDelayMs { get; init; }
    /// <summary>Characters per streamed text chunk (default 32).</summary>
    public int? Chunk { get; init; }
    public ScriptedUsage? Usage { get; init; }
    public string? IfResultContains { get; init; }
    public ScriptedTurn? Else { get; init; }

    public IEnumerable<ScriptedToolCall> AllToolCalls()
    {
        if (ToolCall != null) yield return ToolCall;
        if (ToolCalls != null)
            foreach (var call in ToolCalls) yield return call;
    }
}

/// <summary>
/// The script of one conversation. <see cref="Rounds"/> holds one list of turns per time the conversation starts again
/// (a flow step that is sent back runs again: round 1 rejects, round 2 approves); the last round repeats. <see cref="Turns"/>
/// is the same thing for a single round.
/// </summary>
public record ScriptedAgent
{
    public string? Name { get; init; }
    public ScenarioMatch? Match { get; init; }
    public List<ScriptedTurn>? Turns { get; init; }
    public List<List<ScriptedTurn>>? Rounds { get; init; }

    public IReadOnlyList<IReadOnlyList<ScriptedTurn>> AllRounds()
    {
        var rounds = new List<IReadOnlyList<ScriptedTurn>>();
        if (Turns is { Count: > 0 }) rounds.Add(Turns);
        if (Rounds != null) rounds.AddRange(Rounds.Where(r => r.Count > 0));
        return rounds;
    }
}

/// <summary>
/// A scenario file. A single conversation: <c>match</c> plus <c>turns</c> (or <c>rounds</c>). A flow: <c>agents</c>, one
/// script per step (matched by the character's text). <see cref="Match"/> on the scenario applies to all of its agents too.
/// </summary>
public sealed record ScriptedScenario : ScriptedAgent
{
    public string Description { get; init; } = string.Empty;
    public List<ScriptedAgent>? Agents { get; init; }
    /// <summary>Where it was loaded from (file path or "built-in"); set by the loader.</summary>
    public string Source { get; init; } = string.Empty;
}
