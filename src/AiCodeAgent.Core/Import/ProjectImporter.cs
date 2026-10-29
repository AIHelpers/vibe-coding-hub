using System.Text;
using AiCodeAgent.Core.Context;

namespace AiCodeAgent.Core.Import;

/// <summary>The tools whose setup can be imported (E6-S2).</summary>
public enum ImportSource
{
    /// <summary><c>.claude/skills/*/SKILL.md</c> in the project and <c>~/.claude/skills</c>: same format, copied with the front matter kept.</summary>
    ClaudeSkills,
    /// <summary><c>CLAUDE.md</c> in the repo root: appended to AGENTS.md as a section.</summary>
    ClaudeMd,
    /// <summary><c>.cursor/rules/*.mdc</c> and <c>.cursorrules</c>.</summary>
    CursorRules,
    /// <summary><c>.kiro/steering/*.md</c>.</summary>
    KiroSteering,
    /// <summary><c>.github/copilot-instructions.md</c>: appended to AGENTS.md as a section.</summary>
    CopilotInstructions
}

public enum ImportTarget
{
    /// <summary>A skill in the project's <c>.aiagent/skills</c>.</summary>
    ProjectSkill,
    /// <summary>A skill in the user's global skills folder (from <c>~/.claude/skills</c>).</summary>
    GlobalSkill,
    /// <summary>A section appended to the project's AGENTS.md.</summary>
    AgentsMdSection,
    /// <summary>A section of the pinned-style <c>team-conventions</c> skill (rules that always apply).</summary>
    TeamConventionsSection
}

public enum ImportStatus { New, AlreadyImported }

/// <summary>One thing the import would bring over.</summary>
public sealed class ImportItem
{
    /// <summary>Stable key written into the imported file (<c>imported-from</c>); how a second run knows it is already done.</summary>
    public required string Id { get; init; }
    public required ImportSource Source { get; init; }
    public required ImportTarget Target { get; init; }
    /// <summary>What the user sees: the source file relative to its root.</summary>
    public required string Label { get; init; }
    public required string SourcePath { get; init; }
    /// <summary>Skill name (kebab-case) or section title.</summary>
    public required string TargetName { get; init; }
    public string Description { get; init; } = string.Empty;
    /// <summary>Skill: the SKILL.md text to write. Section: the text of the section body.</summary>
    public string Content { get; init; } = string.Empty;
    /// <summary>For copied Claude skills: the folder whose other files are copied along.</summary>
    public string? SourceDirectory { get; init; }
    public ImportStatus Status { get; init; } = ImportStatus.New;
    /// <summary>Why the name differs from the source, large files, and similar remarks.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Checkbox state in the preview. Only selected, new items are imported.</summary>
    public bool Selected { get; set; } = true;

    public bool IsNew => Status == ImportStatus.New;

    public string TargetText => Target switch
    {
        ImportTarget.ProjectSkill => $"skill '{TargetName}'",
        ImportTarget.GlobalSkill => $"global skill '{TargetName}'",
        ImportTarget.AgentsMdSection => $"AGENTS.md section '{TargetName}'",
        _ => $"team-conventions section '{TargetName}'"
    };
}

public sealed class ImportPlan
{
    public IReadOnlyList<ImportItem> Items { get; init; } = Array.Empty<ImportItem>();
    /// <summary>Sources that were found but cannot be imported (empty files and the like).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsEmpty => Items.Count == 0;
    public IEnumerable<ImportItem> Pending => Items.Where(i => i.IsNew && i.Selected);

    public string Render()
    {
        if (Items.Count == 0) return "Nothing to import: no Claude Code, Cursor, Kiro or Copilot files found.";
        var sb = new StringBuilder();
        foreach (var group in Items.GroupBy(i => i.Source))
        {
            sb.AppendLine(SourceTitle(group.Key));
            foreach (var i in group)
            {
                sb.Append(i.IsNew ? "  [x] " : "  [-] ").Append(i.Label).Append(" → ").Append(i.TargetText);
                if (!i.IsNew) sb.Append("  (already imported)");
                sb.AppendLine();
                foreach (var n in i.Notes) sb.AppendLine("        " + n);
            }
        }
        foreach (var w in Warnings) sb.AppendLine("! " + w);
        return sb.ToString().TrimEnd();
    }

