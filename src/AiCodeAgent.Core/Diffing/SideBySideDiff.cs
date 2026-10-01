using System.Text;

namespace AiCodeAgent.Core.Diffing;

/// <summary>A run of text inside one line; <see cref="Changed"/> marks the words that differ from the paired line.</summary>
public sealed record DiffSegment(string Text, bool Changed);

public enum SideBySideRowKind
{
    /// <summary>A pair of lines (old on the left, new on the right; either side can be empty).</summary>
    Line,
    /// <summary>The "@@ -12,7 +12,9 @@" header that starts every hunk.</summary>
    HunkHeader,
    /// <summary>A collapsed stretch of unchanged lines between hunks that can be expanded.</summary>
    Gap
}

public enum SideBySideCellKind
{
    /// <summary>No line on this side (the other side was added or removed).</summary>
    Empty,
    Context,
    Removed,
    Added
}

/// <summary>One row of the GitHub-style split view.</summary>
public sealed record SideBySideRow
{
    public SideBySideRowKind Kind { get; init; }

    public int? OldNumber { get; init; }
    public string OldText { get; init; } = string.Empty;
    public SideBySideCellKind OldKind { get; init; }
    /// <summary>Word-level highlight for a modified line; null when the whole line is one colour.</summary>
    public IReadOnlyList<DiffSegment>? OldSegments { get; init; }

    public int? NewNumber { get; init; }
    public string NewText { get; init; } = string.Empty;
    public SideBySideCellKind NewKind { get; init; }
    public IReadOnlyList<DiffSegment>? NewSegments { get; init; }

    /// <summary>For header rows: the hunk this row opens (so it can be reverted).</summary>
    public DiffHunk? Hunk { get; init; }
    public string Header { get; init; } = string.Empty;

    /// <summary>For gap rows: first hidden line on each side and how many lines are hidden.</summary>
    public int GapOldStart { get; init; }
    public int GapNewStart { get; init; }
    public int GapCount { get; init; }

    public static SideBySideRow Context(int oldNumber, int newNumber, string text) => new()
    {
        Kind = SideBySideRowKind.Line,
        OldNumber = oldNumber, OldText = text, OldKind = SideBySideCellKind.Context,
        NewNumber = newNumber, NewText = text, NewKind = SideBySideCellKind.Context
    };
}

public sealed class SideBySideDiffResult
{
    public IReadOnlyList<SideBySideRow> Rows { get; init; } = Array.Empty<SideBySideRow>();
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
    public string[] OldLines { get; init; } = Array.Empty<string>();
    public string[] NewLines { get; init; } = Array.Empty<string>();
    /// <summary>True when the file was so large that only the first part of the diff is shown.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Builds the data behind a GitHub-style split diff: the old file on the left, the new file on the
/// right, rows aligned, removed lines red, added lines green, changed words highlighted inside
/// modified lines, and unchanged stretches collapsed into expandable gaps.
/// </summary>
public static class SideBySideDiff
{
    public const int DefaultContext = 3;

    /// <summary>Rows beyond this are not produced (keeps the UI responsive on giant generated files).</summary>
    public const int MaxRows = 20_000;

    /// <summary>If two files need more edits than this the changed region is shown as one replace instead of a minimal diff.</summary>
    private const int MaxEdits = 3_000;

    public static SideBySideDiffResult Build(string? oldText, string? newText, string filePath, int context = DefaultContext)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        if (a.Length == b.Length && a.SequenceEqual(b))
            return new SideBySideDiffResult { OldLines = a, NewLines = b };

        var ops = ComputeOps(a, b);

        // 1. Flatten into aligned line rows. A change block pairs its deleted and inserted lines one-to-one
        //    (those become word-diffed "modified" rows); leftovers sit opposite an empty cell.
        var lines = new List<LineRow>(ops.Count);
        int added = 0, removed = 0;
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                lines.Add(new LineRow(ops[i].OldIndex, ops[i].NewIndex, false));
                i++;
                continue;
            }

