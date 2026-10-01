using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Resolves a <c>data-source-id</c> (e.g. "src:Foo.cs:12-18") back to a source
/// file + line range, and produces <see cref="DiffHunk"/>s for write-back edits.
/// </summary>
public sealed class SourceIdResolver
{
    /// <summary>
    /// Pattern: src:{filePath}:{startLine}-{endLine}
    /// </summary>
    private static readonly Regex SourceIdPattern = new(
        @"^src:(.+):(\d+)-(\d+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a <c>data-source-id</c> into a resolved source span.
    /// </summary>
    public SourceSpan? Resolve(string dataSourceId)
    {
        if (string.IsNullOrWhiteSpace(dataSourceId))
            return null;

        var match = SourceIdPattern.Match(dataSourceId);
        if (!match.Success)
            return null;

        var filePath = match.Groups[1].Value;
        if (!int.TryParse(match.Groups[2].Value, out var startLine))
            return null;
        if (!int.TryParse(match.Groups[3].Value, out var endLine))
            return null;

        return new SourceSpan(filePath, startLine, endLine);
    }

    /// <summary>
    /// Read the current text of the target lines from disk (1-based).
    /// Returns null if the file cannot be read or the line range is out of bounds.
    /// </summary>
    public string? GetOriginalText(SourceSpan span)
    {
        if (!File.Exists(span.FilePath))
            return null;

        try
        {
        var allLines = File.ReadAllLines(span.FilePath);
            if (span.StartLine < 1 || span.EndLine > allLines.Length)
                return null;

            var count = span.EndLine - span.StartLine + 1;
            return string.Join("\n", allLines[(span.StartLine - 1)..(span.StartLine - 1 + count)]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Build a <see cref="DiffHunk"/> representing a replacement of the span
    /// with <paramref name="newText"/>. The hunk is pending and attributed to
    /// the "visual-editor" agent.
    /// </summary>
    public DiffHunk BuildReplacementHunk(SourceSpan span, string newText, string? agentId = null)
    {
        var original = GetOriginalText(span) ?? string.Empty;
        var origLines = original.Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        var lines = new List<DiffLine>();
        foreach (var l in origLines)
            lines.Add(new DiffLine(DiffLineKind.Removed, l));
        foreach (var l in newLines)
            lines.Add(new DiffLine(DiffLineKind.Added, l));

        return new DiffHunk(
            $"{span.FilePath}-vis-{span.StartLine}-{span.EndLine}-{Guid.NewGuid():N}".Substring(0, 64),
            span.FilePath,
            span.StartLine,
            origLines.Length,
            span.StartLine,
            newLines.Length,
            lines.ToArray(),
            agentId ?? "visual-editor",
            HunkStatus.Pending);
    }

    /// <summary>
    /// Resolve + build a hunk in one call. Returns null if the source-id is
    /// invalid or the file cannot be read.
    /// </summary>
    public DiffHunk? ResolveAndBuildHunk(string dataSourceId, string newText, string? agentId = null)
    {
        var span = Resolve(dataSourceId);
        if (span == null)
            return null;
        return BuildReplacementHunk(span.Value, newText, agentId);
    }
}

/// <summary>
/// A resolved source span: file path + 1-based line range.
/// </summary>
public readonly record struct SourceSpan(string FilePath, int StartLine, int EndLine)
{
    public int LineCount => Math.Max(1, EndLine - StartLine + 1);
}