    public static string SourceTitle(ImportSource s) => s switch
    {
        ImportSource.ClaudeSkills => "Claude Code skills",
        ImportSource.ClaudeMd => "CLAUDE.md",
        ImportSource.CursorRules => "Cursor rules",
        ImportSource.KiroSteering => "Kiro steering",
        _ => "Copilot instructions"
    };
}

public enum ImportResultKind { Imported, AlreadyImported, Skipped, Failed }

public sealed record ImportOutcome(ImportItem Item, ImportResultKind Kind, string Message);

/// <summary>
/// Brings an existing setup from other tools over: Claude Code skills, CLAUDE.md, Cursor rules, Kiro steering and Copilot
/// instructions. Detection writes nothing; <see cref="ApplyAsync"/> writes skills and AGENTS.md sections. Source files are
/// never changed or deleted. Imported files carry an <c>imported-from</c> marker, so running the import again does nothing.
/// </summary>
public sealed class ProjectImporter
{
    public const string TeamConventions = "team-conventions";
    public const string MarkerKey = "imported-from";
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxCopiedFiles = 200;
    private const long MaxCopiedBytes = 5L * 1024 * 1024;

    private readonly string _project;
    private readonly string _projectSkills;
    private readonly string _globalSkills;
    private readonly string? _userClaudeSkills;

    /// <param name="projectDirectory">The repository to import into.</param>
    /// <param name="projectSkillsDirectory">Where project skills live (<c>.aiagent/skills</c>).</param>
    /// <param name="globalSkillsDirectory">Where global skills live (<c>~/.aiagent/skills</c>).</param>
    /// <param name="userClaudeSkillsDirectory"><c>~/.claude/skills</c>; null = do not look at the user's Claude setup.</param>
    public ProjectImporter(string projectDirectory, string projectSkillsDirectory, string globalSkillsDirectory, string? userClaudeSkillsDirectory = null)
    {
        _project = projectDirectory;
        _projectSkills = projectSkillsDirectory;
        _globalSkills = globalSkillsDirectory;
        _userClaudeSkills = userClaudeSkillsDirectory;
    }

    /// <summary>Parses a source name from the command line: all, claude, claude-md, cursor, kiro, copilot.</summary>
    public static bool TryParseSources(string? text, out IReadOnlyList<ImportSource>? sources)
    {
        sources = null;
        switch ((text ?? "all").Trim().ToLowerInvariant())
        {
            case "all": sources = Enum.GetValues<ImportSource>(); return true;
            case "claude": case "claude-skills": case "agent-skills": sources = new[] { ImportSource.ClaudeSkills }; return true;
            case "claude-md": case "claude.md": sources = new[] { ImportSource.ClaudeMd }; return true;
            case "cursor": sources = new[] { ImportSource.CursorRules }; return true;
            case "kiro": sources = new[] { ImportSource.KiroSteering }; return true;
            case "copilot": sources = new[] { ImportSource.CopilotInstructions }; return true;
            default: return false;
        }
    }

