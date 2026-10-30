using System.Text.RegularExpressions;

namespace AiCodeAgent.Bench;

/// <summary>
/// One way of solving a task: <c>single</c> (one agent, chat mode, <c>aiagent run</c>) or a flow
/// (<c>aiagent pipeline run --pipeline X</c>), written <c>X-flow</c> or <c>flow:X</c>.
/// </summary>
internal sealed record BenchConfig(string Name, string? Pipeline)
{
    public const string Single = "single";

    public bool IsSingle => Pipeline == null;

    public static bool TryParse(string text, out BenchConfig? config, out string? error)
    {
        config = null;
        error = null;
        text = text.Trim();
        if (text.Length == 0) { error = "Empty configuration name."; return false; }
        if (!Regex.IsMatch(text, @"^[A-Za-z0-9][A-Za-z0-9._:\-]*$"))
        {
            error = $"Invalid configuration '{text}'. Use 'single', '<pipeline>-flow' or 'flow:<pipeline>'.";
            return false;
        }
        if (text.Equals(Single, StringComparison.OrdinalIgnoreCase)) { config = new BenchConfig(Single, null); return true; }
        if (text.StartsWith("flow:", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
        {
            config = new BenchConfig(text, text[5..]);
            return true;
        }
        if (text.EndsWith("-flow", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
        {
            config = new BenchConfig(text, text[..^5]);
            return true;
        }
        error = $"Unknown configuration '{text}'. Use 'single', '<pipeline>-flow' (for example bug-fix-flow) or 'flow:<pipeline>'.";
        return false;
    }

    public static IReadOnlyList<BenchConfig> ParseList(IEnumerable<string> names)
    {
        var result = new List<BenchConfig>();
        foreach (var name in names)
        {
            if (!TryParse(name, out var config, out var error)) throw new BenchException(error!);
            if (result.Any(c => c.Name.Equals(config!.Name, StringComparison.OrdinalIgnoreCase)))
                throw new BenchException($"Configuration '{config!.Name}' is listed twice.");
            result.Add(config!);
        }
        if (result.Count == 0) throw new BenchException("No configurations given.");
        return result;
    }
}
