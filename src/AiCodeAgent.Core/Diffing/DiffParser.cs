using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Diffing;

/// <summary>
/// Parses unified diff text (as produced by edit_file/write_file tools) into structured DiffHunk[].
/// </summary>
public static class DiffParser
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse unified diff text into hunks.
    /// </summary>
    /// <param name="diffText">The raw diff text (e.g. from edit_file output).</param>
    /// <param name="filePath">The file path the diff applies to.</param>
    /// <param name="agentId">Optional agent attribution.</param>
    /// <returns>Array of parsed hunks.</returns>
    public static DiffHunk[] Parse(string diffText, string filePath, string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(diffText))
            return Array.Empty<DiffHunk>();

        var lines = diffText.Split('\n');
        var hunks = new List<DiffHunk>();
        var currentHunk = new List<DiffLine>();
        int origStart = 0, origCount = 0, newStart = 0, newCount = 0;
        var inHunk = false;
        var hunkIndex = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Check for hunk header
            var match = HunkHeaderRegex.Match(line);
            if (match.Success)
            {
                // Flush previous hunk
                if (inHunk && currentHunk.Count > 0)
                {
                    hunks.Add(CreateHunk(hunkIndex++, filePath, origStart, origCount, newStart, newCount, currentHunk, agentId));
                }

                origStart = int.Parse(match.Groups[1].Value);
                origCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                newStart = int.Parse(match.Groups[3].Value);
                newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                currentHunk = new List<DiffLine>();
                inHunk = true;
                continue;
            }

            if (!inHunk)
                continue;

            // Parse diff lines
            if (line.StartsWith("+++") || line.StartsWith("---"))
                continue;

            if (line.StartsWith('+'))
            {
                currentHunk.Add(new DiffLine(DiffLineKind.Added, line[1..]));
            }
            else if (line.StartsWith('-'))
            {
                currentHunk.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
            }
            else if (line.StartsWith(' '))
            {
                currentHunk.Add(new DiffLine(DiffLineKind.Context, line[1..]));
            }
            else if (line.StartsWith("\\"))
            {
                // No newline at end of file marker - skip
                continue;
            }
            else
            {
                // End of hunk (blank line or other content)
                if (currentHunk.Count > 0)
                {
                    hunks.Add(CreateHunk(hunkIndex++, filePath, origStart, origCount, newStart, newCount, currentHunk, agentId));
                    currentHunk = new List<DiffLine>();
                }
                inHunk = false;
            }
        }

        // Flush last hunk
        if (inHunk && currentHunk.Count > 0)
        {
            hunks.Add(CreateHunk(hunkIndex++, filePath, origStart, origCount, newStart, newCount, currentHunk, agentId));
        }

        return hunks.ToArray();
    }

    /// <summary>
    /// Parse a simple line-based diff (as produced by EditFileTool.GenerateDiff) into hunks.
    /// This handles the non-unified format where lines are prefixed with - or +.
    /// </summary>
    public static DiffHunk[] ParseSimpleDiff(string diffText, string filePath, string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(diffText))
            return Array.Empty<DiffHunk>();

        var lines = diffText.Split('\n');
        var hunks = new List<DiffHunk>();
        var currentLines = new List<DiffLine>();
        int origStart = 0, newStart = 0;
        var inHunk = false;
        var hunkIndex = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip file headers
            if (line.StartsWith("--- ") || line.StartsWith("+++ "))
                continue;

            if (line.StartsWith('-'))
            {
                if (!inHunk)
                {
                    origStart = currentLines.Count(l => l.Kind != DiffLineKind.Added) + 1;
                    newStart = currentLines.Count(l => l.Kind != DiffLineKind.Removed) + 1;
                    inHunk = true;
                }
                currentLines.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
            }
            else if (line.StartsWith('+'))
            {
                if (!inHunk)
                {
                    origStart = currentLines.Count(l => l.Kind != DiffLineKind.Added) + 1;
                    newStart = currentLines.Count(l => l.Kind != DiffLineKind.Removed) + 1;
                    inHunk = true;
                }
                currentLines.Add(new DiffLine(DiffLineKind.Added, line[1..]));
            }
            else if (inHunk)
            {
                // End of hunk - flush
                if (currentLines.Count > 0)
                {
                    var removedCount = currentLines.Count(l => l.Kind == DiffLineKind.Removed);
                    var addedCount = currentLines.Count(l => l.Kind == DiffLineKind.Added);
                    hunks.Add(new DiffHunk(
                        $"{filePath}-h{hunkIndex++}",
                        filePath,
                        origStart,
                        removedCount,
                        newStart,
                        addedCount,
                        currentLines.ToArray(),
                        agentId ?? string.Empty,
                        HunkStatus.Pending));
                    currentLines = new List<DiffLine>();
                }
                inHunk = false;
            }
        }

        // Flush last hunk
        if (inHunk && currentLines.Count > 0)
        {
            var removedCount = currentLines.Count(l => l.Kind == DiffLineKind.Removed);
            var addedCount = currentLines.Count(l => l.Kind == DiffLineKind.Added);
            hunks.Add(new DiffHunk(
                $"{filePath}-h{hunkIndex++}",
                filePath,
                origStart,
                removedCount,
                newStart,
                addedCount,
                currentLines.ToArray(),
                agentId ?? string.Empty,
                HunkStatus.Pending));
        }

        return hunks.ToArray();
    }

    private static DiffHunk CreateHunk(
        int index,
        string filePath,
        int origStart,
        int origCount,
        int newStart,
        int newCount,
        List<DiffLine> lines,
        string? agentId)
    {
        return new DiffHunk(
            $"{filePath}-h{index}",
            filePath,
            origStart,
            origCount,
            newStart,
            newCount,
            lines.ToArray(),
            agentId ?? string.Empty,
            HunkStatus.Pending);
    }
}