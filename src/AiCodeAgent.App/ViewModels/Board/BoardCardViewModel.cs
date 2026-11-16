using System;
using System.Collections.Generic;
using AiCodeAgent.Core.Board;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One card on the board as the view shows it: the card's data as text and flags, and the actions it offers.</summary>
public sealed partial class BoardCardViewModel : ObservableObject
{
    private readonly MissionControlViewModel _owner;

    public BoardCardViewModel(BoardCard card, MissionControlViewModel owner)
    {
        Card = card;
        _owner = owner;
    }

    public BoardCard Card { get; private set; }

    public string Id => Card.Id;
    public string Title => Card.Title;
    /// <summary>The full task, as a tooltip.</summary>
    public string Prompt => Card.Prompt;
    public BoardColumn Column => Card.Column;

    /// <summary>Replaces the card's data after the board changed it; every bound property is re-read.</summary>
    public void Update(BoardCard card)
    {
        Card = card;
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------------ the agent (E11-S2)

    /// <summary>The agents the card can be given (shared by all cards).</summary>
    public IReadOnlyList<AgentChoice> AgentChoices => _owner.AgentChoices;

    /// <summary>The picker on a card that is waiting in the Backlog. Once a card has run, the agent is shown as text.</summary>
    public bool CanChooseAgent => Card.Column == BoardColumn.Backlog && _owner.HasAgentChoices;

    /// <summary>The agent is named in text on cards that cannot be changed any more, unless it is the default one.</summary>
    public bool ShowAgentText => !CanChooseAgent && !Card.Agent.Equals(AgentTarget.Default);

    public string AgentText => "Agent: " + Card.Agent.DisplayName;

    public AgentChoice? SelectedAgent
    {
        get => _owner.ChoiceFor(Card.Agent);
        set
        {
            if (value != null && !value.Target.Equals(Card.Agent)) _owner.SetAgent(this, value);
        }
    }

    /// <summary>The list of agents changed: the picker reads it again.</summary>
    public void RefreshAgent()
    {
        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(CanChooseAgent));
        OnPropertyChanged(nameof(ShowAgentText));
    }

    // ------------------------------------------------------------------ what the card shows

    public string StatusText => Card.Column switch
    {
        BoardColumn.Backlog => Card.Runs > 0 ? "In the backlog again" : "Backlog",
        BoardColumn.Running => "● Running",
        BoardColumn.Done => "✓ Done",
        _ => Card.Outcome switch
        {
            CardOutcome.Completed => "✓ Ready for review",
            CardOutcome.NoChanges => "✓ Answered, no changes",
            CardOutcome.Stopped => "○ Stopped",
            CardOutcome.Interrupted => "⚠ Interrupted",
            CardOutcome.Failed => "✗ Failed",
            _ => "Needs review"
        }
    };

    public string StatusColor => Card.Column switch
    {
        BoardColumn.Running => "#4A9EFF",
        BoardColumn.Done => "#4CAF50",
        BoardColumn.NeedsReview => Card.Outcome switch
        {
            CardOutcome.Failed => "#E05555",
            CardOutcome.Interrupted => "#E0A030",
            CardOutcome.Stopped => "#9AA0A6",
            _ => "#4CAF50"
        },
        _ => "#9AA0A6"
    };

    public bool HasCost => Card.CostUsd > 0;
    public string CostText => $"${Card.CostUsd:0.00##}";
    public bool HasTokens => Card.Tokens > 0;
    public string TokensText => FormatTokens(Card.Tokens);
    public bool HasToolCalls => Card.ToolCalls > 0;
    public string ToolCallsText => Card.ToolCalls == 1 ? "1 tool call" : $"{Card.ToolCalls} tool calls";
    public bool HasRunNumber => Card.Runs > 1;
    public string RunText => $"run {Card.Runs}";

    public bool HasLastAction => !string.IsNullOrWhiteSpace(Card.LastAction) && Card.Column != BoardColumn.Backlog;
    public string LastActionText => Card.LastAction ?? string.Empty;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Card.Summary) && Card.Column is BoardColumn.NeedsReview or BoardColumn.Done;
    public string SummaryText => Card.Summary ?? string.Empty;
    public bool SummaryIsError => Card.Outcome is CardOutcome.Failed or CardOutcome.Interrupted;

    public bool HasBranch => !string.IsNullOrWhiteSpace(Card.Branch);
    public string BranchText => Card.Branch ?? string.Empty;
    public string BranchToolTip => string.IsNullOrWhiteSpace(Card.WorktreePath) ? BranchText : $"{BranchText}\n{Card.WorktreePath}";

    /// <summary>When the run started (running) or how long it took (ended).</summary>
    public string TimeText => Card switch
    {
        { StartedAt: null } => string.Empty,
        { FinishedAt: { } end, StartedAt: { } start } => "took " + FormatDuration(end - start),
        { StartedAt: { } start } => "started " + start.ToLocalTime().ToString("t"),
        _ => string.Empty
    };

    public bool HasTime => !string.IsNullOrEmpty(TimeText);

    // ------------------------------------------------------------------ what the card offers

    public bool CanStart => Card.Column == BoardColumn.Backlog && _owner.CanRun;
    public bool CanStop => Card.Column == BoardColumn.Running;
    public bool CanMarkDone => Card.Column == BoardColumn.NeedsReview;
    /// <summary>A card that failed, was stopped or was cut off: start it again at once.</summary>
    public bool CanRetry => Card.Column == BoardColumn.NeedsReview && Card.Outcome is CardOutcome.Failed or CardOutcome.Stopped or CardOutcome.Interrupted && _owner.CanRun;
    public bool CanReturn => Card.Column is BoardColumn.NeedsReview or BoardColumn.Done;
    public bool CanRemove => Card.Column != BoardColumn.Running;

    [RelayCommand] private void Start() => _owner.Start(this);
    [RelayCommand] private void Stop() => _owner.Stop(this);
    [RelayCommand] private void MarkDone() => _owner.MarkDone(this);
    [RelayCommand] private void Retry() => _owner.Retry(this);
    [RelayCommand] private void Return() => _owner.ReturnToBacklog(this);
    [RelayCommand] private void Remove() => _owner.Remove(this);

    // ------------------------------------------------------------------ formatting

    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min {span.Seconds} s";
        return $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    internal static string FormatTokens(long tokens) => tokens switch
    {
        < 1_000 => $"{tokens} tokens",
        < 1_000_000 => $"{tokens / 1_000.0:0.#}k tokens",
        _ => $"{tokens / 1_000_000.0:0.##}M tokens"
    };
}
