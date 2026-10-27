using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Timeline items. The view renders them with one ItemsControl and a DataTemplate per type.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One entry of the chat timeline, in the order it happened.</summary>
public abstract partial class ChatItem : ObservableObject
{
    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;
}

/// <summary>An item whose body is markdown text (user, assistant, system notice).</summary>
public abstract partial class ChatTextItem : ChatItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    private string _content = string.Empty;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    /// <summary>Header label, e.g. "You", "Assistant", "coder".</summary>
    public abstract string Label { get; }
}

public sealed partial class UserMessageItem : ChatTextItem
{
    /// <summary>
    /// The number <c>/rewind N</c> and <c>/fork N</c> use for this message (1-based among the session's user messages),
    /// or null when unknown (messages typed in this view since the transcript was last loaded).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRewindNumber))]
    [NotifyPropertyChangedFor(nameof(RewindLabel))]
    private int? _rewindNumber;

    public bool HasRewindNumber => RewindNumber.HasValue;

    public string RewindLabel => RewindNumber is { } n ? $"#{n}" : string.Empty;

    public override string Label => "You";
}

public sealed partial class AssistantTextItem : ChatTextItem
{
    /// <summary>True while this bubble still receives streamed text.</summary>
    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string? _agentId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string? _agentRole;

    /// <summary>The run that streamed this bubble (null for one-off notes).</summary>
    internal AssistantRun? Run { get; init; }

    public override string Label => AgentRole ?? AgentId ?? "Assistant";
}

/// <summary>Informational entry from the app itself (retrieved context, requirements summary, …).</summary>
public sealed partial class SystemNoticeItem : ChatTextItem
{
    public override string Label => "System";
}

/// <summary>A tool call, rendered with <see cref="Views.ToolCallCardView"/>.</summary>
public sealed partial class ToolCallItem : ChatItem
{
    public ToolCallItem(ToolCallCardViewModel card)
    {
        Card = card;
        Timestamp = card.Timestamp.ToLocalTime();
    }

    public ToolCallCardViewModel Card { get; }
}

public enum ApprovalStatus { Pending, Approved, Declined }

