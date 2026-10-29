using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Packs;

/// <summary>What a pack requires of the app. <c>app</c> is a version range such as <c>&gt;=1.3</c> or <c>&gt;=1.3 &lt;2</c>.</summary>
public sealed record PackRequires
{
    public string? App { get; init; }
}

/// <summary><c>pack.json</c> at the root of a pack repository.</summary>
public sealed record PackManifest
{
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? License { get; init; }
    public PackRequires? Requires { get; init; }

    public const string FileName = "pack.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Reads and validates <c>pack.json</c> in <paramref name="packRoot"/>. Returns null (with the reasons in <paramref name="errors"/>) when it is missing or invalid.</summary>
    public static PackManifest? TryRead(string packRoot, List<string> errors)
    {
        var file = Path.Combine(packRoot, FileName);
        if (!File.Exists(file))
        {
            errors.Add($"The repository has no {FileName}; it is not a pack.");
            return null;
        }
        PackManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(file), JsonOptions); }
        catch (JsonException ex) { errors.Add($"{FileName} is not valid JSON: {ex.Message}"); return null; }
        if (manifest == null) { errors.Add($"{FileName} is empty."); return null; }

        var nameError = LocalFileStore.ValidateName(manifest.Name, "Pack name");
        if (nameError != null) errors.Add(nameError);
        if (!string.IsNullOrWhiteSpace(manifest.Version) && !SemanticVersion.TryParse(manifest.Version, out _))
            errors.Add($"Version '{manifest.Version}' is not a version number (use 1.2.3).");
        if (!string.IsNullOrWhiteSpace(manifest.Requires?.App) && !PackVersionRange.IsValid(manifest.Requires!.App!))
            errors.Add($"requires.app '{manifest.Requires.App}' is not a version range (use for example >=1.3 or >=1.3 <2).");
        return errors.Count == 0 ? manifest : null;
    }
}

/// <summary>Version ranges for <c>requires.app</c>: comparators <c>&gt;= &gt; &lt;= &lt; =</c> (or a bare version) joined by spaces or commas; every one must hold.</summary>
public static class PackVersionRange
{
    private static IEnumerable<(string Op, string Version)> Parts(string range)
    {
        foreach (var raw in range.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var op = raw.StartsWith(">=") ? ">=" : raw.StartsWith("<=") ? "<=" : raw.StartsWith('>') ? ">" : raw.StartsWith('<') ? "<" : raw.StartsWith('=') ? "=" : "=";
            var version = raw.TrimStart('>', '<', '=');
            yield return (op, version);
        }
    }

    public static bool IsValid(string range)
    {
        var parts = Parts(range).ToList();
        return parts.Count > 0 && parts.All(p => SemanticVersion.TryParse(p.Version, out _));
    }

    public static bool Satisfies(string range, SemanticVersion current)
    {
        foreach (var (op, text) in Parts(range))
        {
            if (!SemanticVersion.TryParse(text, out var wanted) || wanted == null) return false;
            var cmp = current.CompareTo(wanted);
            var ok = op switch { ">=" => cmp >= 0, ">" => cmp > 0, "<=" => cmp <= 0, "<" => cmp < 0, _ => cmp == 0 };
            if (!ok) return false;
        }
        return true;
    }
}

/// <summary>A pack on disk that the registries read from: its <c>skills/</c>, <c>characters/</c> and <c>pipelines/</c> folders. Read-only.</summary>
public sealed record PackSource(string Name, string Root, string? Ref = null, string? Version = null)
{
    public string SkillsDirectory => Path.Combine(Root, "skills");
    public string CharactersDirectory => Path.Combine(Root, "characters");
    public string PipelinesDirectory => Path.Combine(Root, "pipelines");

    /// <summary>The scope label shown next to a skill, character or flow from this pack: <c>pack:dotnet-team-pack</c>.</summary>
    public string ScopeLabel => "pack:" + Name;
}

/// <summary>One installed pack (<c>~/.aiagent/packs/installed.json</c>).</summary>
public sealed record InstalledPack
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    /// <summary>The ref that was asked for (tag, commit or branch); null = the repository's default branch.</summary>
    public string? Ref { get; init; }
    /// <summary>The commit that is installed.</summary>
    public string Commit { get; init; } = string.Empty;
    /// <summary>True when <see cref="Ref"/> is a tag or a commit, so the pack never changes on update.</summary>
    public bool Pinned { get; init; }
    public string? Version { get; init; }
    /// <summary>Folder name under the packs root: <c>&lt;name&gt;@&lt;ref&gt;</c>.</summary>
    public string Directory { get; init; } = string.Empty;
    public DateTimeOffset InstalledAt { get; init; }
    /// <summary>The copy used when a project does not declare a ref of its own for this pack.</summary>
    public bool IsDefault { get; init; }

    [JsonIgnore]
    public string ShortCommit => Commit.Length > 12 ? Commit[..12] : Commit;

    /// <summary>"v1.2.0" for a pinned ref, otherwise the short commit.</summary>
    [JsonIgnore]
    public string RefLabel => Pinned && !string.IsNullOrEmpty(Ref) ? Ref! : ShortCommit;
}

/// <summary>A pack a project depends on (<c>.aiagent/packs.json</c>): installed for everyone with <c>pack sync</c>.</summary>
public sealed record PackReference
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    /// <summary>Tag or commit. A branch works but is not reproducible.</summary>
    public string? Ref { get; init; }
}
