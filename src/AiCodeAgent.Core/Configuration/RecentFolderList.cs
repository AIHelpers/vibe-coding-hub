namespace AiCodeAgent.Core.Configuration;

/// <summary>Keeps the "recent folders" list: newest first, no duplicates, capped.</summary>
public static class RecentFolderList
{
    public const int DefaultMax = 8;

    /// <summary>Moves <paramref name="path"/> to the front (adding it if new) and trims the list to <paramref name="max"/>.</summary>
    public static void Push(List<string> list, string? path, int max = DefaultMax)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
        list.RemoveAll(x => string.Equals(Normalize(x), normalized, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, normalized);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    /// <summary>The entries whose folder still exists, in order.</summary>
    public static IReadOnlyList<string> Existing(IEnumerable<string>? list) =>
        (list ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();

    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        var root = Path.GetPathRoot(trimmed);
        return trimmed.Length > (root?.Length ?? 0) ? trimmed.TrimEnd('\\', '/') : trimmed;
    }
}
