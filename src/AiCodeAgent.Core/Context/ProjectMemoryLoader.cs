using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>Default file-based implementation of <see cref="IProjectMemoryLoader"/>.</summary>
public class ProjectMemoryLoader : IProjectMemoryLoader
{
    public const string MemoryFileName = "AIAGENT.md";
    public const string GlobalMemoryDir = ".aiagent";
    private readonly ILogger<ProjectMemoryLoader>? _logger;

    public ProjectMemoryLoader(ILogger<ProjectMemoryLoader>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ProjectMemory?> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return null;

        // Search order: working dir, then global ~/.aiagent/AIAGENT.md
        var candidates = new List<string>
        {
            Path.Combine(workingDirectory, MemoryFileName)
        };

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, GlobalMemoryDir, MemoryFileName));
        }

        string? foundPath = null;
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    foundPath = path;
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error checking memory file at {Path}", path);
            }
        }

        if (foundPath is null)
        {
            _logger?.LogDebug("No AIAGENT.md found in {Dir}", workingDirectory);
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(foundPath, cancellationToken).ConfigureAwait(false);
            return Parse(foundPath, content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read memory file {Path}", foundPath);
            return null;
        }
    }

    public async Task<string> InitAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("workingDirectory is required", nameof(workingDirectory));

        Directory.CreateDirectory(workingDirectory);
        var path = Path.Combine(workingDirectory, MemoryFileName);

        if (File.Exists(path))
            return path;

        var template = BuildTemplate(workingDirectory);
        await File.WriteAllTextAsync(path, template, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Created AIAGENT.md at {Path}", path);
        return path;
    }

    public Task<ProjectDoctorReport> DiagnoseAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();

        // 1. Working directory exists
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            checks.Add(new DoctorCheck("WorkingDirectory", DoctorStatus.Error, "No working directory set."));
            return Task.FromResult(new ProjectDoctorReport { Checks = checks });
        }

        checks.Add(Directory.Exists(workingDirectory)
            ? new DoctorCheck("WorkingDirectory", DoctorStatus.Ok, workingDirectory)
            : new DoctorCheck("WorkingDirectory", DoctorStatus.Error, $"Directory not found: {workingDirectory}", "Set a valid working directory with /cd <dir>."));

        // 2. Memory file exists
        var localPath = Path.Combine(workingDirectory, MemoryFileName);
        if (File.Exists(localPath))
        {
            checks.Add(new DoctorCheck("AIAGENT.md", DoctorStatus.Ok, $"Found at {localPath}"));
            try
            {
                var content = File.ReadAllTextAsync(localPath, cancellationToken).GetAwaiter().GetResult();
                checks.Add(string.IsNullOrWhiteSpace(content)
                    ? new DoctorCheck("AIAGENT.md content", DoctorStatus.Warning, "File is empty.", "Add instructions or run /init again.")
                    : new DoctorCheck("AIAGENT.md content", DoctorStatus.Ok, $"{content.Length} chars"));
            }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheck("AIAGENT.md content", DoctorStatus.Error, ex.Message));
            }
        }
        else
        {
            checks.Add(new DoctorCheck("AIAGENT.md", DoctorStatus.Warning, "Not found in working directory.", "Run /init to create one."));
        }

        // 3. Global memory file
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var globalPath = Path.Combine(home, GlobalMemoryDir, MemoryFileName);
            checks.Add(File.Exists(globalPath)
                ? new DoctorCheck("Global AIAGENT.md", DoctorStatus.Ok, $"Found at {globalPath}")
                : new DoctorCheck("Global AIAGENT.md", DoctorStatus.Ok, "Not present (optional)."));
        }

        // 4. Git repository
        var gitDir = Path.Combine(workingDirectory, ".git");
        checks.Add(Directory.Exists(gitDir) || File.Exists(gitDir)
            ? new DoctorCheck("Git", DoctorStatus.Ok, "Repository detected.")
            : new DoctorCheck("Git", DoctorStatus.Warning, "Not a git repository.", "Run `git init` for change tracking."));

        return Task.FromResult(new ProjectDoctorReport { Checks = checks });
    }

    internal static ProjectMemory Parse(string filePath, string content)
    {
        var instructions = ExtractSection(content, "Instructions");
        var conventions = ExtractSection(content, "Conventions");
        var compact = ExtractSection(content, "Compact Instructions");
        var build = ExtractSection(content, "Build Commands");
        var test = ExtractSection(content, "Test Commands");
        var lint = ExtractSection(content, "Lint Commands");
        var custom = ExtractCustomSections(content);

        return new ProjectMemory
        {
            FilePath = filePath,
            RawContent = content,
            Instructions = instructions,
            Conventions = conventions,
            CompactInstructions = compact,
            BuildCommands = build,
            TestCommands = test,
            LintCommands = lint,
            CustomSections = custom
        };
    }

    private static string ExtractSection(string content, string sectionName)
    {
        // Match "## <sectionName>" up to the next "## " header or EOF.
        var pattern = $@"##\s+{Regex.Escape(sectionName)}\s*\r?\n(.*?)(?=\r?\n##\s+|\z)";
        var match = Regex.Match(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Instructions", "Conventions", "Compact Instructions",
        "Build Commands", "Test Commands", "Lint Commands"
    };

    private static Dictionary<string, string> ExtractCustomSections(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Find all "## <Name>" headers.
        var headerPattern = @"(?m)^##\s+(.+?)\s*$";
        var headers = Regex.Matches(content, headerPattern);
        foreach (Match h in headers)
        {
            var name = h.Groups[1].Value.Trim();
            if (KnownSections.Contains(name)) continue;
            var body = ExtractSection(content, name);
            if (!string.IsNullOrWhiteSpace(body)) result[name] = body;
        }
        return result;
    }

    private static string BuildTemplate(string workingDirectory)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = "project";

        var sb = new StringBuilder();
        sb.AppendLine($"# AIAGENT.md — Project Memory for {name}");
        sb.AppendLine();
        sb.AppendLine("> Persistent instructions the agent loads at the start of every session.");
        sb.AppendLine("> Keep it concise; large files consume context.");
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("- Describe the project in 1-2 lines.");
        sb.AppendLine("- List key goals and constraints the agent must respect.");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine("- Code style, naming, formatting.");
        sb.AppendLine("- Preferred frameworks and libraries.");
        sb.AppendLine("- Test command: `dotnet test`");
        sb.AppendLine("- Build command: `dotnet build`");
        sb.AppendLine();
        sb.AppendLine("## Compact Instructions");
        sb.AppendLine("- When compacting context, keep the most recent tool results.");
        sb.AppendLine("- Preserve any error messages and the current task goal.");
        return sb.ToString();
    }
}