            var deletes = new List<int>();
            var inserts = new List<int>();
            while (i < ops.Count && ops[i].Kind != OpKind.Equal)
            {
                if (ops[i].Kind == OpKind.Delete) deletes.Add(ops[i].OldIndex); else inserts.Add(ops[i].NewIndex);
                i++;
            }
            removed += deletes.Count;
            added += inserts.Count;
            for (var k = 0; k < Math.Max(deletes.Count, inserts.Count); k++)
                lines.Add(new LineRow(k < deletes.Count ? deletes[k] : -1, k < inserts.Count ? inserts[k] : -1, true));
        }

        // 2. prefix counts: how many old/new lines come before each row — gives every number, hunk start and gap start.
        var prefOld = new int[lines.Count + 1];
        var prefNew = new int[lines.Count + 1];
        for (var i = 0; i < lines.Count; i++)
        {
            prefOld[i + 1] = prefOld[i] + (lines[i].Old >= 0 ? 1 : 0);
            prefNew[i + 1] = prefNew[i] + (lines[i].New >= 0 ? 1 : 0);
        }

        // 3. which rows are shown: every changed row plus `context` rows around it.
        var visible = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].IsChange) continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                visible[j] = true;
        }

        var rows = new List<SideBySideRow>();
        var hunks = new List<DiffHunk>();
        var truncated = false;
        var prevEnd = -1;
        var idx = 0;

        for (var i = 0; i < lines.Count && !truncated;)
        {
            if (!visible[i]) { i++; continue; }
            var start = i;
            while (i < lines.Count && visible[i]) i++;
            var end = i - 1; // inclusive

            if (start - prevEnd - 1 > 0)
                rows.Add(GapRow(prefOld[prevEnd + 1] + 1, prefNew[prevEnd + 1] + 1, start - prevEnd - 1));

            var oldStart = prefOld[start] + 1;
            var newStart = prefNew[start] + 1;
            var oldCount = prefOld[end + 1] - prefOld[start];
            var newCount = prefNew[end + 1] - prefNew[start];

            var hunkLines = BuildHunkLines(lines, start, end, a, b);
            var hunk = new DiffHunk($"{filePath}-h{idx++}", filePath, oldStart, oldCount, newStart, newCount,
                hunkLines, string.Empty, HunkStatus.Pending);
            hunks.Add(hunk);

            rows.Add(new SideBySideRow
            {
                Kind = SideBySideRowKind.HunkHeader,
                Hunk = hunk,
                Header = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@"
            });

            for (var r = start; r <= end; r++)
            {
                if (rows.Count >= MaxRows) { truncated = true; break; }
                rows.Add(ToRow(lines[r], prefOld[r], prefNew[r], a, b));
            }
            prevEnd = end;
        }

        if (!truncated && prevEnd < lines.Count - 1)
            rows.Add(GapRow(prefOld[prevEnd + 1] + 1, prefNew[prevEnd + 1] + 1, lines.Count - 1 - prevEnd));

        return new SideBySideDiffResult
        {
            Rows = rows,
            Hunks = hunks,
            AddedLines = added,
            RemovedLines = removed,
            OldLines = a,
            NewLines = b,
            Truncated = truncated
        };
    }

    private readonly record struct LineRow(int Old, int New, bool IsChange);

    private static SideBySideRow GapRow(int oldStart, int newStart, int count) => new()
    {
        Kind = SideBySideRowKind.Gap,
        GapOldStart = oldStart,
        GapNewStart = newStart,
        GapCount = count
    };

    private static SideBySideRow ToRow(LineRow row, int oldBefore, int newBefore, string[] a, string[] b)
    {
        if (!row.IsChange)
            return SideBySideRow.Context(oldBefore + 1, newBefore + 1, a[row.Old]);

        var hasOld = row.Old >= 0;
        var hasNew = row.New >= 0;
        IReadOnlyList<DiffSegment>? oldSeg = null, newSeg = null;
        if (hasOld && hasNew)
            (oldSeg, newSeg) = WordDiff.Compute(a[row.Old], b[row.New]);

        return new SideBySideRow
        {
            Kind = SideBySideRowKind.Line,
            OldNumber = hasOld ? oldBefore + 1 : null,
            OldText = hasOld ? a[row.Old] : string.Empty,
            OldKind = hasOld ? SideBySideCellKind.Removed : SideBySideCellKind.Empty,
            OldSegments = oldSeg,
            NewNumber = hasNew ? newBefore + 1 : null,
            NewText = hasNew ? b[row.New] : string.Empty,
            NewKind = hasNew ? SideBySideCellKind.Added : SideBySideCellKind.Empty,
            NewSegments = newSeg
        };
    }

    /// <summary>Unified-diff style lines for a hunk (all removals of a block, then all additions), used for reverting.</summary>
    private static IReadOnlyList<DiffLine> BuildHunkLines(List<LineRow> lines, int start, int end, string[] a, string[] b)
    {
        var result = new List<DiffLine>();
        var removed = new List<DiffLine>();
        var added = new List<DiffLine>();

        void Flush()
        {
            result.AddRange(removed);
            result.AddRange(added);
            removed.Clear();
            added.Clear();
        }

        for (var r = start; r <= end; r++)
        {
            var row = lines[r];
            if (!row.IsChange)
            {
                Flush();
                result.Add(new DiffLine(DiffLineKind.Context, a[row.Old]));
                continue;
            }
            if (row.Old >= 0) removed.Add(new DiffLine(DiffLineKind.Removed, a[row.Old]));
            if (row.New >= 0) added.Add(new DiffLine(DiffLineKind.Added, b[row.New]));
        }
        Flush();
        return result;
    }

    /// <summary>Splits on line breaks, dropping a trailing empty line and any carriage returns.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var parts = text.Split('\n');
        var count = parts.Length;
        if (parts[^1].Length == 0)
            count--; // text ended with a newline

        var lines = new string[count];
        for (var i = 0; i < count; i++)
            lines[i] = parts[i].TrimEnd('\r');
        return lines;
    }

    // ----- line diff (Myers, O(ND)) -----

    private enum OpKind { Equal, Delete, Insert }
    private readonly record struct Op(OpKind Kind, int OldIndex, int NewIndex);

    private static List<Op> ComputeOps(string[] a, string[] b)
    {
        var ops = new List<Op>(Math.Max(a.Length, b.Length));

        // Identical lines at the start and end are the vast majority in real edits — peel them off first.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        for (var i = 0; i < prefix; i++)
            ops.Add(new Op(OpKind.Equal, i, i));

        var aMid = a.Length - prefix - suffix;
        var bMid = b.Length - prefix - suffix;
        var middle = MyersMiddle(a, b, prefix, aMid, bMid);
        ops.AddRange(middle);

        for (var i = 0; i < suffix; i++)
            ops.Add(new Op(OpKind.Equal, a.Length - suffix + i, b.Length - suffix + i));

        return ops;
    }

    private static List<Op> MyersMiddle(string[] a, string[] b, int offset, int n, int m)
    {
        var ops = new List<Op>();
        if (n == 0 && m == 0) return ops;
        if (n == 0) { for (var j = 0; j < m; j++) ops.Add(new Op(OpKind.Insert, -1, offset + j)); return ops; }
        if (m == 0) { for (var i = 0; i < n; i++) ops.Add(new Op(OpKind.Delete, offset + i, -1)); return ops; }

        // Compare ints, not strings.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s)
        {
            if (!ids.TryGetValue(s, out var id)) ids[s] = id = ids.Count;
            return id;
        }
        var x = new int[n];
        var y = new int[m];
        for (var i = 0; i < n; i++) x[i] = Id(a[offset + i]);
        for (var j = 0; j < m; j++) y[j] = Id(b[offset + j]);

        var trace = Myers(x, y);
        if (trace == null)
        {
            // Too different for a minimal diff: show the region as one replace.
            for (var i = 0; i < n; i++) ops.Add(new Op(OpKind.Delete, offset + i, -1));
            for (var j = 0; j < m; j++) ops.Add(new Op(OpKind.Insert, -1, offset + j));
            return ops;
        }

        // Walk the trace backwards to recover the edit script.
        var script = new List<Op>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            int prevX, prevY;
            if (d == 0)
            {
                prevX = 0; prevY = 0;
            }
            else
            {
                var v = trace[d];
                var k = cx - cy;
                var prevK = (k == -d || (k != d && At(v, d, k - 1) < At(v, d, k + 1))) ? k + 1 : k - 1;
                prevX = At(v, d, prevK);
                prevY = prevX - prevK;
            }

            while (cx > prevX && cy > prevY)
            {
                cx--; cy--;
                script.Add(new Op(OpKind.Equal, offset + cx, offset + cy));
            }

            if (d > 0)
            {
                if (cx == prevX) { cy--; script.Add(new Op(OpKind.Insert, -1, offset + cy)); }
                else { cx--; script.Add(new Op(OpKind.Delete, offset + cx, -1)); }
            }
        }

        script.Reverse();
        return script;
    }

    private static int At(int[] slice, int d, int k) => slice[k + d];

    /// <summary>Myers' greedy forward search; returns, for each edit distance d, the furthest-reaching x per diagonal k ∈ [-d, d], or null if it needs more than <see cref="MaxEdits"/> edits.</summary>
    private static List<int[]>? Myers(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        var limit = Math.Min(n + m, MaxEdits);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= limit; d++)
        {
            // Snapshot of v as it stood before this round: the backtrack reads k±1 from it.
            var slice = new int[2 * d + 1];
            for (var k = -d; k <= d; k++) slice[k + d] = v[offset + k];
            trace.Add(slice);

            for (var k = -d; k <= d; k += 2)
            {
                int xx;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    xx = v[offset + k + 1];
                else
                    xx = v[offset + k - 1] + 1;

                var yy = xx - k;
                while (xx < n && yy < m && a[xx] == b[yy]) { xx++; yy++; }
                v[offset + k] = xx;

                if (xx >= n && yy >= m)
                    return trace;
            }
        }
        return null;
    }
}

