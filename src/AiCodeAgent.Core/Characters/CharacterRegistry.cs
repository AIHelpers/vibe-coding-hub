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
/// Project files override global files.
/// </summary>
public sealed class CharacterRegistry : ICharacterRegistry, IDisposable
{
    /// <summary>Value in a <c>skills</c> list meaning "every skill".</summary>
    public const string AllSkillsToken = "*";

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

    /// <summary>Watch both directories and refresh + raise <see cref="Changed"/> on external edits.</summary>
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

    // ===================== Read =====================

    public Task<IReadOnlyList<CharacterInfo>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CharacterInfo>>(Snapshot().Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<CharacterInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot().TryGetValue(id, out var c) ? c : null);

    public async Task<string> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Snapshot().TryGetValue(id, out var c) || c.FilePath == null) return string.Empty;
        return await File.ReadAllTextAsync(c.FilePath, cancellationToken).ConfigureAwait(false);
    }

    // ===================== Write =====================

    public async Task<CharacterInfo> CreateAsync(CharacterDraft draft, SkillScope scope, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (LocalFileStore.ValidateName(draft.Id, "Character id") is { } idError) errors.Add(idError);
        var root = RootFor(scope);
        if (root == null) errors.Add(scope == SkillScope.BuiltIn ? "Built-in characters cannot be created." : "No project characters directory is configured.");
        if (!string.IsNullOrEmpty(draft.BaseRole) && !PresetExists(draft.BaseRole))
            errors.Add($"Unknown base role '{draft.BaseRole}'. Known roles: {string.Join(", ", _presets().Select(p => p.Role))}.");
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
        if (!string.IsNullOrWhiteSpace(draft.BaseRole)) fields.Add(new("base-role", draft.BaseRole.Trim()));
        if (!string.IsNullOrWhiteSpace(draft.Model)) fields.Add(new("model", Frontmatter.QuoteIfNeeded(draft.Model.Trim())));
        if (draft.PermissionMode is { } mode) fields.Add(new("permission-mode", FormatPermissionMode(mode)));
        fields.Add(new("skills", Frontmatter.FormatList(draft.Skills.Distinct(StringComparer.OrdinalIgnoreCase))));
        if (draft.PinnedSkills.Count > 0) fields.Add(new("pinned-skills", Frontmatter.FormatList(draft.PinnedSkills.Distinct(StringComparer.OrdinalIgnoreCase))));
        var persona = string.IsNullOrWhiteSpace(draft.Persona)
            ? $"You are {(string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.Id : draft.DisplayName!.Trim())}.\n\nDescribe this character's personality, priorities and way of working here.\n"
            : draft.Persona;
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
        var names = skills.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var target = await EnsureEditableAsync(id, cancellationToken).ConfigureAwait(false);
        var content = await File.ReadAllTextAsync(target.FilePath!, cancellationToken).ConfigureAwait(false);
        var doc = Frontmatter.Parse(content);
        var listed = doc.GetList("skills");
        var pinnedList = doc.GetList("pinned-skills");

        foreach (var name in names)
        {
            var into = pinned ? pinnedList : listed;
            var other = pinned ? listed : pinnedList;
            other.RemoveAll(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (!into.Contains(name, StringComparer.OrdinalIgnoreCase)) into.Add(name);
        }

        content = WriteSkillLists(content, listed, pinnedList, hadPinned: doc.Fields.ContainsKey("pinned-skills"));
        await LocalFileStore.WriteAllTextAtomicAsync(target.FilePath!, content, cancellationToken).ConfigureAwait(false);
        return AfterWrite(target.Id);
    }

    public async Task<CharacterInfo> UnassignSkillsAsync(string id, IEnumerable<string> skills, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(skills.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        var target = FindUserFile(id, null)
            ?? throw new SkillValidationException(Snapshot().ContainsKey(id)
                ? $"'{id}' is a built-in character with no skill list to change."
                : $"Character '{id}' does not exist.");
        var content = await File.ReadAllTextAsync(target.FilePath!, cancellationToken).ConfigureAwait(false);
        var doc = Frontmatter.Parse(content);
        var listed = doc.GetList("skills");
        var pinnedList = doc.GetList("pinned-skills");
        listed.RemoveAll(names.Contains);
        pinnedList.RemoveAll(names.Contains);
        content = WriteSkillLists(content, listed, pinnedList, hadPinned: doc.Fields.ContainsKey("pinned-skills"));
        await LocalFileStore.WriteAllTextAtomicAsync(target.FilePath!, content, cancellationToken).ConfigureAwait(false);
        return AfterWrite(target.Id);
    }

    private static string WriteSkillLists(string content, List<string> listed, List<string> pinned, bool hadPinned)
    {
        content = Frontmatter.SetField(content, "skills", Frontmatter.FormatList(listed));
        if (pinned.Count > 0 || hadPinned)
            content = Frontmatter.SetField(content, "pinned-skills", Frontmatter.FormatList(pinned));
        return content;
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

    public Task<IReadOnlyList<SkillReference>> FindSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default)
    {
        Snapshot();
        List<CharacterInfo> files;
        lock (_gate) files = _userFiles.ToList();
        var refs = new List<SkillReference>();
        foreach (var c in files)
        {
            if (c.Skills?.Contains(skillName, StringComparer.OrdinalIgnoreCase) == true)
                refs.Add(new SkillReference(c.Id, c.Scope, c.FilePath!, Pinned: false));
            if (c.PinnedSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
                refs.Add(new SkillReference(c.Id, c.Scope, c.FilePath!, Pinned: true));
        }
        return Task.FromResult<IReadOnlyList<SkillReference>>(refs);
    }

    public Task<int> RenameSkillReferencesAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        => RewriteReferencesAsync(oldName, list =>
        {
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i], oldName, StringComparison.OrdinalIgnoreCase)) list[i] = newName;
            var deduped = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Clear();
            list.AddRange(deduped);
        }, cancellationToken);

    public Task<int> RemoveSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default)
        => RewriteReferencesAsync(skillName, list => list.RemoveAll(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase)), cancellationToken);

    private async Task<int> RewriteReferencesAsync(string skillName, Action<List<string>> edit, CancellationToken cancellationToken)
    {
        var refs = await FindSkillReferencesAsync(skillName, cancellationToken).ConfigureAwait(false);
        var changed = 0;
        foreach (var path in refs.Select(r => r.FilePath).Distinct())
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var doc = Frontmatter.Parse(content);
            var listed = doc.GetList("skills");
            var pinned = doc.GetList("pinned-skills");
            edit(listed);
            edit(pinned);
            content = WriteSkillLists(content, listed, pinned, hadPinned: doc.Fields.ContainsKey("pinned-skills"));
            await LocalFileStore.WriteAllTextAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
            changed++;
        }
        if (changed > 0)
        {
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

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

    private string? RootFor(SkillScope scope) => scope switch
    {
        SkillScope.Global => _globalDir,
        SkillScope.Project => _projectDir,
        _ => null
    };

    private bool PresetExists(string role) =>
        _presets().Any(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>The user file for <paramref name="id"/> in <paramref name="scope"/> (or the effective user file when null).</summary>
    private CharacterInfo? FindUserFile(string id, SkillScope? scope)
    {
        var effective = Snapshot();
        lock (_gate)
        {
            if (scope is { } s)
                return _userFiles.FirstOrDefault(c => c.Scope == s && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            return effective.TryGetValue(id, out var c) && !c.IsBuiltIn ? c : null;
        }
    }

    // ===================== Scan =====================

    private Dictionary<string, CharacterInfo> Snapshot()
    {
        lock (_gate)
        {
            if (_scanned) return _effective;
            var presets = _presets().ToList();
            var map = new Dictionary<string, CharacterInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in presets.Where(p => !string.IsNullOrEmpty(p.Role)))
                map[preset.Role] = FromPreset(preset);

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

            foreach (var c in files) // global first, then project: later wins
                map[c.Id] = map.ContainsKey(c.Id) ? c with { Overrides = true } : c;

            _userFiles = files;
            _effective = map;
            _scanned = true;
            return _effective;
        }
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

    /// <summary>Parse a character file. Content problems become validation errors/warnings, never exceptions.</summary>
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

        var description = doc.GetString("description") ?? string.Empty;
        if (doc.HasFrontmatter && string.IsNullOrWhiteSpace(description))
            warnings.Add("No description: other agents and the UI cannot tell what this character is for.");

        return new CharacterInfo
        {
            Id = id,
            DisplayName = doc.GetString("display-name") ?? doc.GetString("name") ?? id,
            Avatar = doc.GetString("avatar"),
            Description = description,
            BaseRole = string.IsNullOrWhiteSpace(baseRole) ? null : baseRole,
            Model = doc.GetString("model"),
            PermissionMode = mode,
            Tools = explicitTools,
            ToolsAdd = add,
            ToolsRemove = remove,
            Skills = skills,
            PinnedSkills = pinned,
            Persona = doc.HasFrontmatter ? doc.Body.Trim() : string.Empty,
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
