namespace AiCodeAgent.Screenshots;

/// <summary>
/// One documentation screenshot. The numbered file name (<c>05-approval.png</c>) is what the articles link to, so
/// keep the table below in step with <c>docs/content-kit/images</c>: rename here, never in the folder.
/// </summary>
/// <param name="Number">Position in the image set (and the file name prefix).</param>
/// <param name="Slug">Short name after the number.</param>
/// <param name="Title">What the image shows (used in logs and docs/screenshots.md).</param>
/// <param name="ForcesLightTheme">The scene is the light-theme shot: it switches to the light theme itself.</param>
internal sealed record SceneInfo(int Number, string Slug, string Title, bool ForcesLightTheme = false)
{
    public string FileName => $"{Number:00}-{Slug}.png";

    /// <summary>True when <paramref name="filter"/> names this scene: its number, slug or file name.</summary>
    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return false;
        filter = filter.Trim();
        if (filter.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) filter = filter[..^4];
        if (int.TryParse(filter, out var number)) return number == Number;
        return string.Equals(filter, Slug, StringComparison.OrdinalIgnoreCase)
               || string.Equals(filter, $"{Number:00}-{Slug}", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The 22 documentation screenshots, in file-number order.</summary>
internal static class SceneCatalog
{
    public const int Width = 1600;
    public const int Height = 900;

    public static IReadOnlyList<SceneInfo> All { get; } = new SceneInfo[]
    {
        new(1, "main-window", "The main window with the sample project open and the guided checklist"),
        new(2, "sample-task", "The first task typed into the chat box"),
        new(3, "explorer", "The file explorer next to the chat"),
        new(4, "screens-mode", "Screens mode: one panel fills the window"),
        new(5, "approval", "The approval dialog with the diff of the edit"),
        new(6, "result", "The finished run: tool cards, verification and the summary"),
        new(7, "diff", "The side-by-side diff of the changed file"),
        new(8, "commit", "Source control: staged change and commit message"),
        new(9, "project-knowledge", "Project Knowledge: AGENTS.md"),
        new(10, "skills", "Project Knowledge: a skill"),
        new(11, "character", "Project Knowledge: a character with inherited skills"),
        new(12, "add-skill", "Add skill to a character"),
        new(13, "flow-canvas", "The feature-team flow on the canvas"),
        new(14, "inspector", "A flow step selected, with its inspector"),
        new(15, "flow-running", "The flow running: parallel steps"),
        new(16, "flow-loop", "The review sends the work back (loop)"),
        new(17, "flow-complete", "The flow finished with the release notes"),
        new(18, "settings", "Settings"),
        new(19, "command-palette", "The command palette"),
        new(20, "checkpoints", "Checkpoints of the run"),
        new(21, "background-tasks", "Background tasks"),
        new(22, "light-theme", "The main window in the light theme", ForcesLightTheme: true),
    };

    /// <summary>The scenes named by <paramref name="filters"/> (all when empty), in file-number order.</summary>
    public static IReadOnlyList<SceneInfo> Select(IReadOnlyCollection<string> filters, out IReadOnlyList<string> unknown)
    {
        if (filters.Count == 0)
        {
            unknown = Array.Empty<string>();
            return All;
        }

        unknown = filters.Where(f => !All.Any(s => s.Matches(f))).ToList();
        return All.Where(s => filters.Any(s.Matches)).ToList();
    }
}
