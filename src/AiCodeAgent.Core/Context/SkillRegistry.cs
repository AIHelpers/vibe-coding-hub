using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>
/// File-based implementation of <see cref="ISkillRegistry"/>. Scans
/// <c>~/.aiagent/skills/&lt;name&gt;/SKILL.md</c> (global) and
/// <c>.aiagent/skills/&lt;name&gt;/SKILL.md</c> (project), parses the frontmatter,
/// and applies <see cref="AgentConfiguration.SkillOverrides"/>. Project skills
/// override global skills with the same name. Skills are always identified by
/// their folder name; a frontmatter <c>name</c> that disagrees is reported as a
/// warning instead of silently breaking lookups.
/// </summary>
public sealed class SkillRegistry : ISkillRegistry, IDisposable
{
    /// <summary>Skills larger than this (estimated tokens) get a size warning.</summary>
    public const int LargeSkillTokenThreshold = 8_000;

    public const string SkillFileName = "SKILL.md";

    private readonly ILogger<SkillRegistry>? _logger;
    private readonly string _globalSkillsDir;
    private readonly string? _projectSkillsDir;
    private readonly Dictionary<string, string> _overrides;
    private readonly object _gate = new();
    private Dictionary<string, SkillInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SkillInfo> _globalCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SkillInfo> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanned;
    private DebouncedDirectoryWatcher? _watcher;

    public SkillRegistry(
        ILogger<SkillRegistry>? logger = null,
        string? globalSkillsDir = null,
        string? projectSkillsDir = null,
        Dictionary<string, string>? overrides = null)
    {
        _logger = logger;
        _globalSkillsDir = globalSkillsDir ?? GetDefaultGlobalSkillsDir();
        _projectSkillsDir = projectSkillsDir;
        _overrides = overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public string GlobalSkillsDirectory => _globalSkillsDir;

    /// <inheritdoc />
    public string? ProjectSkillsDirectory => _projectSkillsDir;

    /// <summary>Default global skills directory: <c>~/.aiagent/skills</c>.</summary>
    public static string GetDefaultGlobalSkillsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = string.IsNullOrEmpty(home) ? ".aiagent" : Path.Combine(home, ".aiagent");
        return Path.Combine(dir, "skills");
    }

    /// <summary>Default project skills directory: <c>.aiagent/skills</c>.</summary>
    public static string GetDefaultProjectSkillsDir(string workingDirectory)
        => Path.Combine(workingDirectory, ".aiagent", "skills");

