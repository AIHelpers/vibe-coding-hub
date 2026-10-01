using System;
using System.Collections.Generic;
using AiCodeAgent.App.ViewModels;
using Xunit;

namespace AiCodeAgent.App.Tests.Chat;

public class AgentActivityTrackerTests
{
    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static (AgentActivityTracker t, FakeClock c) Create()
    {
        var c = new FakeClock();
        return (new AgentActivityTracker(() => c.Now), c);
    }

    [Fact]
    public void Begin_ShowsThinking_AndCountsElapsedTime()
    {
        var (t, c) = Create();
        t.Begin();

        Assert.True(t.IsActive);
        Assert.Equal(AgentActivityTracker.ThinkingText, t.CurrentActivity);
        Assert.Equal("0s", t.ElapsedText);

        c.Advance(65);
        t.Tick();
        Assert.Equal("1m 05s", t.ElapsedText);
    }

    [Fact]
    public void ToolLifecycle_ShowsDescription_ThenRecordsFinishedStep()
    {
        var (t, c) = Create();
        t.Begin();

        t.ToolStarted("1", "read_file", new Dictionary<string, object?> { ["path"] = @"C:\proj\src\Foo.cs" });
        Assert.Equal("Reading Foo.cs", t.CurrentActivity);

        c.Advance(2.4);
        t.ToolFinished("1", "read_file", isError: false);

        Assert.Equal(AgentActivityTracker.ThinkingText, t.CurrentActivity);
        Assert.Single(t.RecentSteps);
        Assert.Equal("✓ Reading Foo.cs · 2.4s", t.RecentSteps[0]);
        Assert.True(t.HasRecentSteps);
    }

    [Fact]
    public void FailedTool_IsMarkedWithCross()
    {
        var (t, c) = Create();
        t.Begin();
        t.ToolStarted("1", "execute_command", new Dictionary<string, object?> { ["command"] = "dotnet build" });
        c.Advance(12);
        t.ToolFinished("1", "execute_command", isError: true);

        Assert.Equal("✗ Running: dotnet build · 12s", t.RecentSteps[0]);
    }

    [Fact]
    public void ParallelTools_ShowLatestAndCount()
    {
        var (t, _) = Create();
        t.Begin();
        t.ToolStarted("1", "grep", new Dictionary<string, object?> { ["pattern"] = "TODO" });
        t.ToolStarted("2", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" });

        Assert.Equal("Reading a.txt (+1 more)", t.CurrentActivity);

        t.ToolFinished("2", "read_file", false);
        Assert.Equal("Searching for “TODO”", t.CurrentActivity);
    }

    [Fact]
    public void Phase_IsIgnoredWhileToolRunningOrBlocked()
    {
        var (t, _) = Create();
        t.Begin();
        t.ToolStarted("1", "glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" });
        t.SetPhase("Writing response…");
        Assert.Equal("Finding files: **/*.cs", t.CurrentActivity);

        t.ToolFinished("1", "glob", false);
        t.SetBlocking("⏸ Waiting for your approval: Editing X.cs");
        t.SetPhase("Writing response…");
        Assert.StartsWith("⏸ Waiting", t.CurrentActivity);

        t.ClearBlocking();
        t.SetPhase("Writing response…");
        Assert.Equal("Writing response…", t.CurrentActivity);
    }

    [Fact]
    public void StepTimer_ResetsOnNewStep_WhileTotalKeepsCounting()
    {
        var (t, c) = Create();
        t.Begin();
        c.Advance(30);
        t.ToolStarted("1", "git", new Dictionary<string, object?> { ["command"] = "status" });
        c.Advance(5);
        t.Tick();

        Assert.Equal("35s", t.ElapsedText);
        Assert.Equal("5s", t.StepElapsedText);
    }

    [Fact]
    public void End_ClearsAndDeactivates_AndBeginResetsHistory()
    {
        var (t, _) = Create();
        t.Begin();
        t.ToolStarted("1", "read_file", null);
        t.ToolFinished("1", "read_file", false);
        t.End();

        Assert.False(t.IsActive);
        Assert.Equal(string.Empty, t.CurrentActivity);

        t.Begin();
        Assert.Empty(t.RecentSteps);
        Assert.False(t.HasRecentSteps);
    }

    [Fact]
    public void RecentSteps_AreCapped_NewestFirst()
    {
        var (t, _) = Create();
        t.Begin();
        for (var i = 0; i < 10; i++)
        {
            t.ToolStarted(i.ToString(), "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.txt" });
            t.ToolFinished(i.ToString(), "read_file", false);
        }

        Assert.Equal(AgentActivityTracker.MaxRecentSteps, t.RecentSteps.Count);
        Assert.StartsWith("✓ Reading f9.txt", t.RecentSteps[0]);
    }

    [Fact]
    public void Updates_AreIgnoredWhenNotActive()
    {
        var (t, _) = Create();
        t.ToolStarted("1", "read_file", null);
        t.SetPhase("x");
        Assert.False(t.IsActive);
        Assert.Equal(string.Empty, t.CurrentActivity);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 00s")]
    [InlineData(3725, "1h 02m")]
    public void FormatElapsed_Works(int seconds, string expected)
        => Assert.Equal(expected, AgentActivityTracker.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DescribeTool_HandlesMissingArgsAndUnknownTools()
    {
        Assert.Equal("Reading a file", AgentActivityTracker.DescribeTool("read_file", null));
        Assert.Equal("Running my_tool", AgentActivityTracker.DescribeTool("my_tool", new Dictionary<string, object?>()));
        Assert.Equal("Waiting for your answer", AgentActivityTracker.DescribeTool("ask_user", null));
        var longCmd = new string('x', 200);
        var text = AgentActivityTracker.DescribeTool("execute_command", new Dictionary<string, object?> { ["command"] = longCmd });
        Assert.True(text.Length < 80);
    }
}
