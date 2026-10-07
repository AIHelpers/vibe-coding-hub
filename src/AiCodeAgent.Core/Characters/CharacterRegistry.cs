using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Markdown-file implementation of <see cref="ICharacterRegistry"/>. Built-in
/// characters come from the role presets; a user file with the same id
/// overrides the built-in (that is how skills get assigned to "planner" etc.).
/// Project files override global files. Characters are resolved on every scan:
/// <c>extends</c> chains are followed (cycles are reported, not followed), so
/// every consumer sees the effective skillset (template + own − removed).
/// </summary>
public sealed class CharacterRegistry : ICharacterRegistry, IDisposable
{
    /// <summary>Value in a <c>skills</c> list meaning "every skill".</summary>
    public const string AllSkillsToken = "*";

    public const string OwnSource = "own";

    private readonly ILogger<CharacterRegistry>? _logger;
    private readonly string _globalDir;
    private readonly string? _projectDir;
    private readonly Func<IEnumerable<AgentRolePreset>> _presets;
    private readonly object _gate = new();
    private Dictionary<string, CharacterInfo> _effective = new(StringComparer.OrdinalIgnoreCase);
    private List<CharacterInfo> _userFiles = new();
    private bool _scanned;
    private DebouncedDirectoryWatcher? _watcher;

    public CharacterRegistry(
        ILogger<CharacterRegistry>? logger = null,
        string? globalCharactersDir = null,
        string? projectCharactersDir = null,
        Func<IEnumerable<AgentRolePreset>>? presets = null)
    {
        _logger = logger;
        _globalDir = globalCharactersDir ?? GetDefaultGlobalCharactersDir();
        _projectDir = projectCharactersDir;
        _presets = presets ?? (() => Array.Empty<AgentRolePreset>());
    }

    /// <summary>Convenience constructor: built-in characters come from <paramref name="presetLoader"/>.</summary>
    public CharacterRegistry(
        RolePresetLoader presetLoader,
        ILogger<CharacterRegistry>? logger = null,
        string? globalCharactersDir = null,
        string? projectCharactersDir = null)
        : this(logger, globalCharactersDir, projectCharactersDir, presetLoader.GetAllPresets)
    {
    }

    public event EventHandler? Changed;

    public string GlobalCharactersDirectory => _globalDir;
    public string? ProjectCharactersDirectory => _projectDir;

