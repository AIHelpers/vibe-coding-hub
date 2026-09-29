using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Tools.Verification;

/// <summary>One verification stage, e.g. <c>build</c> → <c>dotnet build</c>.</summary>
public sealed record VerifyStage(string Name, string Command);

/// <summary>
/// Works out how to build/test/lint a project. Sources, in priority order:
/// 1. <c>.aiagent/verify.json</c>: <c>{"commands":[{"name":"build","command":"make build"}, ...]}</c>
/// 2. "Build command:", "Test command:", "Lint command:" lines in AGENTS.md
/// 3. Auto-detection from project files (dotnet, npm, Python, Cargo, Go).
/// </summary>
public static class VerifyCommandDetector
{
    private static readonly Regex AgentsLine = new(
        @"^\s*(?:[-*]\s*)?(build|test|lint)\s+command\s*:\s*`?([^`\r\n]+?)`?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string[] Order = ["build", "test", "lint"];

    public static List<VerifyStage> Detect(string directory)
    {
        var fromConfig = FromConfigFile(directory);
        if (fromConfig.Count > 0) return fromConfig;

        var fromAgents = FromAgentsMd(directory);
        if (fromAgents.Count > 0) return fromAgents;

        return AutoDetect(directory);
    }

    internal static List<VerifyStage> FromConfigFile(string directory)
    {
        var path = Path.Combine(directory, ".aiagent", "verify.json");
        if (!File.Exists(path)) return new();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("commands", out var cmds) || cmds.ValueKind != JsonValueKind.Array)
                return new();
            var list = new List<VerifyStage>();
            foreach (var c in cmds.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                var command = c.TryGetProperty("command", out var cm) ? cm.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(command))
                    list.Add(new VerifyStage(name!, command!));
            }
            return list;
        }
        catch { return new(); }
    }

    internal static List<VerifyStage> FromAgentsMd(string directory)
    {
        var path = Path.Combine(directory, "AGENTS.md");
        if (!File.Exists(path)) return new();
        string text;
        try { text = File.ReadAllText(path); } catch { return new(); }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AgentsLine.Matches(text))
            found.TryAdd(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim());

        return Order.Where(found.ContainsKey).Select(k => new VerifyStage(k, found[k])).ToList();
    }

    internal static List<VerifyStage> AutoDetect(string directory)
    {
        bool Has(string pattern) =>
            Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any();

        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj"))
            return [new("build", "dotnet build"), new("test", "dotnet test")];

        var pkg = Path.Combine(directory, "package.json");
        if (File.Exists(pkg))
        {
            var list = new List<VerifyStage>();
            var scripts = ReadNpmScripts(pkg);
            if (scripts.Contains("build")) list.Add(new("build", "npm run build"));
            if (scripts.Contains("lint")) list.Add(new("lint", "npm run lint"));
            if (scripts.Contains("test")) list.Add(new("test", "npm test"));
            return list;
        }

        if (File.Exists(Path.Combine(directory, "Cargo.toml")))
            return [new("build", "cargo build"), new("test", "cargo test")];

        if (File.Exists(Path.Combine(directory, "go.mod")))
            return [new("build", "go build ./..."), new("test", "go test ./...")];

        if (File.Exists(Path.Combine(directory, "pyproject.toml")) || File.Exists(Path.Combine(directory, "pytest.ini"))
            || File.Exists(Path.Combine(directory, "requirements.txt")) || Directory.Exists(Path.Combine(directory, "tests")))
            return [new("test", "python -m pytest -x -q")];

        return new();
    }

    private static HashSet<string> ReadNpmScripts(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }
}

/// <summary>Condenses long build/test output to the lines a model needs to fix the failure.</summary>
public static class FailureSummarizer
{
    private static readonly Regex Interesting = new(
        @"\berror\b|\bfailed\b|\bfail\b|exception|assert|✗|✘|FAILED|panic:|Traceback|expected|CS\d{4}|TS\d{4}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Summarize(string output, int maxLines = 60, int maxChars = 6000)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var picked = lines.Where(l => Interesting.IsMatch(l)).Select(l => l.TrimEnd()).Distinct().Take(maxLines).ToList();
        var text = picked.Count > 0
            ? string.Join("\n", picked)
            : string.Join("\n", lines.Skip(Math.Max(0, lines.Length - maxLines)));
        if (text.Length > maxChars) text = text[..maxChars] + "\n...[truncated]";
        return text.Trim();
    }
}
