using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Scripted;

/// <summary>
/// Finds scenario files. Sources, highest precedence first (a scenario name seen earlier wins):
/// the provider's <c>BaseUrl</c> when it is a folder or a file, <c>AIAGENT_SCRIPTED_DIR</c>, <c>~/.aiagent/scenarios</c>,
/// and the scenarios built into the app (<c>fix-overdue</c>, <c>explain-project</c>, <c>feature-team</c>).
/// Files are JSON; comments and trailing commas are allowed; property names are snake_case.
/// </summary>
public static class ScenarioLoader
{
    public const string DirectoryVariable = "AIAGENT_SCRIPTED_DIR";
    public const string BuiltInSource = "built-in";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The scenarios from every source; files that cannot be read are reported in <paramref name="problems"/> and skipped.</summary>
    public static IReadOnlyList<ScriptedScenario> LoadAll(string? configuredPath = null, ILogger? logger = null, List<string>? problems = null)
    {
        var result = new List<ScriptedScenario>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(ScriptedScenario? scenario, string source)
        {
            if (scenario == null) return;
            if (string.IsNullOrWhiteSpace(scenario.Name))
            {
                Problem($"{source}: the scenario has no \"name\".");
                return;
            }
            if (!seen.Add(scenario.Name)) return; // a higher-precedence source already defined it
            result.Add(scenario with { Source = source });
        }

        void Problem(string message)
        {
            logger?.LogWarning("Scripted scenario skipped: {Message}", message);
            problems?.Add(message);
        }

        foreach (var path in SearchPaths(configuredPath))
        {
            foreach (var file in FilesIn(path))
            {
                try { Add(Parse(File.ReadAllText(file)), file); }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    Problem($"{file}: {ex.Message}");
                }
            }
        }

        var assembly = typeof(ScenarioLoader).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Scripted.Scenarios.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                Add(Parse(reader.ReadToEnd()), BuiltInSource);
            }
            catch (JsonException ex)
            {
                Problem($"{resource}: {ex.Message}");
            }
        }
        return result;
    }

    public static ScriptedScenario? Parse(string json) => JsonSerializer.Deserialize<ScriptedScenario>(json, JsonOptions);

    private static IEnumerable<string> SearchPaths(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && (Directory.Exists(configuredPath) || File.Exists(configuredPath)))
            yield return configuredPath;
        var fromEnvironment = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;
        yield return Path.Combine(AppPaths.UserRoot, "scenarios");
    }

    private static IEnumerable<string> FilesIn(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }
}
