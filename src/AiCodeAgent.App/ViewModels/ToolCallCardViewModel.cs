using System;
using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One card per tool call in the chat transcript.</summary>
public partial class ToolCallCardViewModel : ObservableObject
{
    /// <summary>The <see cref="ToolCall.Id"/> this card belongs to; the key used to update it when the result arrives.</summary>
    [ObservableProperty]
    private string _toolCallId = string.Empty;

    /// <summary>Raw tool name (e.g. "read_file"); shown as the tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    /// <summary>The first meaningful argument: the path for file tools, the command for shell tools.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _output;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(HasDuration))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private TimeSpan? _duration;

    [ObservableProperty]
    private bool _isError;

    /// <summary>True between the start event and the result.</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    private DateTime _timestamp = DateTime.UtcNow;

    [ObservableProperty]
    private bool _needsApproval;

    [ObservableProperty]
    private string? _approvalId;

    [ObservableProperty]
    private string? _agentId;

    [ObservableProperty]
    private string? _agentRole;

    /// <summary>"read_file" → "Read file".</summary>
    public string DisplayName => ToolCallCardFormatting.GetFriendlyName(ToolName);

    public string TitleText => $"🔧 {DisplayName}";

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public string DurationText => ToolCallCardFormatting.FormatDuration(Duration);

    public bool HasDuration => Duration.HasValue;

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    public string ExpandGlyph => IsExpanded ? "▼" : "▶";

    /// <summary>The header as one line: "🔧 Read file · src/Foo.cs · 0.0 s".</summary>
    public string SummaryText =>
        string.Join(" · ", new[] { TitleText, Subtitle, DurationText }.Where(s => !string.IsNullOrEmpty(s)));

    [RelayCommand]
    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

/// <summary>
/// Keeps exactly one <see cref="ToolCallCardViewModel"/> per tool call: the start event creates the card,
/// the end event updates the same card (matched by <see cref="ToolCall.Id"/> and agent), never adds a second one.
/// </summary>
public static class ToolCallCardList
{
    public const int MaxOutputLength = 500;

    public static ToolCallCardViewModel OnStarted(
        IList<ToolCallCardViewModel> cards, ToolCall call, string? agentId = null, string? agentRole = null)
    {
        var card = Find(cards, call, agentId, runningOnly: false);
        if (card == null)
        {
            card = new ToolCallCardViewModel { ToolCallId = call.Id, AgentId = agentId, AgentRole = agentRole };
            cards.Add(card);
        }

        Describe(card, call);
        card.IsError = false;
        card.IsRunning = true;
        return card;
    }

    public static ToolCallCardViewModel OnEnded(
        IList<ToolCallCardViewModel> cards, ToolCall call, ToolResult result, TimeSpan? duration,
        string? agentId = null, string? agentRole = null)
    {
        var card = Find(cards, call, agentId, runningOnly: true);
        if (card == null)
        {
            // A call can end without having started (denied, blocked by a hook, empty name): still show one card.
            card = new ToolCallCardViewModel { ToolCallId = call.Id, AgentId = agentId, AgentRole = agentRole };
            Describe(card, call);
            cards.Add(card);
        }

        card.Output = ToolCallCardFormatting.TruncateOutput(result.Content, MaxOutputLength);
        card.Duration = duration;
        card.IsError = result.IsError;
        card.IsRunning = false;
        return card;
    }

    private static void Describe(ToolCallCardViewModel card, ToolCall call)
    {
        card.ToolName = call.Name;
        card.Arguments = ToolCallCardFormatting.FormatArguments(call.Arguments);
        card.Subtitle = ToolCallCardFormatting.ExtractSubtitle(call.Arguments);
    }

    private static ToolCallCardViewModel? Find(
        IList<ToolCallCardViewModel> cards, ToolCall call, string? agentId, bool runningOnly)
    {
        if (!string.IsNullOrEmpty(call.Id))
        {
            return cards.LastOrDefault(c =>
                c.ToolCallId == call.Id && string.Equals(c.AgentId, agentId, StringComparison.Ordinal));
        }

        // No id to key on: only a still-running card with the same name and agent can be this call's card.
        return runningOnly
            ? cards.LastOrDefault(c =>
                c.IsRunning && string.IsNullOrEmpty(c.ToolCallId) && c.ToolName == call.Name &&
                string.Equals(c.AgentId, agentId, StringComparison.Ordinal))
            : null;
    }
}
