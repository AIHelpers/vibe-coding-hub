using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>Default file-based implementation of <see cref="IProjectMemoryLoader"/>.</summary>
public class ProjectMemoryLoader : IProjectMemoryLoader
{
    /// <summary>
    /// Recognized project-memory filenames, in priority order. "AGENTS.md"
    /// is the emerging cross-tool convention (Codex, Cursor, aider, etc.);
    /// "AGENT.md" is a common singular variant some tools/users expect.
    /// "AIAGENT.md" is this app's original name, kept for backward
    /// compatibility with existing projects. Whichever is found first (in
    /// this order) wins; only one is loaded per directory.
    /// </summary>
    public static readonly string[] CandidateFileNames = { "AGENTS.md", "AGENT.md", "AIAGENT.md" };

    /// <summary>The filename InitAsync creates when no memory file exists yet.</summary>
    public const string MemoryFileName = "AGENTS.md";
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

        // Search order: each candidate name in the working directory first
        // (a local file always wins), then each candidate name in the
        // global ~/.aiagent/ directory.
        var candidates = new List<string>();
        foreach (var name in CandidateFileNames)
            candidates.Add(Path.Combine(workingDirectory, name));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            foreach (var name in CandidateFileNames)
                candidates.Add(Path.Combine(home, GlobalMemoryDir, name));
        }

        string? foundPath = FindExisting(candidates);

        if (foundPath is null)
        {
            _logger?.LogDebug("No project memory file ({Candidates}) found in {Dir}",
                string.Join(", ", CandidateFileNames), workingDirectory);
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

    /// <summary>Returns the first path in <paramref name="candidates"/> that exists on disk, or null.</summary>
    private string? FindExisting(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error checking memory file at {Path}", path);
            }
        }
        return null;
    }

    public async Task<string> InitAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("workingDirectory is required", nameof(workingDirectory));

        Directory.CreateDirectory(workingDirectory);

        // If any recognized memory file already exists locally, leave it
        // alone — "init" means "make sure one exists", not "create the
        // canonical name even when a differently-named one is already there".
        var localCandidates = CandidateFileNames.Select(name => Path.Combine(workingDirectory, name));
        var existing = FindExisting(localCandidates);
        if (existing != null)
            return existing;

        var path = Path.Combine(workingDirectory, MemoryFileName);
        var template = BuildTemplate(workingDirectory);
        await File.WriteAllTextAsync(path, template, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Created {FileName} at {Path}", MemoryFileName, path);
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

        // 2. Memory file exists (any recognized name)
        var localCandidates = CandidateFileNames.Select(name => Path.Combine(workingDirectory, name)).ToList();
        var localPath = FindExisting(localCandidates);
        if (localPath != null)
        {
            checks.Add(new DoctorCheck("Project Memory", DoctorStatus.Ok, $"Found {Path.GetFileName(localPath)} at {localPath}"));
            try
            {
                var content = File.ReadAllTextAsync(localPath, cancellationToken).GetAwaiter().GetResult();
                checks.Add(string.IsNullOrWhiteSpace(content)
                    ? new DoctorCheck("Project Memory content", DoctorStatus.Warning, "File is empty.", "Add instructions or run /init again.")
                    : new DoctorCheck("Project Memory content", DoctorStatus.Ok, $"{content.Length} chars"));
            }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheck("Project Memory content", DoctorStatus.Error, ex.Message));
            }
        }
        else
        {
            checks.Add(new DoctorCheck("Project Memory", DoctorStatus.Warning,
                $"None of {string.Join(", ", CandidateFileNames)} found in working directory.", "Run /init to create one."));
        }

        // 3. Global memory file
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var globalCandidates = CandidateFileNames.Select(name => Path.Combine(home, GlobalMemoryDir, name)).ToList();
            var globalPath = FindExisting(globalCandidates);
            checks.Add(globalPath != null
                ? new DoctorCheck("Global Project Memory", DoctorStatus.Ok, $"Found {Path.GetFileName(globalPath)} at {globalPath}")
                : new DoctorCheck("Global Project Memory", DoctorStatus.Ok, "Not present (optional)."));
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
        sb.AppendLine($"# AGENTS.md — Project Memory for {name}");
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