/// <summary>Word-level comparison of two lines, so the actual edit stands out inside a modified line.</summary>
public static class WordDiff
{
    private const int MaxTokens = 400;
    private const double MinSimilarity = 0.35;

    /// <summary>
    /// Returns highlight segments for both lines, or (null, null) when the lines are too different
    /// (or too long) for word highlighting to help — then the whole line is simply coloured.
    /// </summary>
    public static (IReadOnlyList<DiffSegment>? Old, IReadOnlyList<DiffSegment>? New) Compute(string oldLine, string newLine)
    {
        var a = Tokenize(oldLine);
        var b = Tokenize(newLine);
        if (a.Count == 0 || b.Count == 0 || a.Count > MaxTokens || b.Count > MaxTokens)
            return (null, null);

        int n = a.Count, m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var aKeep = new bool[n];
        var bKeep = new bool[m];
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { aKeep[x] = bKeep[y] = true; x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
            else y++;
        }

        // Lines that share almost nothing read better as a plain red/green line than as confetti.
        static (int Common, int Total) CountWords(List<string> toks, bool[] keep)
        {
            int common = 0, total = 0;
            for (var i = 0; i < toks.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(toks[i])) continue;
                total++;
                if (keep[i]) common++;
            }
            return (common, total);
        }

        var (ca, ta) = CountWords(a, aKeep);
        var (_, tb) = CountWords(b, bKeep);
        if (ta + tb == 0 || 2.0 * ca / (ta + tb) < MinSimilarity)
            return (null, null);

        return (Merge(a, aKeep), Merge(b, bKeep));
    }

    private static List<DiffSegment> Merge(List<string> tokens, bool[] keep)
    {
        var segments = new List<DiffSegment>();
        var sb = new StringBuilder();
        var changed = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var tokenChanged = !keep[i];
            if (sb.Length > 0 && tokenChanged != changed)
            {
                segments.Add(new DiffSegment(sb.ToString(), changed));
                sb.Clear();
            }
            changed = tokenChanged;
            sb.Append(tokens[i]);
        }
        if (sb.Length > 0)
            segments.Add(new DiffSegment(sb.ToString(), changed));
        return segments;
    }

    /// <summary>Words, runs of whitespace, and single punctuation characters.</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }
            else
            {
                i++;
            }
            tokens.Add(text.Substring(start, i - start));
        }
        return tokens;
    }
}