/// <summary>An approval (or confirmation) the agent asked for, and how it was answered.</summary>
public sealed partial class ApprovalItem : ChatItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _toolName = string.Empty;

    /// <summary>Path / command shown after the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _risk = string.Empty;

    [ObservableProperty]
    private string? _agentRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsApproved))]
    [NotifyPropertyChangedFor(nameof(IsDeclined))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private ApprovalStatus _status = ApprovalStatus.Pending;

    public string Title => $"🔔 Approval required · {ToolCallCardFormatting.GetFriendlyName(ToolName)}";
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool IsPending => Status == ApprovalStatus.Pending;
    public bool IsApproved => Status == ApprovalStatus.Approved;
    public bool IsDeclined => Status == ApprovalStatus.Declined;

    /// <summary>Why it was resolved this way, e.g. "edits allowed for this task" or "feedback: use the helper".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _resolutionNote;

    public string StatusText => (Status switch
    {
        ApprovalStatus.Approved => "✓ Approved",
        ApprovalStatus.Declined => "✕ Declined",
        _ => "Waiting for you: approve or decline in the dialog below."
    }) + (string.IsNullOrEmpty(ResolutionNote) || Status == ApprovalStatus.Pending ? string.Empty : $" · {ResolutionNote}");

    /// <summary>Records the answer; only the first answer counts.</summary>
    public void Resolve(bool approved, string? note = null)
    {
        if (Status != ApprovalStatus.Pending) return;
        ResolutionNote = note;
        Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Declined;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The timeline itself.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The chat as one chronological list: user message → assistant text (step 1) → tool card → approval →
/// tool card → assistant text (step 2) → … → final answer. All mutations happen on the UI thread.
/// </summary>
public sealed class ChatTimeline
{
    public const string NoResponseText = "*(No response generated)*";

    private readonly List<ToolCallCardViewModel> _toolCards = new();
    private AssistantTextItem? _openBubble;

    public ObservableCollection<ChatItem> Items { get; } = new();

    /// <summary>Follow-the-bottom state for the view.</summary>
    public ChatAutoScroll AutoScroll { get; } = new();

    /// <summary>All tool cards in the timeline (one per tool call).</summary>
    public IReadOnlyList<ToolCallCardViewModel> ToolCards => _toolCards;

    /// <summary>True while a bubble is receiving streamed text.</summary>
    public bool IsStreaming => _openBubble != null;

    // ── Text items ────────────────────────────────────────────────────────

    /// <summary>Adds a finished message, mapping its role to the matching item type ("User", "System", else assistant).</summary>
    public ChatTextItem Add(ChatMessage message)
    {
        ChatTextItem item = message.Role switch
        {
            "User" => new UserMessageItem(),
            "System" => new SystemNoticeItem(),
            _ => new AssistantTextItem()
        };
        item.Content = message.Content;
        item.Timestamp = message.Timestamp;
        Append(item);
        return item;
    }

    public UserMessageItem AddUser(string content, int? rewindNumber = null)
    {
        var item = new UserMessageItem { Content = content, RewindNumber = rewindNumber };
        Append(item);
        return item;
    }

    public SystemNoticeItem AddNotice(string content)
    {
        var item = new SystemNoticeItem { Content = content };
        Append(item);
        return item;
    }

    // ── Runs (one agent turn, possibly many steps) ───────────────────────────

    public AssistantRun BeginRun() => new(this);

    internal void AppendRunText(AssistantRun run, string text, string? agentId, string? agentRole, bool anyAgent)
    {
        if (string.IsNullOrEmpty(text)) return;

        var open = _openBubble;
        var canContinue = open != null
                          && ReferenceEquals(open.Run, run)
                          && Items.Count > 0 && ReferenceEquals(Items[^1], open)
                          && (anyAgent || string.Equals(open.AgentId, agentId, StringComparison.Ordinal));
        if (canContinue)
        {
            open!.Content += text;
            AutoScroll.OnContentChanged(isStreaming: true);
            return;
        }

        // A new step (after a tool card, an approval, another agent's text, …) gets its own bubble.
        var start = text.TrimStart('\r', '\n');
        if (start.Length == 0) return;

        var bubble = new AssistantTextItem
        {
            Run = run,
            Content = start,
            AgentId = anyAgent ? open?.AgentId : agentId,
            AgentRole = anyAgent ? open?.AgentRole : agentRole,
            IsStreaming = true
        };
        Append(bubble);
        _openBubble = bubble;
        run.Bubbles.Add(bubble);
    }

    internal void CompleteRun(AssistantRun run)
    {
        if (_openBubble != null && ReferenceEquals(_openBubble.Run, run)) CloseOpenBubble();
        if (!run.HasText)
        {
            Append(new AssistantTextItem { Content = NoResponseText });
        }
    }

    // ── Tool calls and approvals ─────────────────────────────────────────

    public ToolCallCardViewModel ToolStarted(ToolCall call, string? agentId = null, string? agentRole = null)
    {
        var count = _toolCards.Count;
        var card = ToolCallCardList.OnStarted(_toolCards, call, agentId, agentRole);
        if (_toolCards.Count != count) Append(new ToolCallItem(card));
        return card;
    }

    public ToolCallCardViewModel ToolEnded(
        ToolCall call, ToolResult result, TimeSpan? duration, string? agentId = null, string? agentRole = null)
    {
        var count = _toolCards.Count;
        var card = ToolCallCardList.OnEnded(_toolCards, call, result, duration, agentId, agentRole);
        if (_toolCards.Count != count) Append(new ToolCallItem(card));
        return card;
    }

    public ApprovalItem AddApproval(ToolCall call, string risk, string? agentRole = null) =>
        AddApproval(call.Name, ToolCallCardFormatting.ExtractSubtitle(call.Arguments),
            ToolCallCardFormatting.FormatArguments(call.Arguments), risk, agentRole);

    public ApprovalItem AddApproval(string toolName, string subtitle, string arguments, string risk, string? agentRole = null)
    {
        var item = new ApprovalItem
        {
            ToolName = toolName,
            Subtitle = subtitle,
            Arguments = arguments,
            Risk = risk,
            AgentRole = agentRole
        };
        Append(item);
        return item;
    }

    // ── Whole-timeline operations ─────────────────────────────────────────

    /// <summary>Empties the timeline; with <paramref name="keepWelcome"/> the first assistant item survives.</summary>
    public void Clear(bool keepWelcome = false)
    {
        var welcome = keepWelcome ? Items.FirstOrDefault() as AssistantTextItem : null;
        _openBubble = null;
        _toolCards.Clear();
        Items.Clear();
        AutoScroll.Reset();
        if (welcome != null) Items.Add(welcome);
    }

    /// <summary>
    /// Rebuilds the timeline from a stored session transcript (the JSONL session store, unchanged schema):
    /// user messages, assistant text and tool calls with their results, in stored order. User messages get the
    /// same numbers <see cref="Core.Sessions.ConversationRewinder.ListPointsAsync"/> gives them.
    /// </summary>
    public void LoadTranscript(IEnumerable<Message> transcript, bool keepWelcome = true)
    {
        Clear(keepWelcome);

        var userNumber = 0;
        var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        foreach (var m in transcript)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    userNumber++; // counted even when blank, exactly like the rewinder
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        Append(new UserMessageItem { Content = m.Content, Timestamp = m.Timestamp.ToLocalTime(), RewindNumber = userNumber });
                    break;

                case MessageRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        Append(new AssistantTextItem { Content = m.Content, Timestamp = m.Timestamp.ToLocalTime() });
                    foreach (var call in m.ToolCalls ?? Enumerable.Empty<ToolCall>())
                    {
                        if (!string.IsNullOrEmpty(call.Id)) calls[call.Id] = call;
                        ToolStarted(call);
                    }
                    break;

                case MessageRole.Tool:
                    if (m.ToolCallId != null && calls.TryGetValue(m.ToolCallId, out var done))
                        ToolEnded(done, new ToolResult { ToolCallId = m.ToolCallId, ToolName = done.Name, Content = m.Content }, duration: null);
                    break;
            }
        }

        // A stored call without a stored result is not running any more.
        foreach (var card in _toolCards) card.IsRunning = false;
        AutoScroll.JumpToLatest();
    }

    /// <summary>The timeline index of the user message <c>/rewind N</c> refers to, or -1.</summary>
    public int IndexOfRewindPoint(int number)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i] is UserMessageItem { RewindNumber: var n } && n == number) return i;
        return -1;
    }

    private void Append(ChatItem item)
    {
        if (_openBubble != null && !ReferenceEquals(item, _openBubble)) CloseOpenBubble();
        Items.Add(item);
        AutoScroll.OnItemAdded(item);
    }

    private void CloseOpenBubble()
    {
        if (_openBubble == null) return;
        _openBubble.IsStreaming = false;
        _openBubble = null;
    }
}

/// <summary>
/// One agent turn as it streams into the timeline. Text continues the run's current bubble until something else
/// (a tool card, an approval) is added; after that the next text starts a new bubble.
/// </summary>
public sealed class AssistantRun
{
    private readonly ChatTimeline _timeline;

    internal AssistantRun(ChatTimeline timeline) => _timeline = timeline;

    internal List<AssistantTextItem> Bubbles { get; } = new();

    /// <summary>The bubbles this run produced, in order.</summary>
    public IReadOnlyList<AssistantTextItem> TextItems => Bubbles;

    public bool HasText => Bubbles.Any(b => b.HasContent);

    /// <summary>Streams model text; text from another agent starts its own bubble.</summary>
    public void AppendText(string text, string? agentId = null, string? agentRole = null) =>
        _timeline.AppendRunText(this, text, agentId, agentRole, anyAgent: false);

    /// <summary>Appends a status line ("*Cancelled*", an error) to the current bubble, or a new one when the bubble was closed.</summary>
    public void AppendNote(string text) => _timeline.AppendRunText(this, text, null, null, anyAgent: true);

    /// <summary>Closes the run; a run without any text gets a "(No response generated)" bubble.</summary>
    public void Complete() => _timeline.CompleteRun(this);
}