    /// <summary>
    /// Start watching both skill directories; external edits (an editor, git
    /// checkout) refresh the cache and raise <see cref="Changed"/>.
    /// </summary>
    public void EnableWatching()
    {
        if (_watcher != null) return;
        var dirs = new List<string> { _globalSkillsDir };
        if (!string.IsNullOrEmpty(_projectSkillsDir)) dirs.Add(_projectSkillsDir);
        _watcher = new DebouncedDirectoryWatcher(dirs, () =>
        {
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose() => _watcher?.Dispose();

    // ===================== Read =====================

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var visible = Snapshot().Values
            .Where(IsVisible)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<SkillInfo>>(visible);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillInfo>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var all = Snapshot().Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<SkillInfo>>(all);
    }

    /// <inheritdoc />
    public Task<SkillInfo?> GetAsync(string skillName, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot().TryGetValue(skillName, out var info) ? info : null);

    /// <inheritdoc />
    public async Task<string> LoadAsync(string skillName, CancellationToken cancellationToken = default)
    {
        if (!Snapshot().TryGetValue(skillName, out var info) || info.FilePath == null)
            return string.Empty;
        try
        {
            return await File.ReadAllTextAsync(info.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load skill {Name} from {Path}", skillName, info.FilePath);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<SkillInvocation?> InvokeAsync(string skillName, string? args = null, CancellationToken cancellationToken = default)
    {
        if (!Snapshot().TryGetValue(skillName, out var info) || info.FilePath == null)
            return null;
        var content = await LoadAsync(skillName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(content))
            return null;
        var doc = Frontmatter.Parse(content);
        var body = doc.HasFrontmatter ? doc.Body : content;
        return new SkillInvocation
        {
            Name = info.Name,
            Content = body,
            Args = args,
            DirectoryPath = info.DirectoryPath
        };
    }

    /// <inheritdoc />
    public string? GetSkillPath(string skillName)
        => Snapshot().TryGetValue(skillName, out var info) ? info.FilePath : null;

    /// <summary>True when the model may see/invoke the skill (valid, not manual-only, not hidden by an override).</summary>
    public bool IsVisible(SkillInfo skill)
    {
        if (!skill.IsValid) return false;
        // Explicit override wins.
        if (_overrides.TryGetValue(skill.Name, out var mode))
            return string.Equals(mode, "visible", StringComparison.OrdinalIgnoreCase);
        // Manual-only skills are hidden from model-visible list until invoked.
        return !skill.DisableModelInvocation;
    }

    // ===================== Write =====================

    /// <inheritdoc />
    public async Task<SkillInfo> CreateAsync(SkillDraft draft, SkillScope scope, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (LocalFileStore.ValidateName(draft.Name, "Skill name") is { } nameError) errors.Add(nameError);
        if (string.IsNullOrWhiteSpace(draft.Description)) errors.Add("Description is required: it is how the agent decides when to use the skill.");
        var root = RootFor(scope);
        if (root == null) errors.Add("No project skills directory is configured.");
        if (errors.Count > 0) throw new SkillValidationException(errors);

        var dir = Path.Combine(root!, draft.Name);
        if (File.Exists(Path.Combine(dir, SkillFileName)))
            throw new SkillValidationException($"A {scope.ToString().ToLowerInvariant()} skill named '{draft.Name}' already exists.");

        var content = BuildSkillFile(draft);
        await LocalFileStore.WriteAllTextAtomicAsync(Path.Combine(dir, SkillFileName), content, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Created {Scope} skill {Name} in {Dir}", scope, draft.Name, dir);
        return AfterWrite(draft.Name);
    }

    /// <summary>Render a new SKILL.md from a draft.</summary>
    public static string BuildSkillFile(SkillDraft draft)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("name", draft.Name),
            new("description", Frontmatter.QuoteIfNeeded(draft.Description.Trim()))
        };
        if (draft.Tags.Count > 0) fields.Add(new("tags", Frontmatter.FormatList(draft.Tags)));
        if (draft.AllowedTools.Count > 0) fields.Add(new("allowed-tools", Frontmatter.FormatList(draft.AllowedTools)));
        if (draft.DisableModelInvocation) fields.Add(new("disable-model-invocation", "true"));
        var body = string.IsNullOrWhiteSpace(draft.Body)
            ? $"# {draft.Name}\n\nDescribe, step by step, what the agent should do when this skill is used.\n"
            : draft.Body;
        return Frontmatter.Compose(fields, body);
    }

    /// <inheritdoc />
    public async Task<SkillInfo> UpdateAsync(string skillName, string content, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var existing = Find(skillName, scope)
            ?? throw new SkillValidationException($"Skill '{skillName}' does not exist.");

        var doc = Frontmatter.Parse(content);
        var errors = new List<string>();
        if (!doc.HasFrontmatter) errors.Add("The skill must start with a frontmatter block (--- name/description ---).");
        var fmName = doc.GetString("name");
        if (!string.IsNullOrEmpty(fmName) && !string.Equals(fmName, existing.Name, StringComparison.Ordinal))
            errors.Add($"Frontmatter name '{fmName}' does not match the skill folder '{existing.Name}'. Use rename to change a skill's name.");
        if (doc.HasFrontmatter && string.IsNullOrWhiteSpace(doc.GetString("description")))
            errors.Add("Description is required: it is how the agent decides when to use the skill.");
        if (errors.Count > 0) throw new SkillValidationException(errors);

        await LocalFileStore.WriteAllTextAtomicAsync(existing.FilePath!, content, cancellationToken).ConfigureAwait(false);
        return AfterWrite(existing.Name);
    }

    /// <inheritdoc />
    public async Task<SkillInfo> RenameAsync(string oldName, string newName, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var existing = Find(oldName, scope)
            ?? throw new SkillValidationException($"Skill '{oldName}' does not exist.");
        if (LocalFileStore.ValidateName(newName, "Skill name") is { } nameError)
            throw new SkillValidationException(nameError);
        if (string.Equals(existing.Name, newName, StringComparison.Ordinal))
            return existing;

        var root = RootFor(existing.Scope)!;
        var newDir = Path.Combine(root, newName);
        // Case-only renames are allowed on case-insensitive file systems.
        var caseOnly = string.Equals(existing.Name, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Directory.Exists(newDir))
            throw new SkillValidationException($"A {existing.Scope.ToString().ToLowerInvariant()} skill named '{newName}' already exists.");

        var content = await File.ReadAllTextAsync(existing.FilePath!, cancellationToken).ConfigureAwait(false);
        var updated = Frontmatter.SetField(content, "name", newName);

        if (caseOnly)
        {
            var tmpDir = Path.Combine(root, "." + newName + "." + Guid.NewGuid().ToString("N")[..6]);
            Directory.Move(existing.DirectoryPath!, tmpDir);
            Directory.Move(tmpDir, newDir);
        }
        else
        {
            Directory.Move(existing.DirectoryPath!, newDir);
        }
        await LocalFileStore.WriteAllTextAtomicAsync(Path.Combine(newDir, SkillFileName), updated, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Renamed skill {Old} -> {New}", existing.Name, newName);
        return AfterWrite(newName);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string skillName, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var existing = Find(skillName, scope);
        if (existing?.DirectoryPath == null || !Directory.Exists(existing.DirectoryPath))
            return Task.FromResult(false);
        Directory.Delete(existing.DirectoryPath, recursive: true);
        _logger?.LogInformation("Deleted {Scope} skill {Name}", existing.Scope, existing.Name);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public void Refresh()
    {
        lock (_gate)
        {
            _scanned = false;
        }
    }

    private SkillInfo AfterWrite(string name)
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return Snapshot().TryGetValue(name, out var info)
            ? info
            : throw new InvalidOperationException($"Skill '{name}' was written but could not be read back.");
    }

    private string? RootFor(SkillScope scope) => scope switch
    {
        SkillScope.Global => _globalSkillsDir,
        SkillScope.Project => _projectSkillsDir,
        _ => null
    };

    /// <summary>Find a skill by name in a specific scope, or the effective copy when <paramref name="scope"/> is null.</summary>
    private SkillInfo? Find(string name, SkillScope? scope)
    {
        Snapshot();
        lock (_gate)
        {
            var source = scope switch
            {
                SkillScope.Global => _globalCache,
                SkillScope.Project => _projectCache,
                _ => _cache
            };
            return source.TryGetValue(name, out var info) ? info : null;
        }
    }

    // ===================== Scan =====================

    private Dictionary<string, SkillInfo> Snapshot()
    {
        lock (_gate)
        {
            if (_scanned) return _cache;
            var global = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
            var project = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ScanDirectory(_globalSkillsDir, SkillScope.Global, global);
                if (!string.IsNullOrEmpty(_projectSkillsDir))
                    ScanDirectory(_projectSkillsDir, SkillScope.Project, project);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to scan skills directories");
            }

            var merged = new Dictionary<string, SkillInfo>(global, StringComparer.OrdinalIgnoreCase);
            // Project skills override global skills with the same name.
            foreach (var (name, info) in project)
                merged[name] = global.ContainsKey(name) ? info with { ShadowsGlobal = true } : info;

            _globalCache = global;
            _projectCache = project;
            _cache = merged;
            _scanned = true;
            return _cache;
        }
    }

    private void ScanDirectory(string dir, SkillScope scope, Dictionary<string, SkillInfo> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var folderName = Path.GetFileName(sub);
            if (folderName.StartsWith('.')) continue; // temp/trash folders
            var skillFile = Path.Combine(sub, SkillFileName);
            if (!File.Exists(skillFile)) continue;
            try
            {
                into[folderName] = ParseSkillFile(folderName, skillFile, scope);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse skill {Path}", skillFile);
            }
        }
    }

    /// <summary>Parse one SKILL.md into <see cref="SkillInfo"/> (never throws for content problems; they become validation errors).</summary>
    internal static SkillInfo ParseSkillFile(string folderName, string filePath, SkillScope scope)
    {
        var content = File.ReadAllText(filePath);
        var doc = Frontmatter.Parse(content);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!doc.HasFrontmatter)
            errors.Add("Missing frontmatter block (--- name/description ---) at the top of SKILL.md.");

        var fmName = doc.GetString("name");
        if (!string.IsNullOrEmpty(fmName) && !string.Equals(fmName, folderName, StringComparison.Ordinal))
            warnings.Add($"Frontmatter name '{fmName}' differs from folder '{folderName}'; the folder name is used.");
        if (!LocalFileStore.IsValidName(folderName))
            warnings.Add($"Folder name '{folderName}' is not kebab-case; rename it to avoid problems.");

        var description = doc.GetString("description") ?? string.Empty;
        if (doc.HasFrontmatter && string.IsNullOrWhiteSpace(description))
            warnings.Add("No description: the agent cannot tell when to use this skill.");

        var tokens = LocalFileStore.EstimateTokens(doc.HasFrontmatter ? doc.Body : content);
        if (tokens > LargeSkillTokenThreshold)
            warnings.Add($"Large skill (~{tokens:N0} tokens). Pinning it costs that much on every turn.");

        return new SkillInfo
        {
            Name = folderName,
            Description = description,
            DisableModelInvocation = doc.GetBool("disable-model-invocation"),
            FilePath = filePath,
            DirectoryPath = Path.GetDirectoryName(filePath),
            Scope = scope,
            Tags = doc.GetList("tags"),
            Version = doc.GetInt("version"),
            AllowedTools = doc.GetList("allowed-tools"),
            EstimatedTokens = tokens,
            ValidationErrors = errors,
            Warnings = warnings
        };
    }

    // ===================== Back-compat helpers =====================

    /// <summary>Parse frontmatter (name, description, disable-model-invocation) from a SKILL.md file. Returns null when the file has no frontmatter.</summary>
    internal static SkillInfo? ParseFrontmatter(string name, string filePath)
    {
        var info = ParseSkillFile(name, filePath, SkillScope.Global);
        return info.IsValid ? info : null;
    }

    /// <summary>Split a markdown file into (frontmatter, body). Frontmatter is delimited by <c>---</c>.</summary>
    internal static (string? frontmatter, string body) SplitFrontmatter(string content) => Frontmatter.Split(content);

    /// <summary>Extract a simple <c>key: value</c> scalar field from frontmatter text.</summary>
    internal static string? GetFrontmatterValue(string frontmatter, string key)
        => Frontmatter.ParseFields(frontmatter).TryGetValue(key, out var v) && v is string s ? s : null;
}
