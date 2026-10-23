using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Repairs user role-preset files that list unknown tool names (<c>skills doctor --fix</c>).
/// Misspelt names that have a canonical form are rewritten, and the exact old test
/// fixture is deleted. After a repair, planning finds nothing, so a second run changes nothing.
/// </summary>
public static class PresetRepair
{
    /// <summary>System prompt that the old test suite wrote into <c>presets/tester.json</c>.</summary>
    public const string TestFixturePrompt = "You are the TESTER agent.";

    public enum RepairKind { RewriteTools, DeleteFixture }

    public sealed record RepairAction(string Role, string FilePath, RepairKind Kind, string Description);

    /// <summary>Lists what <see cref="Apply"/> would change. Files that cannot be read are skipped.</summary>
    public static IReadOnlyList<RepairAction> Plan(RolePresetLoader presets)
    {
        var actions = new List<RepairAction>();
        foreach (var (role, file) in presets.UserPresetFiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var preset = TryRead(file);
            if (preset == null) continue;

            if (preset.SystemPrompt == TestFixturePrompt)
            {
                actions.Add(new RepairAction(role, file, RepairKind.DeleteFixture,
                    $"Delete {file} (exact match of the old test fixture)."));
                continue;
            }

            var fixes = (preset.AllowedTools ?? new List<string>())
                .Where(t => !KnownToolNames.IsKnown(t) && KnownToolNames.Suggest(t) != null)
                .Select(t => $"{t} → {KnownToolNames.Suggest(t)}")
                .ToList();
            if (fixes.Count > 0)
                actions.Add(new RepairAction(role, file, RepairKind.RewriteTools,
                    $"Rewrite tool names in {file}: {string.Join(", ", fixes)}."));
        }
        return actions;
    }

    /// <summary>Carries out one action from <see cref="Plan"/>.</summary>
    public static void Apply(RepairAction action)
    {
        if (action.Kind == RepairKind.DeleteFixture)
        {
            File.Delete(action.FilePath);
            return;
        }

        // Edit only the allowedTools array so every other field in the file is kept as it is.
        var root = JsonNode.Parse(File.ReadAllText(action.FilePath))?.AsObject()
            ?? throw new InvalidDataException($"{action.FilePath} is not a JSON object.");
        var key = root.Select(p => p.Key)
            .FirstOrDefault(k => string.Equals(k, "allowedTools", StringComparison.OrdinalIgnoreCase));
        if (key == null || root[key] is not JsonArray array) return;

        var names = array.Select(n => n?.GetValue<string>() ?? string.Empty).ToList();
        root[key] = new JsonArray(Canonicalize(names).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        File.WriteAllText(action.FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Replaces misspelt names that have a canonical form, and drops duplicates.</summary>
    public static List<string> Canonicalize(IEnumerable<string> names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            var fixedName = KnownToolNames.IsKnown(name) ? name : (KnownToolNames.Suggest(name) ?? name);
            if (!result.Contains(fixedName, StringComparer.Ordinal)) result.Add(fixedName);
        }
        return result;
    }

    private static AgentRolePreset? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentRolePreset>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            return null;
        }
    }
}
