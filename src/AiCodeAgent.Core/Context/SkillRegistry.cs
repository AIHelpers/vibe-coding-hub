using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>
/// File-based implementation of <see cref="ISkillRegistry"/>. Scans
/// <c>~/.aiagent/skills/<name>/SKILL.md</c> (global) and
/// <c>.aiagent/skills/<name>/SKILL.md</c> (project) for skill files,
/// parses YAML-like frontmatter, and applies <see cref="AgentConfiguration.SkillOverrides"/>.
/// Project skills override global skills with the same name.
/// </summary>
public class SkillRegistry : ISkillRegistry
{
    private readonly ILogger<SkillRegistry>? _logger;
    private readonly string _globalSkillsDir;
    private readonly string? _projectSkillsDir;
    private readonly Dictionary<string, string> _overrides;
    private readonly Dictionary<string, SkillInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanned;

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

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureScanned();
        var visible = _cache.Values
            .Where(IsVisible)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<SkillInfo>>(visible);
    }

    /// <inheritdoc />
    public async Task<string> LoadAsync(string skillName, CancellationToken cancellationToken = default)
    {
        EnsureScanned();
        if (!_cache.TryGetValue(skillName, out var info) || info.FilePath == null)
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
        EnsureScanned();
        if (!_cache.TryGetValue(skillName, out var info) || info.FilePath == null)
            return null;
        var content = await LoadAsync(skillName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(content))
            return null;
        return new SkillInvocation
        {
            Name = skillName,
            Content = content,
            Args = args
        };
    }

    /// <inheritdoc />
    public string? GetSkillPath(string skillName)
    {
        EnsureScanned();
        return _cache.TryGetValue(skillName, out var info) ? info.FilePath : null;
    }

    private bool IsVisible(SkillInfo skill)
    {
        // Explicit override wins.
        if (_overrides.TryGetValue(skill.Name, out var mode))
        {
            return string.Equals(mode, "visible", StringComparison.OrdinalIgnoreCase);
        }
        // Manual-only skills are hidden from model-visible list until invoked.
        if (skill.DisableModelInvocation)
            return false;
        return true;
    }

    private void EnsureScanned()
    {
        if (_scanned) return;
        _scanned = true;
        try
        {
            ScanDirectory(_globalSkillsDir, isProject: false);
            if (!string.IsNullOrEmpty(_projectSkillsDir))
                ScanDirectory(_projectSkillsDir, isProject: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to scan skills directories");
        }
    }

    private void ScanDirectory(string dir, bool isProject)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            var skillFile = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            try
            {
                var info = ParseFrontmatter(name, skillFile);
                if (info == null) continue;
                // Project skills override global skills with the same name.
                _cache[name] = info with { };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse skill {Path}", skillFile);
            }
        }
    }

    /// <summary>Parse frontmatter (name, description, disable-model-invocation) from a SKILL.md file.</summary>
    internal static SkillInfo? ParseFrontmatter(string name, string filePath)
    {
        var content = File.ReadAllText(filePath);
        var (frontmatter, _) = SplitFrontmatter(content);
        if (frontmatter == null) return null;
        var description = GetFrontmatterValue(frontmatter, "description") ?? string.Empty;
        var disableModel = GetFrontmatterValue(frontmatter, "disable-model-invocation");
        var fmName = GetFrontmatterValue(frontmatter, "name") ?? name;
        return new SkillInfo
        {
            Name = fmName,
            Description = description,
            DisableModelInvocation = string.Equals(disableModel, "true", StringComparison.OrdinalIgnoreCase),
            FilePath = filePath
        };
    }

    /// <summary>Split a markdown file into (frontmatter, body). Frontmatter is delimited by <c>---</c>.</summary>
    internal static (string? frontmatter, string body) SplitFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content)) return (null, content);
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("---")) return (null, content);
        // Find the closing ---
        var rest = trimmed.Substring(3);
        var end = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0) return (null, content);
        var frontmatter = rest.Substring(0, end).Trim();
        var bodyStart = end + 4; // skip "\n---"
        var body = bodyStart < rest.Length ? rest.Substring(bodyStart).TrimStart('\r', '\n') : string.Empty;
        return (frontmatter, body);
    }

    /// <summary>Extract a simple <c>key: value</c> field from frontmatter text.</summary>
    internal static string? GetFrontmatterValue(string frontmatter, string key)
    {
        var match = Regex.Match(
            frontmatter,
            $@"(?m)^{Regex.Escape(key)}\s*:\s*(.+?)\s*$",
            RegexOptions.None);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }
}