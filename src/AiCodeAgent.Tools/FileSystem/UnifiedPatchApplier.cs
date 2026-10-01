using System.Text.RegularExpressions;

namespace AiCodeAgent.Tools.FileSystem;

/// <summary>
/// Applies unified-diff hunks (<c>@@ -a,b +c,d @@</c> with ' ', '-', '+' lines) to a single file's text.
/// Line numbers are only a hint: each hunk is located by matching its context, searching outward from the expected
/// position, so patches produced from slightly stale views still apply. Whitespace at line ends is ignored when matching.
/// </summary>
public static class UnifiedPatchApplier
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    private sealed record Hunk(int OldStart, List<string> Old, List<string> New);

    /// <summary>Returns the patched text, or null with an <paramref name="error"/> when a hunk cannot be placed.</summary>
    public static string? Apply(string original, string patch, out string error)
    {
        error = string.Empty;
        var crlf = original.Contains("\r\n", StringComparison.Ordinal);
        var endsWithNewline = original.EndsWith('\n');
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var hunks = Parse(patch, out var parseError);
        if (hunks == null) { error = parseError; return null; }
        if (hunks.Count == 0) { error = "The patch contains no hunks (expected lines starting with '@@ -a,b +c,d @@')."; return null; }

        var offset = 0;
        for (var h = 0; h < hunks.Count; h++)
        {
            var hunk = hunks[h];
            var expected = Math.Max(0, hunk.OldStart - 1 + offset);
            var at = Locate(lines, hunk.Old, expected);
            if (at < 0)
            {
                error = $"Hunk {h + 1} does not match the file (context or removed lines differ). Re-read the file and regenerate the patch.";
                return null;
            }
            lines.RemoveRange(at, hunk.Old.Count);
            lines.InsertRange(at, hunk.New);
            offset += hunk.New.Count - hunk.Old.Count;
        }

        var result = string.Join(crlf ? "\r\n" : "\n", lines);
        if (endsWithNewline || original.Length == 0) result += crlf ? "\r\n" : "\n";
        return result;
    }

    private static List<Hunk>? Parse(string patch, out string error)
    {
        error = string.Empty;
        var hunks = new List<Hunk>();
        Hunk? current = null;
        List<string>? oldLines = null, newLines = null;

        void Flush()
        {
            if (current != null) hunks.Add(new Hunk(current.OldStart, oldLines!, newLines!));
            current = null;
        }

        foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            var m = HunkHeader.Match(raw);
            if (m.Success)
            {
                Flush();
                oldLines = new List<string>();
                newLines = new List<string>();
                current = new Hunk(int.Parse(m.Groups[1].Value), oldLines, newLines);
                continue;
            }
            if (current == null) continue; // file headers (---/+++/diff/index) before the first hunk
            if (raw.StartsWith("\\", StringComparison.Ordinal)) continue; // "\ No newline at end of file"

            if (raw.StartsWith('+')) newLines!.Add(raw[1..]);
            else if (raw.StartsWith('-')) oldLines!.Add(raw[1..]);
            else if (raw.StartsWith(' ')) { oldLines!.Add(raw[1..]); newLines!.Add(raw[1..]); }
            else if (raw.Length == 0) { oldLines!.Add(string.Empty); newLines!.Add(string.Empty); } // blank context line whose leading space was stripped
            else { error = $"Unexpected line in hunk: '{raw}'. Hunk lines must start with ' ', '-' or '+'."; return null; }
        }
        Flush();

        // A trailing blank line after the last hunk is usually just the patch's final newline, not context.
        if (hunks.Count > 0)
        {
            var last = hunks[^1];
            if (last.Old.Count > 0 && last.New.Count > 0 && last.Old[^1].Length == 0 && last.New[^1].Length == 0)
            {
                last.Old.RemoveAt(last.Old.Count - 1);
                last.New.RemoveAt(last.New.Count - 1);
            }
        }
        return hunks;
    }

    private static int Locate(List<string> lines, List<string> old, int expected)
    {
        if (old.Count == 0) return Math.Min(expected, lines.Count);
        var max = lines.Count - old.Count;
        if (max < 0) return -1;
        expected = Math.Min(expected, max);
        for (var d = 0; d <= Math.Max(expected, max - expected); d++)
        {
            if (expected - d >= 0 && Matches(lines, old, expected - d)) return expected - d;
            if (d > 0 && expected + d <= max && Matches(lines, old, expected + d)) return expected + d;
        }
        return -1;
    }

    private static bool Matches(List<string> lines, List<string> old, int at)
    {
        for (var i = 0; i < old.Count; i++)
            if (!string.Equals(lines[at + i].TrimEnd(), old[i].TrimEnd(), StringComparison.Ordinal)) return false;
        return true;
    }
}
