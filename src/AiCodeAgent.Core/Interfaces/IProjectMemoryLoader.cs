using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Loads and manages the project memory file (AIAGENT.md) which contains
/// persistent project-specific instructions, conventions, and compaction rules.
/// </summary>
public interface IProjectMemoryLoader
{
    /// <summary>Find and read the memory file for the given working directory.</summary>
    Task<ProjectMemory?> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>Scaffold a template memory file in the working directory.</summary>
    Task<string> InitAsync(string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>Diagnose setup/configuration issues and return a report.</summary>
    Task<ProjectDoctorReport> DiagnoseAsync(string workingDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Parsed project memory with structured sections.</summary>
public record ProjectMemory
{
    /// <summary>Raw file path that was loaded.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Raw markdown content.</summary>
    public string RawContent { get; init; } = string.Empty;

    /// <summary>Parsed "## Instructions" section (if present).</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>Parsed "## Conventions" section (if present).</summary>
    public string Conventions { get; init; } = string.Empty;

    /// <summary>Parsed "## Compact Instructions" section (if present).</summary>
    public string CompactInstructions { get; init; } = string.Empty;

    /// <summary>Parsed "## Build Commands" section (if present).</summary>
    public string BuildCommands { get; init; } = string.Empty;

    /// <summary>Parsed "## Test Commands" section (if present).</summary>
    public string TestCommands { get; init; } = string.Empty;

    /// <summary>Parsed "## Lint Commands" section (if present).</summary>
    public string LintCommands { get; init; } = string.Empty;

    /// <summary>Additional custom "## <Name>" sections not covered above.</summary>
    public Dictionary<string, string> CustomSections { get; init; } = new();

    /// <summary>Whether any memory content was actually loaded.</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(RawContent);

    /// <summary>Render the memory as a system-prompt block.</summary>
    public string ToPromptBlock()
    {
        if (!HasContent) return string.Empty;
        return $"""
            # Project Memory (AIAGENT.md)
            {RawContent.Trim()}
            """;
    }
}

/// <summary>Result of a /doctor diagnostic run.</summary>
public record ProjectDoctorReport
{
    public List<DoctorCheck> Checks { get; init; } = new();

    public bool AllOk => Checks.All(c => c.Status == DoctorStatus.Ok);
}

public record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? FixHint = null);

public enum DoctorStatus
{
    Ok,
    Warning,
    Error
}