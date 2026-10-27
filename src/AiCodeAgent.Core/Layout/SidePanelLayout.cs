using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Layout;

/// <summary>Ids of the panels that dock into the side-panel host, next to the editor and chat (E4-S1).</summary>
public static class SidePanelIds
{
    public const string Changes = "changes";
    public const string Tasks = "tasks";
    public const string Checkpoints = "checkpoints";
    public const string Sessions = "sessions";
    public const string Dashboard = "dashboard";
    /// <summary>The autonomous plan. The agent opens it, so it is never remembered between runs.</summary>
    public const string Plan = "plan";

    public static readonly IReadOnlyList<string> All = new[] { Changes, Tasks, Checkpoints, Sessions, Dashboard, Plan };

    public static bool IsKnown(string? id) => id != null && All.Contains(id);

    /// <summary>True for panels the user opened on purpose and that are worth reopening next time.</summary>
    public static bool IsRestorable(string? id) => IsKnown(id) && id != Plan;
}

/// <summary>What is remembered for one workspace: which side panel was open and how wide it was.</summary>
public class WorkspacePanelState
{
    /// <summary>The open side panel (see <see cref="SidePanelIds"/>), or null when it was closed.</summary>
    public string? ActivePanel { get; set; }

    public double Width { get; set; } = SidePanelLayout.DefaultWidth;

    /// <summary>When this entry was written (UTC); the oldest entries are dropped first.</summary>
    public DateTimeOffset Updated { get; set; }
}

public static class SidePanelLayout
{
    public const double DefaultWidth = 380;
    public const double MinWidth = 280;
    public const double MaxWidth = 720;

    /// <summary>Room that must stay for the editor/chat area next to the activity bar and the panel.</summary>
    public const double ReservedWidth = 520;

    /// <summary>Keeps a width inside the allowed range; a missing or broken value becomes the default.
    /// <paramref name="availableWidth"/> is the window width, so a narrow window never lets the panel eat the chat.</summary>
    public static double ClampWidth(double width, double availableWidth = double.PositiveInfinity)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            width = DefaultWidth;
        var max = MaxWidth;
        if (!double.IsNaN(availableWidth) && !double.IsInfinity(availableWidth) && availableWidth > 0)
            max = Math.Min(MaxWidth, Math.Max(MinWidth, availableWidth - ReservedWidth));
        return Math.Min(max, Math.Max(MinWidth, width));
    }

    /// <summary>The key a workspace is stored under: full path without a trailing separator.</summary>
    public static string WorkspaceKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception) { full = path.Trim(); }
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }
}

/// <summary>
/// Remembers the active side panel and its width per workspace inside <see cref="UiConfiguration.WorkspacePanels"/>.
/// It only edits the in-memory configuration; the caller saves it.
/// </summary>
public sealed class SidePanelMemory
{
    public const int MaxEntries = 50;

    private readonly UiConfiguration _ui;

    public SidePanelMemory(UiConfiguration ui) => _ui = ui;

    private static bool SameKey(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private string? FindKey(string workspaceKey) =>
        _ui.WorkspacePanels.Keys.FirstOrDefault(k => SameKey(k, workspaceKey));

    /// <summary>The remembered state of a workspace, or null when it never had one. Unknown panels and bad widths are repaired.</summary>
    public WorkspacePanelState? Get(string? workspace)
    {
        var key = SidePanelLayout.WorkspaceKey(workspace);
        if (key.Length == 0) return null;
        var found = FindKey(key);
        if (found == null || _ui.WorkspacePanels[found] is not { } stored) return null;
        return new WorkspacePanelState
        {
            ActivePanel = SidePanelIds.IsRestorable(stored.ActivePanel) ? stored.ActivePanel : null,
            Width = SidePanelLayout.ClampWidth(stored.Width),
            Updated = stored.Updated
        };
    }

    /// <summary>Stores the panel (null = closed) and width for a workspace. Returns false when nothing changed.</summary>
    public bool Remember(string? workspace, string? activePanel, double width, DateTimeOffset? now = null)
    {
        var key = SidePanelLayout.WorkspaceKey(workspace);
        if (key.Length == 0) return false;

        var panel = SidePanelIds.IsRestorable(activePanel) ? activePanel : null;
        var clamped = SidePanelLayout.ClampWidth(width);

        var existingKey = FindKey(key);
        if (existingKey != null && _ui.WorkspacePanels[existingKey] is { } old
            && old.ActivePanel == panel && Math.Abs(old.Width - clamped) < 0.5)
            return false;

        if (existingKey != null) _ui.WorkspacePanels.Remove(existingKey);
        _ui.WorkspacePanels[key] = new WorkspacePanelState
        {
            ActivePanel = panel,
            Width = clamped,
            Updated = now ?? DateTimeOffset.UtcNow
        };

        while (_ui.WorkspacePanels.Count > MaxEntries)
        {
            var oldest = _ui.WorkspacePanels.OrderBy(p => p.Value?.Updated ?? DateTimeOffset.MinValue).First().Key;
            _ui.WorkspacePanels.Remove(oldest);
        }
        return true;
    }
}