    public static string? DefaultUserClaudeSkills()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", "skills");
    }

    /// <summary>The AGENTS.md to append to: the existing memory file, or AGENTS.md in the project root.</summary>
    public string AgentsMdPath
    {
        get
        {
            foreach (var name in ProjectMemoryLoader.CandidateFileNames)
            {
                var path = Path.Combine(_project, name);
                if (File.Exists(path)) return path;
            }
            return Path.Combine(_project, ProjectMemoryLoader.MemoryFileName);
        }
    }

    // ------------------------------------------------------------------ detect

    /// <summary>Finds what can be imported and what is already imported. Writes nothing.</summary>
    public ImportPlan Plan(IEnumerable<ImportSource>? only = null)
    {
        var wanted = only?.ToHashSet() ?? Enum.GetValues<ImportSource>().ToHashSet();
        var items = new List<ImportItem>();
        var warnings = new List<string>();
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (wanted.Contains(ImportSource.ClaudeSkills))
        {
            DetectClaudeSkills(Path.Combine(_project, ".claude", "skills"), "claude-skills", ".claude/skills", ImportTarget.ProjectSkill, _projectSkills, items, warnings, taken);
            if (_userClaudeSkills != null)
                DetectClaudeSkills(_userClaudeSkills, "claude-user-skills", "~/.claude/skills", ImportTarget.GlobalSkill, _globalSkills, items, warnings, taken);
        }
        if (wanted.Contains(ImportSource.ClaudeMd))
            DetectSection(Path.Combine(_project, "CLAUDE.md"), "claude-md", "CLAUDE.md", ImportSource.ClaudeMd, items, warnings);
        if (wanted.Contains(ImportSource.CopilotInstructions))
            DetectSection(Path.Combine(_project, ".github", "copilot-instructions.md"), "copilot-instructions", ".github/copilot-instructions.md", ImportSource.CopilotInstructions, items, warnings);
        if (wanted.Contains(ImportSource.CursorRules))
            DetectCursor(items, warnings, taken);
        if (wanted.Contains(ImportSource.KiroSteering))
            DetectKiro(items, warnings, taken);

        return new ImportPlan { Items = items, Warnings = warnings };
    }

    private void DetectClaudeSkills(string root, string idPrefix, string display, ImportTarget target, string targetRoot, List<ImportItem> items, List<string> warnings, Dictionary<string, HashSet<string>> taken)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file) || IsLink(dir)) continue;
            var text = ReadText(file, warnings, $"{display}/{folder}/SKILL.md");
            if (text == null) continue;
            var id = $"{idPrefix}:{folder}";
            var notes = new List<string>();
            var doc = Frontmatter.Parse(text);
            var wantedName = doc.GetString("name") is { Length: > 0 } fmName && LocalFileStore.IsValidName(fmName) ? fmName : NormalizeName(folder);
            var description = doc.GetString("description");
            if (string.IsNullOrWhiteSpace(description))
            {
                description = Describe(doc.HasFrontmatter ? doc.Body : text, folder);
                notes.Add("no description in the front matter; one was taken from the text");
            }
            var existing = FindImported(targetRoot, id);
            var name = existing ?? UniqueName(wantedName, targetRoot, taken, notes);
            if (existing == null && !string.Equals(name, folder, StringComparison.Ordinal)) notes.Add($"imported as '{name}' (folder is '{folder}')");
            var content = text;
            content = Frontmatter.SetField(content, "name", name);
            if (string.IsNullOrWhiteSpace(doc.GetString("description"))) content = Frontmatter.SetField(content, "description", Frontmatter.QuoteIfNeeded(Shorten(description!, 300)));
            content = Frontmatter.SetField(content, MarkerKey, Frontmatter.QuoteIfNeeded(id));
            items.Add(new ImportItem
            {
                Id = id, Source = ImportSource.ClaudeSkills, Target = target, Label = $"{display}/{folder}", SourcePath = file,
                SourceDirectory = dir, TargetName = name, Description = description!, Content = content, Notes = notes,
                Status = existing != null ? ImportStatus.AlreadyImported : ImportStatus.New, Selected = existing == null
            });
        }
    }

    private void DetectSection(string file, string id, string label, ImportSource source, List<ImportItem> items, List<string> warnings)
    {
        if (!File.Exists(file)) return;
        var text = ReadText(file, warnings, label);
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text)) { warnings.Add($"{label} is empty; skipped."); return; }
        var title = $"Imported from {Path.GetFileName(file)}";
        var already = SectionExists(AgentsMdPath, id);
        items.Add(new ImportItem
        {
            Id = id, Source = source, Target = ImportTarget.AgentsMdSection, Label = label, SourcePath = file, TargetName = title,
            Description = Describe(text, title), Content = text.Trim(),
            Status = already ? ImportStatus.AlreadyImported : ImportStatus.New, Selected = !already
        });
    }

    private void DetectCursor(List<ImportItem> items, List<string> warnings, Dictionary<string, HashSet<string>> taken)
    {
        var rules = Path.Combine(_project, ".cursor", "rules");
        if (Directory.Exists(rules))
        {
            var files = Directory.GetFiles(rules, "*.mdc", SearchOption.AllDirectories).Concat(Directory.GetFiles(rules, "*.md", SearchOption.AllDirectories))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(rules, file).Replace('\\', '/');
                var label = ".cursor/rules/" + rel;
                var text = ReadText(file, warnings, label);
                if (text == null) continue;
                var doc = Frontmatter.Parse(text);
                if (string.IsNullOrWhiteSpace(doc.Body)) { warnings.Add($"{label} has no rule text; skipped."); continue; }
                var globs = string.Join(", ", doc.GetList("globs"));
                var always = doc.GetBool("alwaysApply");
                var description = doc.GetString("description");
                var stem = Path.GetFileNameWithoutExtension(file);
                var notes = new List<string>();
                var body = doc.Body.Trim();
                if (!string.IsNullOrEmpty(globs)) body = $"_Applies to files matching: {globs}_\n\n{body}";
                if (string.IsNullOrWhiteSpace(description))
                    description = !string.IsNullOrEmpty(globs) ? $"Cursor rule '{stem}' for files matching {globs}" : Describe(doc.Body, stem);
                AddRule(items, taken, ImportSource.CursorRules, $"cursor:{rel}", label, file, stem, description!, body, always,
                    manualOnly: !always && string.IsNullOrEmpty(globs) && string.IsNullOrWhiteSpace(doc.GetString("description")), notes);
            }
        }
        var legacy = Path.Combine(_project, ".cursorrules");
        if (File.Exists(legacy))
        {
            var text = ReadText(legacy, warnings, ".cursorrules");
            if (text != null)
            {
                if (string.IsNullOrWhiteSpace(text)) warnings.Add(".cursorrules is empty; skipped.");
                else AddRule(items, taken, ImportSource.CursorRules, "cursorrules", ".cursorrules", legacy, "cursorrules", Describe(text, "Cursor rules"), text.Trim(), always: true, manualOnly: false, new List<string>());
            }
        }
    }

    private void DetectKiro(List<ImportItem> items, List<string> warnings, Dictionary<string, HashSet<string>> taken)
    {
        var steering = Path.Combine(_project, ".kiro", "steering");
        if (!Directory.Exists(steering)) return;
        foreach (var file in Directory.GetFiles(steering, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(steering, file).Replace('\\', '/');
            var label = ".kiro/steering/" + rel;
            var text = ReadText(file, warnings, label);
            if (text == null) continue;
            var doc = Frontmatter.Parse(text);
            if (string.IsNullOrWhiteSpace(doc.Body)) { warnings.Add($"{label} has no text; skipped."); continue; }
            var inclusion = (doc.GetString("inclusion") ?? "always").Trim().ToLowerInvariant();
            var pattern = doc.GetString("fileMatchPattern");
            var stem = Path.GetFileNameWithoutExtension(file);
            var description = doc.GetString("description");
            var body = doc.Body.Trim();
            if (!string.IsNullOrWhiteSpace(pattern)) body = $"_Applies to files matching: {pattern}_\n\n{body}";
            if (string.IsNullOrWhiteSpace(description))
                description = !string.IsNullOrWhiteSpace(pattern) ? $"Kiro steering '{stem}' for files matching {pattern}" : Describe(doc.Body, stem);
            var name = doc.GetString("name");
            AddRule(items, taken, ImportSource.KiroSteering, $"kiro:{rel}", label, file, string.IsNullOrWhiteSpace(name) ? stem : name!, description!, body,
                always: inclusion == "always", manualOnly: inclusion == "manual", new List<string>());
        }
    }

    private void AddRule(List<ImportItem> items, Dictionary<string, HashSet<string>> taken, ImportSource source, string id, string label, string path,
        string stem, string description, string body, bool always, bool manualOnly, List<string> notes)
    {
        if (always)
        {
            var title = TitleOf(stem);
            var already = SectionExists(Path.Combine(_projectSkills, TeamConventions, "SKILL.md"), id);
            notes.Add("always applies: merged into the team-conventions skill, which characters can pin");
            items.Add(new ImportItem
            {
                Id = id, Source = source, Target = ImportTarget.TeamConventionsSection, Label = label, SourcePath = path, TargetName = title,
                Description = description, Content = body, Notes = notes,
                Status = already ? ImportStatus.AlreadyImported : ImportStatus.New, Selected = !already
            });
            return;
        }
        var existing = FindImported(_projectSkills, id);
        var name = existing ?? UniqueName(NormalizeName(stem), _projectSkills, taken, notes);
        if (existing == null && !string.Equals(name, stem, StringComparison.Ordinal)) notes.Add($"imported as '{name}' (source is '{stem}')");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("name", name),
            new("description", Frontmatter.QuoteIfNeeded(Shorten(description, 300)))
        };
        if (manualOnly) fields.Add(new("disable-model-invocation", "true"));
        fields.Add(new(MarkerKey, Frontmatter.QuoteIfNeeded(id)));
        items.Add(new ImportItem
        {
            Id = id, Source = source, Target = ImportTarget.ProjectSkill, Label = label, SourcePath = path, TargetName = name,
            Description = description, Content = Frontmatter.Compose(fields, body), Notes = notes,
            Status = existing != null ? ImportStatus.AlreadyImported : ImportStatus.New, Selected = existing == null
        });
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Writes the selected, new items. Never touches the source files.</summary>
    public async Task<IReadOnlyList<ImportOutcome>> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ImportOutcome>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsNew || !item.Selected)
            {
                outcomes.Add(new ImportOutcome(item, item.IsNew ? ImportResultKind.Skipped : ImportResultKind.AlreadyImported, item.IsNew ? "not selected" : "already imported"));
                continue;
            }
            try
            {
                outcomes.Add(await ApplyOneAsync(item, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new ImportOutcome(item, ImportResultKind.Failed, ex.Message));
            }
        }
        return outcomes;
    }

    private async Task<ImportOutcome> ApplyOneAsync(ImportItem item, CancellationToken ct)
    {
        switch (item.Target)
        {
            case ImportTarget.ProjectSkill:
            case ImportTarget.GlobalSkill:
            {
                var root = item.Target == ImportTarget.GlobalSkill ? _globalSkills : _projectSkills;
                if (FindImported(root, item.Id) != null) return new ImportOutcome(item, ImportResultKind.AlreadyImported, "already imported");
                var dir = Path.Combine(root, item.TargetName);
                if (File.Exists(Path.Combine(dir, "SKILL.md")))
                    return new ImportOutcome(item, ImportResultKind.Skipped, $"a skill named '{item.TargetName}' appeared meanwhile; run the import again");
                if (item.SourceDirectory != null) CopyResources(item.SourceDirectory, dir);
                await LocalFileStore.WriteAllTextAtomicAsync(Path.Combine(dir, "SKILL.md"), item.Content, ct).ConfigureAwait(false);
                return new ImportOutcome(item, ImportResultKind.Imported, $"skill '{item.TargetName}'");
            }
            case ImportTarget.AgentsMdSection:
            {
                var path = AgentsMdPath;
                if (SectionExists(path, item.Id)) return new ImportOutcome(item, ImportResultKind.AlreadyImported, "already imported");
                var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "# AGENTS.md\n";
                var section = $"## {item.TargetName}\n<!-- {MarkerKey}: {item.Id} -->\n\n{item.Content}\n";
                var updated = existing.TrimEnd() + "\n\n" + section;
                await LocalFileStore.WriteAllTextAtomicAsync(path, updated, ct).ConfigureAwait(false);
                return new ImportOutcome(item, ImportResultKind.Imported, $"AGENTS.md section '{item.TargetName}'");
            }
            default:
            {
                var file = Path.Combine(_projectSkills, TeamConventions, "SKILL.md");
                if (SectionExists(file, item.Id)) return new ImportOutcome(item, ImportResultKind.AlreadyImported, "already imported");
                var section = $"## {item.TargetName}\n<!-- {MarkerKey}: {item.Id} -->\n\n{item.Content}\n";
                string updated;
                if (File.Exists(file))
                    updated = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).TrimEnd() + "\n\n" + section;
                else
                    updated = Frontmatter.Compose(new KeyValuePair<string, string>[]
                    {
                        new("name", TeamConventions),
                        new("description", Frontmatter.QuoteIfNeeded("Team conventions imported from other tools: rules that always apply. Pin this skill on characters to keep it in context."))
                    }, "# Team conventions\n\nRules imported from Cursor rules and Kiro steering.\n\n" + section);
                await LocalFileStore.WriteAllTextAtomicAsync(file, updated, ct).ConfigureAwait(false);
                return new ImportOutcome(item, ImportResultKind.Imported, $"team-conventions section '{item.TargetName}'");
            }
        }
    }

    private static void CopyResources(string sourceDir, string targetDir)
    {
        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            if (string.Equals(rel, "SKILL.md", StringComparison.OrdinalIgnoreCase)) continue; // written with the edited front matter
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == ".git")) continue;
            var length = new FileInfo(file).Length;
            if (++count > MaxCopiedFiles || (bytes += length) > MaxCopiedBytes) break;
            var target = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Name of a skill in <paramref name="root"/> that was imported from <paramref name="id"/>, or null.</summary>
    private static string? FindImported(string root, string id)
    {
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            try
            {
                var doc = Frontmatter.Parse(File.ReadAllText(file));
                if (string.Equals(doc.GetString(MarkerKey), id, StringComparison.Ordinal)) return Path.GetFileName(dir);
            }
            catch (IOException) { /* unreadable: not ours */ }
        }
        return null;
    }

    private static bool SectionExists(string file, string id)
    {
        try { return File.Exists(file) && File.ReadAllText(file).Contains($"{MarkerKey}: {id} -->", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private static string UniqueName(string wanted, string root, Dictionary<string, HashSet<string>> taken, List<string> notes)
    {
        if (!taken.TryGetValue(root, out var used)) taken[root] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = wanted;
        var n = 2;
        while (used.Contains(name) || File.Exists(Path.Combine(root, name, "SKILL.md")))
        {
            var suffix = "-" + n++;
            name = (wanted.Length + suffix.Length > LocalFileStore.MaxNameLength ? wanted[..(LocalFileStore.MaxNameLength - suffix.Length)] : wanted).TrimEnd('-') + suffix;
        }
        if (name != wanted) notes.Add($"'{wanted}' is taken, so '{name}' is used");
        used.Add(name);
        return name;
    }

    internal static string NormalizeName(string text)
    {
        var name = LocalFileStore.ToKebab(text);
        return name.Length == 0 ? "imported-rule" : name;
    }

    private static string TitleOf(string stem)
    {
        var words = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? "Imported rule" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Describe(string text, string fallback)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', '>', ' ').Trim();
            if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
            return Shorten(line, 200);
        }
        return fallback;
    }

    private static string Shorten(string text, int max)
    {
        var single = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length <= max ? single : single[..(max - 1)].TrimEnd() + "…";
    }

    private static string? ReadText(string file, List<string> warnings, string label)
    {
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { warnings.Add($"{label} is a link; skipped."); return null; }
            if (new FileInfo(file).Length > MaxFileBytes) { warnings.Add($"{label} is larger than 1 MB; skipped."); return null; }
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{label} could not be read: {ex.Message}");
            return null;
        }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
