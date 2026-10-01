using System.Text;

namespace AiCodeAgent.Core.Diffing;

/// <summary>
/// Builds a standard unified-diff string (with <c>--- a/</c>/<c>+++ b/</c>
/// headers and <c>@@ -o,oc +n,nc @@</c> hunk headers) from two versions of a
/// file's text. The output is directly parseable by <see cref="DiffParser"/>,
/// so this is the shared engine for anything that needs to show or apply a
/// real diff — the standalone diff viewer, checkpoint comparisons, etc.
/// </summary>
public static class UnifiedDiffBuilder
{
    /// <summary>
    /// Lines beyond this count on either side fall back to a single
    /// whole-file replacement hunk rather than running the O(n*m) LCS —
    /// keeps worst-case memory/time bounded for huge generated files.
    /// </summary>
    private const int MaxLinesForLcs = 4000;

    /// <summary>
    /// Build a unified diff between <paramref name="oldText"/> and
    /// <paramref name="newText"/>. Returns an empty string when the two are
    /// identical.
    /// </summary>
    public static string Build(string oldText, string newText, string filePath, int contextLines = 3)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (oldText == newText)
            return string.Empty;

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var ops = oldLines.Length > MaxLinesForLcs || newLines.Length > MaxLinesForLcs
            ? WholeFileReplace(oldLines, newLines)
            : DiffLines(oldLines, newLines);

        return Render(ops, filePath, contextLines);
    }

    private enum OpKind { Equal, Delete, Insert }
    private readonly record struct Op(OpKind Kind, string Text);

    /// <summary>Classic O(n*m) longest-common-subsequence line diff.</summary>
    private static List<Op> DiffLines(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(new Op(OpKind.Equal, a[x]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(new Op(OpKind.Delete, a[x]));
                x++;
            }
            else
            {
                ops.Add(new Op(OpKind.Insert, b[y]));
                y++;
            }
        }
        while (x < n) { ops.Add(new Op(OpKind.Delete, a[x])); x++; }
        while (y < m) { ops.Add(new Op(OpKind.Insert, b[y])); y++; }
        return ops;
    }

    private static List<Op> WholeFileReplace(string[] a, string[] b)
    {
        var ops = new List<Op>(a.Length + b.Length);
        foreach (var line in a) ops.Add(new Op(OpKind.Delete, line));
        foreach (var line in b) ops.Add(new Op(OpKind.Insert, line));
        return ops;
    }

    /// <summary>
    /// Groups the raw equal/delete/insert ops into context-bounded hunks and
    /// renders them as unified-diff text. Line numbers for each hunk are
    /// derived from precomputed prefix positions (by op *index*, not by
    /// content), so repeated/blank lines never throw the numbering off.
    /// </summary>
    private static string Render(List<Op> ops, string filePath, int contextLines)
    {
        // oldPos[i] / newPos[i] = the 1-based old/new-file line number that
        // op i would occupy — i.e. 1 + how many prior ops consumed a line
        // on that side. Defined for every index, including pure-insert or
        // pure-delete ops, which is exactly what unified-diff headers need.
        var oldPos = new int[ops.Count + 1];
        var newPos = new int[ops.Count + 1];
        oldPos[0] = 1;
        newPos[0] = 1;
        for (var i = 0; i < ops.Count; i++)
        {
            oldPos[i + 1] = oldPos[i] + (ops[i].Kind is OpKind.Equal or OpKind.Delete ? 1 : 0);
            newPos[i + 1] = newPos[i] + (ops[i].Kind is OpKind.Equal or OpKind.Insert ? 1 : 0);
        }

        // Find contiguous change regions (runs containing at least one
        // Delete/Insert), then expand each by `contextLines` on both sides,
        // merging regions whose expanded context windows overlap.
        var changeRuns = new List<(int Start, int End)>(); // [Start, End) over ops indices
        var i2 = 0;
        while (i2 < ops.Count)
        {
            if (ops[i2].Kind == OpKind.Equal) { i2++; continue; }
            var start = i2;
            while (i2 < ops.Count && ops[i2].Kind != OpKind.Equal) i2++;
            changeRuns.Add((start, i2));
        }

        if (changeRuns.Count == 0)
            return string.Empty;

        var hunkRanges = new List<(int Start, int End)>();
        foreach (var run in changeRuns)
        {
            var start = Math.Max(0, run.Start - contextLines);
            var end = Math.Min(ops.Count, run.End + contextLines);
            if (hunkRanges.Count > 0 && start <= hunkRanges[^1].End)
            {
                var last = hunkRanges[^1];
                hunkRanges[^1] = (last.Start, end);
            }
            else
            {
                hunkRanges.Add((start, end));
            }
        }

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(filePath).Append('\n');
        sb.Append("+++ b/").Append(filePath).Append('\n');

        foreach (var (start, end) in hunkRanges)
        {
            var oldStart = oldPos[start];
            var newStart = newPos[start];
            var oldCount = 0;
            var newCount = 0;
            for (var k = start; k < end; k++)
            {
                if (ops[k].Kind is OpKind.Equal or OpKind.Delete) oldCount++;
                if (ops[k].Kind is OpKind.Equal or OpKind.Insert) newCount++;
            }

            sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
              .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");

            for (var k = start; k < end; k++)
            {
                var prefix = ops[k].Kind switch
                {
                    OpKind.Equal => ' ',
                    OpKind.Delete => '-',
                    OpKind.Insert => '+',
                    _ => ' '
                };
                sb.Append(prefix).Append(ops[k].Text).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');
}
