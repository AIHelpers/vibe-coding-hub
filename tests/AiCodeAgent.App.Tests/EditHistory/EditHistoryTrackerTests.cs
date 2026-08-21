using System;
using System.Linq;
using AiCodeAgent.App.EditHistory;

namespace AiCodeAgent.App.Tests.EditHistory;

public class EditHistoryTrackerTests
{
    private static EditRecord Rec(string file, int line, string after, DateTimeOffset ts) =>
        new() { FilePath = file, StartLine = line, After = after, Timestamp = ts };

    [Fact]
    public void Record_AddsToHistory()
    {
        var tracker = new EditHistoryTracker();
        var ts = DateTimeOffset.UtcNow;

        tracker.Record(Rec("a.cs", 1, "x", ts));

        var history = tracker.GetHistory("a.cs");
        Assert.Single(history);
        Assert.Equal("x", history[0].After);
    }

    [Fact]
    public void GetHistory_ReturnsOldestFirst()
    {
        var tracker = new EditHistoryTracker();
        var baseTs = DateTimeOffset.UtcNow;

        tracker.Record(Rec("a.cs", 1, "first", baseTs));
        tracker.Record(Rec("a.cs", 2, "second", baseTs.AddSeconds(1)));
        tracker.Record(Rec("a.cs", 3, "third", baseTs.AddSeconds(2)));

        var history = tracker.GetHistory("a.cs");
        Assert.Equal(new[] { "first", "second", "third" }, history.Select(r => r.After).ToArray());
    }

    [Fact]
    public void Record_EvictsOldestWhenAtCapacity()
    {
        var tracker = new EditHistoryTracker(capacity: 3);
        var baseTs = DateTimeOffset.UtcNow;

        tracker.Record(Rec("a.cs", 1, "1", baseTs));
        tracker.Record(Rec("a.cs", 2, "2", baseTs.AddSeconds(1)));
        tracker.Record(Rec("a.cs", 3, "3", baseTs.AddSeconds(2)));
        tracker.Record(Rec("a.cs", 4, "4", baseTs.AddSeconds(3)));

        var history = tracker.GetHistory("a.cs");
        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { "2", "3", "4" }, history.Select(r => r.After).ToArray());
    }

    [Fact]
    public void GetHistory_UnknownFile_ReturnsEmpty()
    {
        var tracker = new EditHistoryTracker();
        Assert.Empty(tracker.GetHistory("none.cs"));
    }

    [Fact]
    public void GetHistory_NullOrEmptyFile_ReturnsEmpty()
    {
        var tracker = new EditHistoryTracker();
        Assert.Empty(tracker.GetHistory(""));
        Assert.Empty(tracker.GetHistory(null!));
    }

    [Fact]
    public void Record_NullOrEmptyFilePath_IsIgnored()
    {
        var tracker = new EditHistoryTracker();
        tracker.Record(Rec("", 1, "x", DateTimeOffset.UtcNow));
        tracker.Record(Rec(null!, 1, "x", DateTimeOffset.UtcNow));

        Assert.Empty(tracker.GetHistory(""));
    }

    [Fact]
    public void GetRecent_ReturnsMostRecentAcrossFiles()
    {
        var tracker = new EditHistoryTracker();
        var baseTs = DateTimeOffset.UtcNow;

        tracker.Record(Rec("a.cs", 1, "a1", baseTs));
        tracker.Record(Rec("b.cs", 1, "b1", baseTs.AddSeconds(2)));
        tracker.Record(Rec("a.cs", 2, "a2", baseTs.AddSeconds(1)));

        var recent = tracker.GetRecent(2);
        Assert.Equal(new[] { "b1", "a2" }, recent.Select(r => r.After).ToArray());
    }

    [Fact]
    public void GetRecent_RespectsCount()
    {
        var tracker = new EditHistoryTracker();
        var baseTs = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            tracker.Record(Rec("a.cs", i, i.ToString(), baseTs.AddSeconds(i)));

        Assert.Equal(3, tracker.GetRecent(3).Count);
        Assert.Empty(tracker.GetRecent(0));
    }

    [Fact]
    public void Clear_RemovesFileHistory()
    {
        var tracker = new EditHistoryTracker();
        tracker.Record(Rec("a.cs", 1, "x", DateTimeOffset.UtcNow));
        tracker.Record(Rec("b.cs", 1, "y", DateTimeOffset.UtcNow));

        tracker.Clear("a.cs");

        Assert.Empty(tracker.GetHistory("a.cs"));
        Assert.Single(tracker.GetHistory("b.cs"));
    }

    [Fact]
    public void Clear_WithNull_DoesNothing()
    {
        var tracker = new EditHistoryTracker();
        tracker.Record(Rec("a.cs", 1, "x", DateTimeOffset.UtcNow));

        tracker.Clear(null!);
        Assert.Single(tracker.GetHistory("a.cs"));
    }

    [Fact]
    public void ClearAll_RemovesEverything()
    {
        var tracker = new EditHistoryTracker();
        tracker.Record(Rec("a.cs", 1, "x", DateTimeOffset.UtcNow));
        tracker.Record(Rec("b.cs", 1, "y", DateTimeOffset.UtcNow));

        tracker.ClearAll();

        Assert.Empty(tracker.GetHistory("a.cs"));
        Assert.Empty(tracker.GetHistory("b.cs"));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditHistoryTracker(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditHistoryTracker(-1));
    }

    [Fact]
    public void History_IsIsolatedPerFile()
    {
        var tracker = new EditHistoryTracker();
        tracker.Record(Rec("a.cs", 1, "a", DateTimeOffset.UtcNow));
        tracker.Record(Rec("b.cs", 1, "b", DateTimeOffset.UtcNow));

        Assert.Single(tracker.GetHistory("a.cs"));
        Assert.Single(tracker.GetHistory("b.cs"));
    }
}