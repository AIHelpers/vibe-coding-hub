using AiCodeAgent.Core.Diffing;
using Xunit;

namespace AiCodeAgent.Core.Tests.Diffing;

public class SideBySideDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void IdenticalFiles_ProduceNoRows()
    {
        var result = SideBySideDiff.Build(Lines("a", "b"), Lines("a", "b"), "f.txt");

        Assert.Empty(result.Rows);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void ModifiedLine_SitsOnOneRow_OldOnTheLeft_NewOnTheRight()
    {
        var old = Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        var neu = Lines("a", "b", "c", "d", "E", "f", "g", "h", "i", "j");

        var result = SideBySideDiff.Build(old, neu, "f.txt");

        Assert.Equal(1, result.AddedLines);
        Assert.Equal(1, result.RemovedLines);
        var row = result.Rows.Single(r => r.OldKind == SideBySideCellKind.Removed);
        Assert.Equal("e", row.OldText);
        Assert.Equal("E", row.NewText);
        Assert.Equal(SideBySideCellKind.Added, row.NewKind);
        Assert.Equal(5, row.OldNumber);
        Assert.Equal(5, row.NewNumber);
    }

    [Fact]
    public void UnchangedLinesFarFromChanges_AreCollapsedIntoGaps_WithContextAround()
    {
        var old = Lines(Enumerable.Range(1, 30).Select(i => $"line{i}").ToArray());
        var neuLines = Enumerable.Range(1, 30).Select(i => i == 15 ? "CHANGED" : $"line{i}").ToArray();

        var result = SideBySideDiff.Build(old, Lines(neuLines), "f.txt");

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(12, hunk.OriginalStartLine);    // 3 lines of context before line 15
        Assert.Equal(7, hunk.OriginalLineCount);     // 3 + changed + 3
        var gaps = result.Rows.Where(r => r.Kind == SideBySideRowKind.Gap).ToList();
        Assert.Equal(2, gaps.Count);
        Assert.Equal(11, gaps[0].GapCount);          // lines 1-11
        Assert.Equal(12, gaps[1].GapCount);          // lines 19-30
        Assert.Equal(19, gaps[1].GapOldStart);
        Assert.Equal("@@ -12,7 +12,7 @@", result.Rows.Single(r => r.Kind == SideBySideRowKind.HunkHeader).Header);
    }

    [Fact]
    public void PureAdditionsAndRemovals_FaceAnEmptyCell()
    {
        var added = SideBySideDiff.Build(Lines("one", "two"), Lines("one", "NEW1", "NEW2", "two"), "f.txt");
        var addedRows = added.Rows.Where(r => r.NewKind == SideBySideCellKind.Added).ToList();
        Assert.Equal(2, addedRows.Count);
        Assert.All(addedRows, r =>
        {
            Assert.Equal(SideBySideCellKind.Empty, r.OldKind);
            Assert.Null(r.OldNumber);
        });

        var removed = SideBySideDiff.Build(Lines("one", "GONE", "two"), Lines("one", "two"), "f.txt");
        var row = Assert.Single(removed.Rows, r => r.OldKind == SideBySideCellKind.Removed);
        Assert.Equal(SideBySideCellKind.Empty, row.NewKind);
        Assert.Null(row.NewNumber);
    }

    [Fact]
    public void ModifiedRow_HighlightsOnlyTheChangedWord()
    {
        var result = SideBySideDiff.Build("var count = items.Length;\n", "var total = items.Length;\n", "f.cs");

        var row = Assert.Single(result.Rows, r => r.OldKind == SideBySideCellKind.Removed);
        Assert.Equal("count", Assert.Single(row.OldSegments!, s => s.Changed).Text);
        Assert.Equal("total", Assert.Single(row.NewSegments!, s => s.Changed).Text);
        Assert.Equal("var count = items.Length;", string.Concat(row.OldSegments!.Select(s => s.Text)));
    }

    [Fact]
    public void VeryDifferentLines_GetNoWordHighlight()
    {
        var (o, n) = WordDiff.Compute("completely different", "xyz 123 !!!");

        Assert.Null(o);
        Assert.Null(n);
    }

    [Fact]
    public void NewFile_IsOneAllAddedHunk_AndDeletedFileOneAllRemovedHunk()
    {
        var created = SideBySideDiff.Build(string.Empty, Lines("l1", "l2", "l3"), "new.txt");
        var hunk = Assert.Single(created.Hunks);
        Assert.Equal(3, created.AddedLines);
        Assert.Equal(0, hunk.OriginalLineCount);
        Assert.Equal(3, hunk.NewLineCount);
        Assert.Equal(1, hunk.NewStartLine);

        var deleted = SideBySideDiff.Build(Lines("l1", "l2"), string.Empty, "gone.txt");
        Assert.Equal(2, deleted.RemovedLines);
        Assert.Equal(0, Assert.Single(deleted.Hunks).NewLineCount);
    }

    [Fact]
    public void LineEndingsAloneDoNotCountAsChanges()
        => Assert.Empty(SideBySideDiff.Build("a\r\nb\r\n", "a\nb\n", "f.txt").Rows);

    [Fact]
    public void BothColumns_RebuildTheOriginalFiles_ForRandomEdits()
    {
        var rnd = new Random(42);
        for (var round = 0; round < 300; round++)
        {
            var oldLines = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => "v" + rnd.Next(8)).ToList();
            var newLines = new List<string>(oldLines);
            for (var e = 0; e < rnd.Next(0, 8); e++)
            {
                switch (rnd.Next(3))
                {
                    case 0 when newLines.Count > 0: newLines.RemoveAt(rnd.Next(newLines.Count)); break;
                    case 1: newLines.Insert(rnd.Next(newLines.Count + 1), "v" + rnd.Next(8)); break;
                    case 2 when newLines.Count > 0: newLines[rnd.Next(newLines.Count)] = "edited" + rnd.Next(5); break;
                }
            }

            var result = SideBySideDiff.Build(string.Join("\n", oldLines), string.Join("\n", newLines), "f.txt", rnd.Next(0, 5));

            var left = new List<string>();
            var right = new List<string>();
            foreach (var row in result.Rows)
            {
                if (row.Kind == SideBySideRowKind.Gap)
                {
                    left.AddRange(result.OldLines.Skip(row.GapOldStart - 1).Take(row.GapCount));
                    right.AddRange(result.NewLines.Skip(row.GapNewStart - 1).Take(row.GapCount));
                }
                else if (row.Kind == SideBySideRowKind.Line)
                {
                    if (row.OldNumber != null) left.Add(row.OldText);
                    if (row.NewNumber != null) right.Add(row.NewText);
                }
            }

            if (oldLines.SequenceEqual(newLines)) { Assert.Empty(result.Rows); continue; }
            Assert.Equal(oldLines, left);
            Assert.Equal(newLines, right);
        }
    }

    [Fact]
    public void RevertingEveryHunk_RestoresTheOldFile()
    {
        var oldLines = Enumerable.Range(1, 40).Select(i => $"line{i}").ToList();
        var newLines = oldLines.ToList();
        newLines[3] = "CHANGED 4";
        newLines.RemoveAt(20);
        newLines.Insert(30, "INSERTED");

        var result = SideBySideDiff.Build(string.Join("\n", oldLines), string.Join("\n", newLines), "f.txt");

        var restored = newLines.ToList();
        foreach (var h in result.Hunks.Reverse())
        {
            restored.RemoveRange(h.NewStartLine - 1, h.NewLineCount);
            restored.InsertRange(h.NewStartLine - 1, oldLines.Skip(h.OriginalStartLine - 1).Take(h.OriginalLineCount));
        }
        Assert.Equal(oldLines, restored);
    }

    [Fact]
    public void LargeFiles_StayFast_AndGiantAdditionsAreCapped()
    {
        var big = Enumerable.Range(0, 20_000).Select(i => $"    var v{i} = Compute({i});").ToArray();
        var edited = (string[])big.Clone();
        for (var i = 0; i < 200; i++) edited[i * 97 % edited.Length] += " // edited";

        var started = DateTime.UtcNow;
        var result = SideBySideDiff.Build(string.Join("\n", big), string.Join("\n", edited), "big.cs");

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.AddedLines);

        var huge = SideBySideDiff.Build(string.Empty, string.Join("\n", Enumerable.Range(0, 30_000).Select(i => "l" + i)), "huge.txt");
        Assert.True(huge.Truncated);
        Assert.True(huge.Rows.Count <= SideBySideDiff.MaxRows + 2);
    }
}
