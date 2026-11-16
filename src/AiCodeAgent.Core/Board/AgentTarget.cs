namespace AiCodeAgent.Core.Board;

/// <summary>Who does the work of a card (E11-S2).</summary>
public enum AgentTargetKind
{
    /// <summary>The app's own agent with the current model and settings.</summary>
    Default,
    /// <summary>One of the app's own agents with a persona, tools and skills (a character file). A character with <c>runtime: acp</c> is an external agent.</summary>
    Character,
    /// <summary>A flow (a pipeline with edges): several steps and agents, run unattended on the card's worktree.</summary>
    Flow,
    /// <summary>A coding agent outside the app, started as a program and driven over the Agent Client Protocol (Claude Code, Codex, Gemini CLI...).</summary>
    External
}

/// <summary>
/// The agent a card is assigned to. A small immutable value saved with the card, so a retry uses the same agent and the board
/// file stays readable. What <see cref="Id"/> means depends on <see cref="Kind"/>: a character id, a flow name, or for an external
/// agent the id of a preset (<see cref="ExternalAgentPresets"/>), which is looked up again at run time so the program is found
/// where it is installed now.
/// </summary>
public sealed record AgentTarget
{
    public AgentTargetKind Kind { get; init; } = AgentTargetKind.Default;
    public string? Id { get; init; }
    /// <summary>External agents without a preset: the program to start.</summary>
    public string? Command { get; init; }
    /// <summary>Arguments for <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    /// <summary>How the agent is named on the card, when the id is not good enough.</summary>
    public string? Label { get; init; }

    public static AgentTarget Default { get; } = new();

    public static AgentTarget ForCharacter(string id, string? label = null) =>
        new() { Kind = AgentTargetKind.Character, Id = id, Label = label };

    public static AgentTarget ForFlow(string name) =>
        new() { Kind = AgentTargetKind.Flow, Id = name };

    public static AgentTarget ForPreset(ExternalAgentPreset preset) =>
        new() { Kind = AgentTargetKind.External, Id = preset.Id, Label = preset.Name };

    public static AgentTarget ForExternal(string command, IEnumerable<string>? args = null, string? label = null) =>
        new() { Kind = AgentTargetKind.External, Command = command, Args = args?.ToList() ?? new List<string>(), Label = label };

    /// <summary>The name shown on the card.</summary>
    public string DisplayName => Kind switch
    {
        AgentTargetKind.Character => FirstText(Label, Id) ?? "Character",
        AgentTargetKind.Flow => "Flow " + (FirstText(Label, Id) ?? string.Empty),
        AgentTargetKind.External => FirstText(Label, Command, Id) ?? "External agent",
        _ => "Default agent"
    };

    /// <summary>What is missing for this target to make sense at all; null when it is complete.</summary>
    public string? Problem => Kind switch
    {
        AgentTargetKind.Character when string.IsNullOrWhiteSpace(Id) => "No character is chosen.",
        AgentTargetKind.Flow when string.IsNullOrWhiteSpace(Id) => "No flow is chosen.",
        AgentTargetKind.External when string.IsNullOrWhiteSpace(Id) && string.IsNullOrWhiteSpace(Command) => "No external agent is chosen.",
        _ => null
    };

    // A record compares collections by reference; two targets with the same arguments are the same target.
    public bool Equals(AgentTarget? other) =>
        other != null && Kind == other.Kind && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Command, other.Command, StringComparison.Ordinal) && string.Equals(Label, other.Label, StringComparison.Ordinal)
        && Args.SequenceEqual(other.Args, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Id);
        hash.Add(Command);
        hash.Add(Label);
        foreach (var arg in Args) hash.Add(arg);
        return hash.ToHashCode();
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