    /// <summary>Default global directory: <c>~/.aiagent/characters</c>.</summary>
    public static string GetDefaultGlobalCharactersDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = string.IsNullOrEmpty(home) ? ".aiagent" : Path.Combine(home, ".aiagent");
        return Path.Combine(dir, "characters");
    }

    /// <summary>Default project directory: <c>&lt;workdir&gt;/.aiagent/characters</c>.</summary>
    public static string GetDefaultProjectCharactersDir(string workingDirectory)
        => Path.Combine(workingDirectory, ".aiagent", "characters");

    /// <summary>Watch character directories and refresh + raise <see cref="Changed"/> on external edits.</summary>
    public void EnableWatching()
    {
        if (_watcher != null) return;
        var dirs = new List<string> { _globalDir };
        if (!string.IsNullOrEmpty(_projectDir)) dirs.Add(_projectDir);
        _watcher = new DebouncedDirectoryWatcher(dirs, () =>
        {
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose() => _watcher?.Dispose();

    // ============================== Read ==============================

    public Task<IReadOnlyList<CharacterInfo>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CharacterInfo>>(Snapshot().Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<CharacterInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot().TryGetValue(id, out var c) ? c : null);

    public async Task<string> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Snapshot().TryGetValue(id, out var c) || c.FilePath == null) return string.Empty;
        return await File.ReadAllTextAsync(c.FilePath, cancellationToken).ConfigureAwait(false);
    }

    // ============================== Characters: write ==============================

    public async Task<CharacterInfo> CreateAsync(CharacterDraft draft, SkillScope scope, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (LocalFileStore.ValidateName(draft.Id, "Character id") is { } idError) errors.Add(idError);
        var root = RootFor(scope);
        if (root == null) errors.Add(scope == SkillScope.BuiltIn ? "Built-in characters cannot be created." : "No project characters directory is configured.");
        if (!string.IsNullOrEmpty(draft.BaseRole) && !PresetExists(draft.BaseRole))
            errors.Add($"Unknown base role '{draft.BaseRole}'. Known roles: {string.Join(", ", _presets().Select(p => p.Role))}.");
        if (!string.IsNullOrEmpty(draft.Extends))
        {
            if (string.Equals(draft.Extends, draft.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add("A character cannot extend itself.");
            else if (!Snapshot().ContainsKey(draft.Extends))
                errors.Add($"Unknown template '{draft.Extends}'.");
        }
        if (errors.Count > 0) throw new SkillValidationException(errors);

        var path = Path.Combine(root!, draft.Id + ".md");
        if (File.Exists(path))
            throw new SkillValidationException($"A {scope.ToString().ToLowerInvariant()} character named '{draft.Id}' already exists.");

        await LocalFileStore.WriteAllTextAtomicAsync(path, BuildCharacterFile(draft), cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Created {Scope} character {Id}", scope, draft.Id);
        return AfterWrite(draft.Id);
    }

    /// <summary>Render a new character file from a draft.</summary>
    public static string BuildCharacterFile(CharacterDraft draft)
    {
        var fields = new List<KeyValuePair<string, string>> { new("id", draft.Id) };
        if (!string.IsNullOrWhiteSpace(draft.DisplayName)) fields.Add(new("display-name", Frontmatter.QuoteIfNeeded(draft.DisplayName.Trim())));
        if (!string.IsNullOrWhiteSpace(draft.Avatar)) fields.Add(new("avatar", Frontmatter.QuoteIfNeeded(draft.Avatar.Trim())));
        fields.Add(new("description", Frontmatter.QuoteIfNeeded(draft.Description.Trim())));
        if (draft.IsTemplate) fields.Add(new("template", "true"));
        if (!string.IsNullOrWhiteSpace(draft.Extends)) fields.Add(new("extends", draft.Extends.Trim()));
        if (!string.IsNullOrWhiteSpace(draft.BaseRole)) fields.Add(new("base-role", draft.BaseRole.Trim()));
        if (!string.IsNullOrWhiteSpace(draft.Model)) fields.Add(new("model", Frontmatter.QuoteIfNeeded(draft.Model.Trim())));
        if (draft.PermissionMode is { } mode) fields.Add(new("permission-mode", FormatPermissionMode(mode)));
        fields.Add(new("skills", Frontmatter.FormatList(draft.Skills.Distinct(StringComparer.OrdinalIgnoreCase))));
        if (draft.PinnedSkills.Count > 0) fields.Add(new("pinned-skills", Frontmatter.FormatList(draft.PinnedSkills.Distinct(StringComparer.OrdinalIgnoreCase))));
        var name = string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.Id : draft.DisplayName!.Trim();
        var persona = !string.IsNullOrWhiteSpace(draft.Persona)
            ? draft.Persona
            : draft.IsTemplate
                ? $"You are a {name}.\n\nDescribe what every {name} shares: profession, values and way of working. Characters that extend this template add their specialty after this text.\n"
            : !string.IsNullOrWhiteSpace(draft.Extends)
                ? $"You are {name}.\n\nDescribe what makes this character different from {draft.Extends.Trim()} (its specialty, priorities and way of working). The template's persona is included before this text.\n"
                : $"You are {name}.\n\nDescribe this character's personality, priorities and way of working here.\n";
        return Frontmatter.Compose(fields, persona);
    }

    public async Task<CharacterInfo> UpdateAsync(string id, string content, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var existing = FindUserFile(id, scope)
            ?? throw new SkillValidationException(Snapshot().TryGetValue(id, out var c) && c.IsBuiltIn
                ? $"'{id}' is a built-in character; assign skills or create a character file with that id to customize it."
                : $"Character '{id}' does not exist.");

        var doc = Frontmatter.Parse(content);
        var errors = new List<string>();
        if (!doc.HasFrontmatter) errors.Add("The character must start with a frontmatter block (--- id/description ---).");
        var fmId = doc.GetString("id");
        if (!string.IsNullOrEmpty(fmId) && !string.Equals(fmId, existing.Id, StringComparison.Ordinal))
            errors.Add($"Frontmatter id '{fmId}' does not match the file name '{existing.Id}'.");
        var mode = doc.GetString("permission-mode");
        if (!string.IsNullOrEmpty(mode) && ParsePermissionMode(mode) == null)
            errors.Add($"Unknown permission-mode '{mode}'. Use ask, auto-edit, full-auto or plan.");
        var extends = doc.GetString("extends");
        if (!string.IsNullOrEmpty(extends) && WouldCreateCycle(existing.Id, extends))
            errors.Add($"Extending '{extends}' would create an inheritance cycle.");
        if (errors.Count > 0) throw new SkillValidationException(errors);

        await LocalFileStore.WriteAllTextAtomicAsync(existing.FilePath!, content, cancellationToken).ConfigureAwait(false);
        return AfterWrite(existing.Id);
    }

    public Task<bool> DeleteAsync(string id, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var existing = FindUserFile(id, scope);
        if (existing?.FilePath == null || !File.Exists(existing.FilePath)) return Task.FromResult(false);
        File.Delete(existing.FilePath);
        _logger?.LogInformation("Deleted {Scope} character {Id}", existing.Scope, existing.Id);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public async Task<CharacterInfo> AssignSkillsAsync(string id, IEnumerable<string> skills, bool pinned = false, CancellationToken cancellationToken = default)
    {
        var names = CleanNames(skills);
        var target = await EnsureEditableAsync(id, cancellationToken).ConfigureAwait(false);
        return await EditCharacterAsync(target, (content, doc) =>
        {
            var listed = doc.GetList("skills");
            var pinnedList = doc.GetList("pinned-skills");
            var removed = doc.GetList("remove-skills");
            foreach (var name in names)
            {
                var wasRemoved = removed.RemoveAll(s => Same(s, name)) > 0;
                // Restoring an inherited skill just un-removes it, so it keeps following its template.
                if (wasRemoved && !pinned && target.InheritedSkillSources.ContainsKey(name) &&
                    !listed.Contains(name, StringComparer.OrdinalIgnoreCase) && !pinnedList.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                var into = pinned ? pinnedList : listed;
                var other = pinned ? listed : pinnedList;
                other.RemoveAll(s => Same(s, name));
                if (!into.Contains(name, StringComparer.OrdinalIgnoreCase)) into.Add(name);
            }
            content = WriteSkillLists(content, doc, listed, pinnedList);
            return WriteListField(content, doc, "remove-skills", removed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CharacterInfo> UnassignSkillsAsync(string id, IEnumerable<string> skills, CancellationToken cancellationToken = default)
    {
        var names = CleanNames(skills);
        var target = FindUserFile(id, null)
            ?? throw new SkillValidationException(Snapshot().ContainsKey(id)
                ? $"'{id}' is a built-in character with no skill list to change."
                : $"Character '{id}' does not exist.");
        return await EditCharacterAsync(target, (content, doc) =>
        {
            var listed = doc.GetList("skills");
            var pinnedList = doc.GetList("pinned-skills");
            var removed = doc.GetList("remove-skills");
            foreach (var name in names)
            {
                listed.RemoveAll(s => Same(s, name));
                pinnedList.RemoveAll(s => Same(s, name));
                // Skills that come from the template can only be dropped explicitly.
                if (target.InheritedSkillSources.ContainsKey(name) && !removed.Contains(name, StringComparer.OrdinalIgnoreCase))
                    removed.Add(name);
            }
            content = WriteSkillLists(content, doc, listed, pinnedList);
            return WriteListField(content, doc, "remove-skills", removed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CharacterInfo> SetExtendsAsync(string id, string? parentId, CancellationToken cancellationToken = default)
    {
        parentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId.Trim();
        if (parentId != null)
        {
            if (Same(parentId, id)) throw new SkillValidationException("A character cannot extend itself.");
            if (!Snapshot().ContainsKey(parentId)) throw new SkillValidationException($"Unknown template '{parentId}'.");
            if (WouldCreateCycle(id, parentId)) throw new SkillValidationException($"Extending '{parentId}' would create an inheritance cycle.");
        }
        var target = await EnsureEditableAsync(id, cancellationToken).ConfigureAwait(false);
        return await EditCharacterAsync(target, (content, _) =>
            parentId == null ? Frontmatter.RemoveField(content, "extends") : Frontmatter.SetField(content, "extends", parentId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CharacterInfo> SetTemplateAsync(string id, bool isTemplate, CancellationToken cancellationToken = default)
    {
        var target = await EnsureEditableAsync(id, cancellationToken).ConfigureAwait(false);
        return await EditCharacterAsync(target, (content, _) =>
            isTemplate ? Frontmatter.SetField(content, "template", "true") : Frontmatter.RemoveField(content, "template"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CharacterInfo> EditCharacterAsync(CharacterInfo target, Func<string, FrontmatterDocument, string> edit, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(target.FilePath!, cancellationToken).ConfigureAwait(false);
        var updated = edit(content, Frontmatter.Parse(content));
        await LocalFileStore.WriteAllTextAtomicAsync(target.FilePath!, updated, cancellationToken).ConfigureAwait(false);
        return AfterWrite(target.Id);
    }

    private static string WriteSkillLists(string content, FrontmatterDocument doc, List<string> listed, List<string> pinned)
    {
        content = Frontmatter.SetField(content, "skills", Frontmatter.FormatList(listed));
        return WriteListField(content, doc, "pinned-skills", pinned);
    }

    /// <summary>Write a list field; an empty list leaves a missing field missing (and drops an empty <c>remove-skills</c>).</summary>
    private static string WriteListField(string content, FrontmatterDocument doc, string key, List<string> values)
    {
        if (values.Count == 0 && !doc.Fields.ContainsKey(key)) return content;
        if (values.Count == 0 && key == "remove-skills") return Frontmatter.RemoveField(content, key);
        return Frontmatter.SetField(content, key, Frontmatter.FormatList(values));
    }

    /// <summary>
    /// Returns the effective user file for <paramref name="id"/>; for a built-in
    /// character, first writes a global override file that keeps its behavior.
    /// </summary>
    private async Task<CharacterInfo> EnsureEditableAsync(string id, CancellationToken cancellationToken)
    {
        var user = FindUserFile(id, null);
        if (user != null) return user;
        if (!Snapshot().TryGetValue(id, out var builtIn) || !builtIn.IsBuiltIn)
            throw new SkillValidationException($"Character '{id}' does not exist.");

        var path = Path.Combine(_globalDir, builtIn.Id + ".md");
        var draft = new CharacterDraft
        {
            Id = builtIn.Id,
            DisplayName = builtIn.DisplayName,
            Description = builtIn.Description,
            BaseRole = builtIn.BaseRole,
            Persona = $"Customized built-in {builtIn.Id} character. Its role instructions come from the '{builtIn.BaseRole}' preset; add persona details here.\n"
        };
        await LocalFileStore.WriteAllTextAtomicAsync(path, BuildCharacterFile(draft), cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Created global override for built-in character {Id}", id);
        Refresh();
        return FindUserFile(id, null) ?? throw new InvalidOperationException($"Override for '{id}' could not be read back.");
    }

    // ============================== Skill references ==============================

    public Task<IReadOnlyList<SkillReference>> FindSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default)
    {
        Snapshot();
        List<CharacterInfo> chars;
        lock (_gate) chars = _userFiles.ToList();
        var refs = new List<SkillReference>();
        foreach (var c in chars)
        {
            if (c.OwnSkills?.Contains(skillName, StringComparer.OrdinalIgnoreCase) == true)
                refs.Add(new SkillReference(c.Id, c.Scope, c.FilePath!, Pinned: false));
            if (c.OwnPinnedSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
                refs.Add(new SkillReference(c.Id, c.Scope, c.FilePath!, Pinned: true));
        }
        return Task.FromResult<IReadOnlyList<SkillReference>>(refs);
    }

    public Task<int> RenameSkillReferencesAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        => RewriteReferencesAsync(oldName, list =>
        {
            for (var i = 0; i < list.Count; i++)
                if (Same(list[i], oldName)) list[i] = newName;
            var deduped = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Clear();
            list.AddRange(deduped);
        }, cancellationToken);

    public Task<int> RemoveSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default)
        => RewriteReferencesAsync(skillName, list => list.RemoveAll(s => Same(s, skillName)), cancellationToken);

    private async Task<int> RewriteReferencesAsync(string skillName, Action<List<string>> edit, CancellationToken cancellationToken)
    {
        Snapshot();
        List<string> paths;
        lock (_gate)
        {
            paths = _userFiles
                .Where(c => c.DeclaredSkillReferences.Concat(c.RemoveSkills).Contains(skillName, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.FilePath!)
                .Distinct()
                .ToList();
        }

        foreach (var path in paths)
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var doc = Frontmatter.Parse(content);
            var listed = doc.GetList("skills");
            var pinned = doc.GetList("pinned-skills");
            var removed = doc.GetList("remove-skills");
            edit(listed);
            edit(pinned);
            edit(removed);
            content = WriteSkillLists(content, doc, listed, pinned);
            content = WriteListField(content, doc, "remove-skills", removed);
            await LocalFileStore.WriteAllTextAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        if (paths.Count > 0)
        {
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return paths.Count;
    }

    // ============================== Helpers ==============================

    public void Refresh()
    {
        lock (_gate) _scanned = false;
    }

    private CharacterInfo AfterWrite(string id)
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return Snapshot().TryGetValue(id, out var c)
            ? c
            : throw new InvalidOperationException($"Character '{id}' was written but could not be read back.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<string> CleanNames(IEnumerable<string> names) =>
        names.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private string? RootFor(SkillScope scope) => scope switch
    {
        SkillScope.Global => _globalDir,
        SkillScope.Project => _projectDir,
        _ => null
    };

    private bool PresetExists(string role) =>
        _presets().Any(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when making <paramref name="id"/> extend <paramref name="parentId"/> would loop back to <paramref name="id"/>.</summary>
    private bool WouldCreateCycle(string id, string parentId)
    {
        var map = Snapshot();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = parentId;
        while (current != null && seen.Add(current))
        {
            if (Same(current, id)) return true;
            current = map.TryGetValue(current, out var c) ? c.Extends : null;
        }
        return false;
    }

    /// <summary>The user file for <paramref name="id"/> in <paramref name="scope"/> (or the effective user file when null).</summary>
    private CharacterInfo? FindUserFile(string id, SkillScope? scope)
    {
        var effective = Snapshot();
        lock (_gate)
        {
            if (scope is { } s)
                return _userFiles.FirstOrDefault(c => c.Scope == s && Same(c.Id, id));
            return effective.TryGetValue(id, out var c) && !c.IsBuiltIn ? c : null;
        }
    }

    // ============================== Scan + resolve ==============================

    private Dictionary<string, CharacterInfo> Snapshot()
    {
        lock (_gate)
        {
            if (_scanned) return _effective;
            var presets = _presets().ToList();

            // 1. Characters: built-ins, then global files, then project files (later wins).
            var declared = new Dictionary<string, CharacterInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in presets.Where(p => !string.IsNullOrEmpty(p.Role)))
                declared[preset.Role] = FromPreset(preset);

            var files = new List<CharacterInfo>();
            try
            {
                files.AddRange(ScanDirectory(_globalDir, SkillScope.Global, presets));
                if (!string.IsNullOrEmpty(_projectDir))
                    files.AddRange(ScanDirectory(_projectDir, SkillScope.Project, presets));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to scan character directories");
            }
            foreach (var c in files)
                declared[c.Id] = declared.ContainsKey(c.Id) ? c with { Overrides = true } : c;

            // 2. Resolve templates into effective characters.
            var effective = ResolveCharacters(declared);

            _userFiles = files;
            _effective = effective;
            _scanned = true;
            return _effective;
        }
    }

    /// <summary>Follow <c>extends</c> into the effective character settings.</summary>
    internal static Dictionary<string, CharacterInfo> ResolveCharacters(Dictionary<string, CharacterInfo> declared)
    {
        var result = new Dictionary<string, CharacterInfo>(StringComparer.OrdinalIgnoreCase);
        var cycles = FindCycles(declared.Keys, id =>
            declared.TryGetValue(id, out var c) && c.Extends != null ? new[] { c.Extends } : Array.Empty<string>());

        CharacterInfo Resolve(string id)
        {
            if (result.TryGetValue(id, out var done)) return done;
            var c = declared[id];
            if (c.IsBuiltIn)
            {
                result[id] = c;
                return c;
            }

            var errors = c.ValidationErrors.ToList();
            var warnings = c.Warnings.ToList();
            CharacterInfo? parent = null;
            if (c.Extends != null)
            {
                if (cycles.TryGetValue(id, out var cyclePath))
                    errors.Add($"Inheritance cycle: {cyclePath}.");
                else if (!declared.ContainsKey(c.Extends))
                    warnings.Add($"Extends unknown template '{c.Extends}'; nothing is inherited.");
                else
                {
                    var p = Resolve(c.Extends);
                    if (p.IsValid) parent = p;
                    else warnings.Add($"Template '{c.Extends}' is invalid; nothing is inherited.");
                }
            }

            // Skills contributed by the template (before remove-skills).
            var inherited = new OrderedSources();
            var inheritedPinned = new OrderedSources();
            if (parent != null)
            {
                foreach (var s in parent.Skills ?? Array.Empty<string>())
                    inherited.Set(s, InheritedSource(parent, s));
                foreach (var s in parent.PinnedSkills)
                    inheritedPinned.Set(s, InheritedSource(parent, s));
            }
            var removed = new HashSet<string>(c.RemoveSkills, StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.RemoveSkills.Where(r => !inherited.Contains(r) && !inheritedPinned.Contains(r) &&
                                                       c.OwnSkills?.Contains(r, StringComparer.OrdinalIgnoreCase) != true &&
                                                       !c.OwnPinnedSkills.Contains(r, StringComparer.OrdinalIgnoreCase)))
                warnings.Add($"remove-skills lists '{r}', which is not inherited.");

            // Effective lists: inherited − removed, then own (own always wins, even over remove-skills).
            List<string>? skills = null;
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pinned = new OrderedSources();
            foreach (var (s, src) in inheritedPinned.Items.Where(i => !removed.Contains(i.Key))) pinned.Set(s, src);
            foreach (var s in c.OwnPinnedSkills) pinned.Set(s, OwnSource);

            if (c.OwnSkills != null)
            {
                var listed = new OrderedSources();
                foreach (var (s, src) in inherited.Items.Where(i => !removed.Contains(i.Key))) listed.Set(s, src);
                foreach (var s in c.OwnSkills) listed.Set(s, OwnSource);
                foreach (var p in pinned.Keys) listed.Remove(p);
                skills = listed.Keys.ToList();
                foreach (var (k, v) in listed.Items) sources[k] = v;
            }
            foreach (var (k, v) in pinned.Items) sources[k] = v;

            var inheritedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in inherited.Items.Concat(inheritedPinned.Items)) inheritedSources[k] = v;

            var resolved = c with
            {
                BaseRole = c.BaseRole ?? parent?.BaseRole,
                Model = c.Model ?? parent?.Model,
                PermissionMode = c.PermissionMode ?? parent?.PermissionMode,
                Tools = c.Tools ?? parent?.Tools,
                ToolsAdd = (parent?.ToolsAdd ?? Array.Empty<string>()).Concat(c.ToolsAdd).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ToolsRemove = (parent?.ToolsRemove ?? Array.Empty<string>()).Concat(c.ToolsRemove).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Persona = string.Join("\n\n", new[] { parent?.Persona, c.OwnPersona }.Where(p => !string.IsNullOrWhiteSpace(p))),
                Description = string.IsNullOrWhiteSpace(c.Description) && parent != null ? parent.Description : c.Description,
                Avatar = c.Avatar ?? parent?.Avatar,
                InheritanceChain = parent == null ? Array.Empty<string>() : new[] { parent.Id }.Concat(parent.InheritanceChain).ToList(),
                Skills = skills,
                PinnedSkills = pinned.Keys.ToList(),
                SkillSources = sources,
                InheritedSkillSources = inheritedSources,
                ValidationErrors = errors,
                Warnings = warnings
            };
            result[id] = resolved;
            return resolved;
        }

        foreach (var id in declared.Keys.ToList())
            Resolve(id);
        return result;
    }

    /// <summary>Source label for a skill inherited from <paramref name="parent"/>: the template that declares it.</summary>
    private static string InheritedSource(CharacterInfo parent, string skill)
    {
        var src = parent.SkillSources.TryGetValue(skill, out var s) ? s : OwnSource;
        return src.StartsWith("template:", StringComparison.Ordinal) ? src : $"template:{parent.Id}";
    }

    /// <summary>Ids that are part of a cycle, mapped to a readable path like "a → b → a".</summary>
    private static Dictionary<string, string> FindCycles(IEnumerable<string> ids, Func<string, IReadOnlyList<string>> edges)
    {
        var cycles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in ids)
        {
            // DFS from start; a path that returns to start is a cycle containing start.
            var stack = new Stack<(string Node, List<string> Path)>();
            stack.Push((start, new List<string> { start }));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                var (node, path) = stack.Pop();
                foreach (var next in edges(node))
                {
                    if (Same(next, start))
                    {
                        cycles[start] = string.Join(" → ", path.Append(start));
                        stack.Clear();
                        break;
                    }
                    if (seen.Add(next)) stack.Push((next, path.Append(next).ToList()));
                }
            }
        }
        return cycles;
    }

    /// <summary>Insertion-ordered skill → source map (case-insensitive keys).</summary>
    private sealed class OrderedSources
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string key, string source)
        {
            if (key == AllSkillsToken) return;
            if (!_map.ContainsKey(key)) _order.Add(key);
            _map[key] = source;
        }
        public void Remove(string key)
        {
            if (_map.Remove(key)) _order.RemoveAll(k => Same(k, key));
        }
        public bool Contains(string key) => _map.ContainsKey(key);
        public IEnumerable<string> Keys => _order;
        public IEnumerable<KeyValuePair<string, string>> Items => _order.Select(k => new KeyValuePair<string, string>(k, _map[k]));
    }

    private IEnumerable<CharacterInfo> ScanDirectory(string dir, SkillScope scope, List<AgentRolePreset> presets)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id.StartsWith('.')) continue;
            CharacterInfo? info = null;
            try
            {
                info = ParseCharacterFile(id, file, scope, presets);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse character {Path}", file);
            }
            if (info != null) yield return info;
        }
    }

    /// <summary>Parse a character file as declared (own values only). Content problems become errors/warnings, never exceptions.</summary>
    internal static CharacterInfo ParseCharacterFile(string id, string filePath, SkillScope scope, IReadOnlyCollection<AgentRolePreset> presets)
    {
        var doc = Frontmatter.Parse(File.ReadAllText(filePath));
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!doc.HasFrontmatter) errors.Add("Missing frontmatter block (--- id/description ---) at the top of the file.");
        var fmId = doc.GetString("id");
        if (!string.IsNullOrEmpty(fmId) && !string.Equals(fmId, id, StringComparison.Ordinal))
            warnings.Add($"Frontmatter id '{fmId}' differs from file name '{id}'; the file name is used.");
        if (!LocalFileStore.IsValidName(id))
            warnings.Add($"File name '{id}' is not kebab-case; rename it to avoid problems.");

        var baseRole = doc.GetString("base-role");
        if (!string.IsNullOrEmpty(baseRole) && !presets.Any(p => string.Equals(p.Role, baseRole, StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"Unknown base role '{baseRole}'; the character runs as a generic agent.");

        PermissionMode? mode = null;
        var modeText = doc.GetString("permission-mode");
        if (!string.IsNullOrEmpty(modeText))
        {
            mode = ParsePermissionMode(modeText);
            if (mode == null) errors.Add($"Unknown permission-mode '{modeText}'. Use ask, auto-edit, full-auto or plan.");
        }

        IReadOnlyList<string>? explicitTools = null;
        IReadOnlyList<string> add = Array.Empty<string>();
        IReadOnlyList<string> remove = Array.Empty<string>();
        if (doc.Fields.TryGetValue("tools", out var toolsValue))
        {
            switch (toolsValue)
            {
                case List<string> list:
                    explicitTools = list;
                    break;
                case Dictionary<string, object?> map:
                    var m = new FrontmatterDocument { Fields = map };
                    add = m.GetList("add");
                    remove = m.GetList("remove");
                    break;
                case string s when !string.IsNullOrWhiteSpace(s):
                    explicitTools = doc.GetList("tools");
                    break;
            }
        }

        IReadOnlyList<string>? skills = doc.GetList("skills");
        if (skills.Contains(AllSkillsToken)) skills = null;
        var pinned = doc.GetList("pinned-skills");
        var extends = doc.GetString("extends");
        if (string.Equals(extends, id, StringComparison.OrdinalIgnoreCase))
            errors.Add("A character cannot extend itself.");

        if (doc.GetList("skillsets").Count > 0)
            warnings.Add("'skillsets' is no longer supported and is ignored; add those skills to this character's own skills instead.");

        var description = doc.GetString("description") ?? string.Empty;
        if (doc.HasFrontmatter && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(extends))
            warnings.Add("No description: other agents and the UI cannot tell what this character is for.");

        var persona = doc.HasFrontmatter ? doc.Body.Trim() : string.Empty;
        return new CharacterInfo
        {
            Id = id,
            DisplayName = doc.GetString("display-name") ?? doc.GetString("name") ?? id,
            Avatar = doc.GetString("avatar"),
            Description = description,
            IsTemplate = doc.GetBool("template"),
            Extends = string.IsNullOrWhiteSpace(extends) ? null : extends.Trim(),
            RemoveSkills = doc.GetList("remove-skills"),
            BaseRole = string.IsNullOrWhiteSpace(baseRole) ? null : baseRole,
            Model = doc.GetString("model"),
            PermissionMode = mode,
            Tools = explicitTools,
            ToolsAdd = add,
            ToolsRemove = remove,
            Skills = skills,
            PinnedSkills = pinned,
            OwnSkills = skills,
            OwnPinnedSkills = pinned,
            Persona = persona,
            OwnPersona = persona,
            Scope = scope,
            FilePath = filePath,
            ValidationErrors = errors,
            Warnings = warnings
        };
    }

    /// <summary>Built-in character view of a role preset.</summary>
    internal static CharacterInfo FromPreset(AgentRolePreset preset) => new()
    {
        Id = preset.Role,
        DisplayName = char.ToUpperInvariant(preset.Role[0]) + preset.Role[1..],
        Description = preset.Description ?? string.Empty,
        BaseRole = preset.Role,
        Skills = null, // built-ins keep access to every skill, as before characters existed
        OwnSkills = null,
        Scope = SkillScope.BuiltIn
    };

    /// <summary>Parse <c>ask | auto-edit | full-auto | plan</c> (case/separator-insensitive).</summary>
    public static PermissionMode? ParsePermissionMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace("-", "").Replace("_", "").Replace(" ", "");
        return Enum.TryParse<PermissionMode>(normalized, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : null;
    }

    public static string FormatPermissionMode(PermissionMode mode) => mode switch
    {
        PermissionMode.AutoEdit => "auto-edit",
        PermissionMode.FullAuto => "full-auto",
        PermissionMode.Plan => "plan",
        _ => "ask"
    };
}
