namespace AiCodeAgent.Core.Board;

/// <summary>
/// A well-known coding agent that speaks the Agent Client Protocol. <see cref="Executable"/> is the program whose presence on the
/// PATH shows the agent is installed; <see cref="Command"/> and <see cref="Args"/> start the ACP server. Claude Code and Codex reach
/// ACP through a small adapter package that <c>npx</c> fetches on first use, so they also need Node.js; Gemini CLI speaks ACP itself.
/// </summary>
public sealed record ExternalAgentPreset(
    string Id, string Name, string Executable, string Command, IReadOnlyList<string> Args, string Setup)
{
    /// <summary>Programs that must be found on the PATH for this preset to work.</summary>
    public IEnumerable<string> Requirements =>
        string.Equals(Command, Executable, StringComparison.OrdinalIgnoreCase) ? new[] { Executable } : new[] { Executable, Command };
}

/// <summary>The presets Mission Control offers. They spend the subscription or key the person already set up in that tool.</summary>
public static class ExternalAgentPresets
{
    public static readonly ExternalAgentPreset ClaudeCode = new(
        "claude-code", "Claude Code", "claude", "npx", new[] { "-y", "@agentclientprotocol/claude-agent-acp" },
        "Install Claude Code and sign in once (run `claude` in a terminal). Node.js is needed for the ACP adapter.");

    public static readonly ExternalAgentPreset Codex = new(
        "codex", "Codex", "codex", "npx", new[] { "-y", "@zed-industries/codex-acp" },
        "Install the Codex CLI and sign in once (run `codex` in a terminal). Node.js is needed for the ACP adapter.");

    public static readonly ExternalAgentPreset GeminiCli = new(
        "gemini-cli", "Gemini CLI", "gemini", "gemini", new[] { "--acp" },
        "Install Gemini CLI and sign in once (run `gemini` in a terminal).");

    public static IReadOnlyList<ExternalAgentPreset> All { get; } = new[] { ClaudeCode, Codex, GeminiCli };

    public static ExternalAgentPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Whether a preset can be started on this machine, and why not when it cannot.</summary>
public sealed record DetectedAgent(ExternalAgentPreset Preset, bool Installed, string Detail);

/// <summary>Looks for the programs of the presets on the PATH.</summary>
public sealed class ExternalAgentDetector
{
    private readonly Func<string, string?> _find;

    /// <param name="find">Returns the full path of a program or null; defaults to a search of the PATH.</param>
    public ExternalAgentDetector(Func<string, string?>? find = null) => _find = find ?? (name => FindOnPath(name));

    public IReadOnlyList<DetectedAgent> DetectAll() => ExternalAgentPresets.All.Select(Detect).ToList();

    public DetectedAgent Detect(ExternalAgentPreset preset)
    {
        var cli = _find(preset.Executable);
        if (cli == null)
            return new DetectedAgent(preset, false, $"'{preset.Executable}' was not found on the PATH. {preset.Setup}");

        foreach (var requirement in preset.Requirements)
        {
            if (_find(requirement) == null)
                return new DetectedAgent(preset, false, $"'{requirement}' was not found on the PATH. {preset.Setup}");
        }
        return new DetectedAgent(preset, true, $"Found {cli}");
    }

    /// <summary>The full path of <paramref name="name"/> in the PATH folders (with the Windows executable extensions), or null.</summary>
    public static string? FindOnPath(string name, string? path = null, string? pathExt = null, bool? windows = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var folders = (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = isWindows
            ? (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        foreach (var folder in folders)
        {
            try
            {
                var plain = Path.Combine(folder.Trim('"'), name);
                if (!isWindows && File.Exists(plain)) return plain;
                if (isWindows)
                {
                    foreach (var ext in extensions)
                    {
                        var candidate = plain + ext;
                        if (File.Exists(candidate)) return candidate;
                    }
                    if (Path.HasExtension(plain) && File.Exists(plain)) return plain;
                }
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }
}
