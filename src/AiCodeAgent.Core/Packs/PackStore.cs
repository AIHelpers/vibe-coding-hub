using System.Text.Json;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Context;

namespace AiCodeAgent.Core.Packs;

/// <summary>
/// The packs installed on this machine (<c>~/.aiagent/packs/&lt;name&gt;@&lt;ref&gt;/</c> plus <c>installed.json</c>) and the
/// project's <c>.aiagent/packs.json</c>. Several refs of one pack can be installed side by side; a project that declares a
/// ref in <c>packs.json</c> gets exactly that one, every other project gets the default copy.
/// </summary>
public sealed class PackStore
{
    public const string InstalledFileName = "installed.json";
    public const string ProjectFileName = "packs.json";

    private readonly string _root;
    private readonly object _gate = new();

    public PackStore(string? root = null) => _root = root ?? AppPaths.Packs;

    public string Root => _root;

    /// <summary>Where a pack is cloned before it is checked and moved into place.</summary>
    public string StagingRoot => Path.Combine(_root, ".staging");

    private string InstalledFile => Path.Combine(_root, InstalledFileName);

    public string DirectoryOf(InstalledPack pack) => Path.Combine(_root, pack.Directory);

    public IReadOnlyList<InstalledPack> List()
    {
        lock (_gate) return ReadInstalled().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.InstalledAt).ToList();
    }

    public InstalledPack? Find(string name, string? gitRef = null)
    {
        var matches = List().Where(p => Same(p.Name, name)).ToList();
        if (gitRef == null) return matches.FirstOrDefault(p => p.IsDefault) ?? matches.LastOrDefault();
        return matches.FirstOrDefault(p => Same(p.Ref ?? string.Empty, gitRef) || Same(p.RefLabel, gitRef) || p.Commit.StartsWith(gitRef, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Records <paramref name="pack"/>. A second copy of the same name and directory replaces the first.</summary>
    public void Add(InstalledPack pack, bool makeDefault)
    {
        lock (_gate)
        {
            var all = ReadInstalled();
            all.RemoveAll(p => Same(p.Name, pack.Name) && Same(p.Directory, pack.Directory));
            var anyOther = all.Any(p => Same(p.Name, pack.Name));
            if (makeDefault || !anyOther)
                all = all.Select(p => Same(p.Name, pack.Name) ? p with { IsDefault = false } : p).ToList();
            all.Add(pack with { IsDefault = makeDefault || !anyOther });
            WriteInstalled(all);
        }
    }

    /// <summary>Deletes a pack's folders and records. Without <paramref name="gitRef"/> every installed ref of the name goes. Returns what was removed.</summary>
    public IReadOnlyList<InstalledPack> Remove(string name, string? gitRef = null)
    {
        lock (_gate)
        {
            var all = ReadInstalled();
            var named = all.Where(p => Same(p.Name, name)).ToList();
            // An exact ref or folder label wins over a commit prefix, so removing the copy "abc123def456" never takes a pinned copy that happens to be at the same commit.
            var doomed = gitRef == null
                ? named
                : named.Where(p => Same(p.Ref ?? string.Empty, gitRef) || Same(p.RefLabel, gitRef)).ToList();
            if (gitRef != null && doomed.Count == 0)
                doomed = named.Where(p => p.Commit.StartsWith(gitRef, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var p in doomed)
            {
                DeleteDirectory(DirectoryOf(p));
                all.Remove(p);
            }
            // Keep one default per name.
            foreach (var group in all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                if (!group.Any(p => p.IsDefault) && group.OrderBy(p => p.InstalledAt).Last() is { } newest)
                {
                    var i = all.IndexOf(newest);
                    all[i] = newest with { IsDefault = true };
                }
            WriteInstalled(all);
            return doomed;
        }
    }

    /// <summary>
    /// The pack folders to read for a project: one per pack name. The ref the project declares in <c>packs.json</c> if that ref is
    /// installed, otherwise the default copy. Sorted by name; on a clash between two packs the later name wins.
    /// </summary>
    public IReadOnlyList<PackSource> Sources(string? projectDirectory = null)
    {
        var declared = projectDirectory == null ? new List<PackReference>() : ReadProjectFile(projectDirectory);
        var result = new List<PackSource>();
        foreach (var group in List().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var want = declared.FirstOrDefault(d => Same(d.Name, group.Key))?.Ref;
            var chosen = (want == null ? null : group.FirstOrDefault(p => Same(p.Ref ?? string.Empty, want) || p.Commit.StartsWith(want, StringComparison.OrdinalIgnoreCase)))
                         ?? group.FirstOrDefault(p => p.IsDefault) ?? group.Last();
            var dir = DirectoryOf(chosen);
            if (Directory.Exists(dir)) result.Add(new PackSource(chosen.Name, dir, chosen.RefLabel, chosen.Version));
        }
        return result;
    }

    // ------------------------------------------------------------------ project file

    public static string ProjectFile(string projectDirectory) => Path.Combine(projectDirectory, ".aiagent", ProjectFileName);

    /// <summary>The packs a project declares; empty when it has no <c>packs.json</c> or the file is unreadable.</summary>
    public static List<PackReference> ReadProjectFile(string projectDirectory)
    {
        var file = ProjectFile(projectDirectory);
        try
        {
            if (!File.Exists(file)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Array) return new();
            return packs.EnumerateArray()
                .Select(e => JsonSerializer.Deserialize<PackReference>(e.GetRawText(), PackManifest.JsonOptions))
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Url))
                .Select(r => r!)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Adds or replaces (by name) an entry in the project's <c>packs.json</c>.</summary>
    public static void SaveToProjectFile(string projectDirectory, PackReference reference)
    {
        var all = ReadProjectFile(projectDirectory);
        all.RemoveAll(r => Same(r.Name, reference.Name));
        all.Add(reference);
        var file = ProjectFile(projectDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(new { packs = all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase) }, PackManifest.JsonOptions);
        File.WriteAllText(file, json + "\n", new System.Text.UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ files

    private List<InstalledPack> ReadInstalled()
    {
        try
        {
            if (!File.Exists(InstalledFile)) return new();
            return JsonSerializer.Deserialize<List<InstalledPack>>(File.ReadAllText(InstalledFile), PackManifest.JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void WriteInstalled(List<InstalledPack> all)
    {
        Directory.CreateDirectory(_root);
        LocalFileStore.WriteAllTextAtomicAsync(InstalledFile, JsonSerializer.Serialize(all, PackManifest.JsonOptions) + "\n").GetAwaiter().GetResult();
    }

    /// <summary>Deletes a folder, also when git left read-only files in it (Windows).</summary>
    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Directory.Delete(path, recursive: true);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
