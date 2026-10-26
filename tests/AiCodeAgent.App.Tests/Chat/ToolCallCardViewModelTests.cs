using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.Tests.Chat;

public class ToolCallCardViewModelTests
{
    private static ToolCall Call(string name, params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (k, v) in args) dict[k] = v;
        return new ToolCall { Name = name, Arguments = dict };
    }

    private static ToolResult Ok(string content = "ok") => new() { Content = content };
    private static ToolResult Fail(string content) => new() { Content = content, IsError = true };

    // ---- DurationText ----

    [Theory]
    [InlineData(0.0094395, "0.0 s")]
    [InlineData(0.3, "0.3 s")]
    [InlineData(0.99, "0.9 s")]
    [InlineData(11.8, "11.8 s")]
    [InlineData(59.96, "59.9 s")]
    [InlineData(60, "1 m 00 s")]
    [InlineData(65, "1 m 05 s")]
    [InlineData(754, "12 m 34 s")]
    [InlineData(3725, "62 m 05 s")]
    public void DurationText_IsReadable(double seconds, string expected)
    {
        var card = new ToolCallCardViewModel { Duration = TimeSpan.FromSeconds(seconds) };
        Assert.Equal(expected, card.DurationText);
        Assert.True(card.HasDuration);
    }

    [Fact]
    public void DurationText_IsEmpty_WhileNoDuration()
    {
        var card = new ToolCallCardViewModel();
        Assert.Equal(string.Empty, card.DurationText);
        Assert.False(card.HasDuration);
    }

    [Fact]
    public void DurationText_ClampsNegativeToZero()
    {
        Assert.Equal("0.0 s", ToolCallCardFormatting.FormatDuration(TimeSpan.FromSeconds(-2)));
    }

    [Fact]
    public void DurationText_IgnoresCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("11.8 s", ToolCallCardFormatting.FormatDuration(TimeSpan.FromSeconds(11.8)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- Label map ----

    [Theory]
    [InlineData("read_file", "Read file")]
    [InlineData("edit_file", "Edit file")]
    [InlineData("verify_changes", "Verify (build + test)")]
    [InlineData("execute_command", "Run command")]
    [InlineData("some_new_tool", "Some new tool")]
    [InlineData("", "Tool")]
    public void DisplayName_UsesFriendlyLabel(string toolName, string expected)
    {
        var card = new ToolCallCardViewModel { ToolName = toolName };
        Assert.Equal(expected, card.DisplayName);
        Assert.Equal($"🔧 {expected}", card.TitleText);
    }

    [Fact]
    public void ToolName_ChangeRaisesDisplayNameChanged()
    {
        var card = new ToolCallCardViewModel();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.ToolName = "read_file";

        Assert.Contains(nameof(ToolCallCardViewModel.DisplayName), raised);
        Assert.Contains(nameof(ToolCallCardViewModel.TitleText), raised);
    }

    // ---- Subtitle ----

    [Fact]
    public void Subtitle_PrefersPath_ThenCommand()
    {
        Assert.Equal("src/A.cs", ToolCallCardFormatting.ExtractSubtitle(
            new Dictionary<string, object?> { ["content"] = "x", ["path"] = "src/A.cs" }));
        Assert.Equal("dotnet build", ToolCallCardFormatting.ExtractSubtitle(
            new Dictionary<string, object?> { ["timeout"] = 30, ["command"] = "dotnet build" }));
        Assert.Equal(string.Empty, ToolCallCardFormatting.ExtractSubtitle(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Subtitle_IsOneShortLine()
    {
        var longCommand = "echo one\necho two " + new string('x', 200);
        var subtitle = ToolCallCardFormatting.ExtractSubtitle(new Dictionary<string, object?> { ["command"] = longCommand });

        Assert.DoesNotContain('\n', subtitle);
        Assert.Equal(ToolCallCardFormatting.SubtitleMaxLength, subtitle.Length);
        Assert.EndsWith("…", subtitle);
    }

    // ---- Dedup by tool-call id ----

    [Fact]
    public void SuccessfulReadFile_ReadsAsOneLine_WithoutFalse()
    {
        var cards = new ObservableCollection<ToolCallCardViewModel>();
        var call = Call("read_file", ("path", "src/Todo.Core/TodoService.cs"));

        ToolCallCardList.OnStarted(cards, call);
        var card = ToolCallCardList.OnEnded(cards, call, Ok("class TodoService {}"), TimeSpan.FromTicks(94395));

        Assert.Single(cards);
        Assert.Equal("🔧 Read file · src/Todo.Core/TodoService.cs · 0.0 s", card.SummaryText);
        Assert.False(card.IsRunning);
        Assert.False(card.IsError);
        Assert.Equal("read_file", card.ToolName);
        Assert.Equal(call.Id, card.ToolCallId);
    }

    [Fact]
    public void Start_MarksCardRunning()
    {
        var cards = new List<ToolCallCardViewModel>();
        var card = ToolCallCardList.OnStarted(cards, Call("execute_command", ("command", "dotnet test")));

        Assert.True(card.IsRunning);
        Assert.Equal("dotnet test", card.Subtitle);
        Assert.Equal(string.Empty, card.DurationText);
    }

    [Fact]
    public void FailedCall_IsMarkedError_WithErrorText()
    {
        var cards = new List<ToolCallCardViewModel>();
        var call = Call("execute_command", ("command", "dotnet build"));

        ToolCallCardList.OnStarted(cards, call);
        var card = ToolCallCardList.OnEnded(cards, call, Fail("error CS1002: ; expected"), TimeSpan.FromSeconds(11.8));

        Assert.Single(cards);
        Assert.True(card.IsError);
        Assert.False(card.IsRunning);
        Assert.Equal("error CS1002: ; expected", card.Output);
        Assert.Equal("11.8 s", card.DurationText);
    }

    [Fact]
    public void TwoCallsOfTheSameTool_GetTheirOwnResults()
    {
        var cards = new List<ToolCallCardViewModel>();
        var first = Call("read_file", ("path", "a.cs"));
        var second = Call("read_file", ("path", "b.cs"));

        ToolCallCardList.OnStarted(cards, first);
        ToolCallCardList.OnStarted(cards, second);
        ToolCallCardList.OnEnded(cards, second, Ok("B"), TimeSpan.FromSeconds(1));
        ToolCallCardList.OnEnded(cards, first, Ok("A"), TimeSpan.FromSeconds(2));

        Assert.Equal(2, cards.Count);
        Assert.Equal("A", cards[0].Output);
        Assert.Equal("a.cs", cards[0].Subtitle);
        Assert.Equal("B", cards[1].Output);
        Assert.Equal("b.cs", cards[1].Subtitle);
        Assert.All(cards, c => Assert.False(c.IsRunning));
    }

    [Fact]
    public void RepeatedStartForSameId_DoesNotAddASecondCard()
    {
        var cards = new List<ToolCallCardViewModel>();
        var call = Call("edit_file", ("path", "a.cs"));

        ToolCallCardList.OnStarted(cards, call);
        ToolCallCardList.OnStarted(cards, call);
        ToolCallCardList.OnEnded(cards, call, Ok(), TimeSpan.FromSeconds(0.3));

        var card = Assert.Single(cards);
        Assert.Equal("0.3 s", card.DurationText);
    }

    [Fact]
    public void EndWithoutStart_StillShowsExactlyOneCard()
    {
        // A denied call ends without a start event.
        var cards = new List<ToolCallCardViewModel>();
        var call = Call("execute_command", ("command", "rm -rf /"));

        ToolCallCardList.OnEnded(cards, call, Fail("Denied by user"), TimeSpan.Zero);

        var card = Assert.Single(cards);
        Assert.Equal("Run command", card.DisplayName);
        Assert.True(card.IsError);
        Assert.False(card.IsRunning);
    }

    [Fact]
    public void SameIdFromDifferentAgents_AreSeparateCards()
    {
        var cards = new List<ToolCallCardViewModel>();
        var call = Call("read_file", ("path", "a.cs"));

        ToolCallCardList.OnStarted(cards, call, "agent-1", "coder");
        ToolCallCardList.OnStarted(cards, call, "agent-2", "reviewer");
        ToolCallCardList.OnEnded(cards, call, Ok("from 2"), TimeSpan.FromSeconds(1), "agent-2", "reviewer");

        Assert.Equal(2, cards.Count);
        Assert.True(cards[0].IsRunning);
        Assert.Equal("from 2", cards[1].Output);
        Assert.Equal("reviewer", cards[1].AgentRole);
    }

    [Fact]
    public void CallWithoutId_MatchesTheRunningCardByName()
    {
        var cards = new List<ToolCallCardViewModel>();
        var call = new ToolCall { Id = string.Empty, Name = "grep", Arguments = new() { ["pattern"] = "Todo" } };

        ToolCallCardList.OnStarted(cards, call);
        ToolCallCardList.OnEnded(cards, call, Ok("3 matches"), TimeSpan.FromSeconds(0.2));

        var card = Assert.Single(cards);
        Assert.Equal("3 matches", card.Output);
        Assert.False(card.IsRunning);
    }

    [Fact]
    public void LongOutput_IsTruncated()
    {
        var cards = new List<ToolCallCardViewModel>();
        var call = Call("read_file", ("path", "big.cs"));

        var card = ToolCallCardList.OnEnded(cards, call, Ok(new string('x', 2000)), TimeSpan.FromSeconds(1));

        Assert.StartsWith(new string('x', ToolCallCardList.MaxOutputLength), card.Output);
        Assert.EndsWith("...[truncated]", card.Output);
    }

    [Fact]
    public void ToggleExpand_FlipsStateAndGlyph()
    {
        var card = new ToolCallCardViewModel();
        Assert.Equal("▶", card.ExpandGlyph);

        card.ToggleExpandCommand.Execute(null);

        Assert.True(card.IsExpanded);
        Assert.Equal("▼", card.ExpandGlyph);
    }
}
