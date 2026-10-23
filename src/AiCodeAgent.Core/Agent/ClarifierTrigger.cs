using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Decides whether a message should start the requirements clarifier (Feature 8). The clarifier
/// costs two extra LLM calls, so it runs only for a new app or feature being built, never for
/// questions, slash commands, or fix, refactor, test and review requests.
/// </summary>
public static class ClarifierTrigger
{
    private static readonly Regex BuildIntent = new(
        @"\b(?:create|build|make|scaffold|generate)\b(?:\s+\S+){0,4}?\s+(?:app|application|service|website|site|api|project|tool|game|dashboard)\b|\bnew\s+(?:app|service|website|api|project)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FreshStart = new(
        @"from\s+scratch|new\s+project", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ExcludedLeadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "explain", "why", "what", "how", "where", "who", "when", "is", "are", "does", "do",
        "fix", "refactor", "test", "review", "debug", "rename", "update", "improve", "optimize",
        "optimise", "document", "describe", "summarize", "summarise", "show", "find", "list", "check"
    };

    // One pattern per requirement dimension; a prompt that already names two or more is detailed enough.
    private static readonly Regex[] DimensionPatterns =
    {
        new(@"\b(?:web|mobile|desktop|cli|api)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:auth\w*|login|jwt|oauth)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:database|postgres\w*|mongo\w*|entity)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:tailwind|material|css|styled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:stripe|sendgrid|aws|integration)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:docker|vercel|deploy|serverless)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
        ".swift", ".rb", ".php", ".cpp", ".c", ".h", ".hpp", ".razor", ".xaml", ".html", ".css", ".vue", ".svelte"
    };

    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "target", "packages"
    };

    /// <summary>
    /// True when the message should start the clarifier. <paramref name="workingDirectoryHasSourceFiles"/>
    /// is evaluated only when it matters (Auto mode, after the build-intent check).
    /// </summary>
    public static bool ShouldClarify(
        string prompt,
        Func<bool> workingDirectoryHasSourceFiles,
        RequirementsClarifierMode mode)
    {
        if (mode == RequirementsClarifierMode.Off || string.IsNullOrWhiteSpace(prompt))
            return false;

        var text = prompt.Trim();
        if (text.StartsWith('/') || text.Contains('?'))
            return false;
        if (ExcludedLeadWords.Contains(LeadWord(text)))
            return false;
        if (!BuildIntent.IsMatch(text))
            return false;

        // Always: every build task asks, whatever the folder or the detail given.
        if (mode == RequirementsClarifierMode.Always)
            return true;

        // Auto: in an existing project, only a fresh start ("from scratch", "new project") asks.
        if (workingDirectoryHasSourceFiles() && !FreshStart.IsMatch(text))
            return false;

        return CountDimensions(text) < 2;
    }

    /// <summary>How many requirement dimensions (platform, auth, data, styling, integrations, deployment) the text names.</summary>
    public static int CountDimensions(string prompt) =>
        DimensionPatterns.Count(pattern => pattern.IsMatch(prompt));

    /// <summary>
    /// True when the folder holds at least one source file. Build, dependency and hidden folders are
    /// skipped, and the search stops at the first match.
    /// </summary>
    public static bool HasSourceFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (SourceExtensions.Contains(Path.GetExtension(file)))
                        return true;
                }

                foreach (var folder in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(folder);
                    if (!name.StartsWith('.') && !IgnoredFolders.Contains(name))
                        pending.Push(folder);
                }
            }
            catch (IOException)
            {
                // Unreadable folder: skip it rather than fail the send.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
        return false;
    }

    private static string LeadWord(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
            end++;
        return text[..end];
    }
}
