using System;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Continue where you left off: the "Resume last session" banner and opening a stored session.</summary>
public partial class ChatViewModel
{
    private SessionResumeService? _sessionResume;

    /// <summary>Cost already spent in a resumed session, so the stored total keeps growing instead of restarting at zero.</summary>
    private decimal _resumedCostBase;

    /// <summary>The session offered by the start-up banner, or null when there is nothing to offer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResumeOffer))]
    [NotifyPropertyChangedFor(nameof(ResumeOfferText))]
    private ChatSessionInfo? _resumeOffer;

    public bool HasResumeOffer => ResumeOffer != null;

    /// <summary>"Resume last session (12 messages, 14:32)".</summary>
    public string ResumeOfferText => ResumeOffer == null ? string.Empty : FormatResumeOffer(ResumeOffer);

    internal static string FormatResumeOffer(ChatSessionInfo info) =>
        $"Resume last session ({info.MessageCount} message{(info.MessageCount == 1 ? string.Empty : "s")}, {LocalTime.FormatShortTime(info.UpdatedAt)})";

    private void InitializeResume(SessionResumeService? service) => _sessionResume = service;

    /// <summary>Fire-and-forget start-up check (called once the window is open).</summary>
    public void StartResumeCheck() => _ = CheckForResumableSessionAsync();

    /// <summary>
    /// If the last session of this working directory has messages: shows the banner (Ask), continues it right away
    /// (Always) or does nothing (Never). Skipped when the user has already started a conversation.
    /// </summary>
    public async Task CheckForResumableSessionAsync()
    {
        if (_sessionResume == null || IsProcessing) return;

        var mode = _configurationService?.Config.Ui?.ResumeLastSession ?? ResumeLastSessionMode.Ask;
        if (mode == ResumeLastSessionMode.Never) return;

        try
        {
            var last = await _sessionResume.FindLastAsync().ConfigureAwait(true);
            if (last == null || last.SessionId == SessionId) return;
            if (Timeline.Items.OfType<UserMessageItem>().Any()) return;

            if (mode == ResumeLastSessionMode.Always)
                await ResumeSessionAsync(last.SessionId).ConfigureAwait(true);
            else
                ResumeOffer = last;
        }
        catch (Exception)
        {
            // Resuming is a convenience: a broken session file must never get in the way of starting the app.
            ResumeOffer = null;
        }
    }

    [RelayCommand]
    private async Task AcceptResumeOfferAsync()
    {
        var offer = ResumeOffer;
        if (offer != null) await ResumeSessionAsync(offer.SessionId).ConfigureAwait(true);
    }

    /// <summary>The banner's "New": keep the empty chat and hide the offer.</summary>
    [RelayCommand]
    private void DismissResumeOffer() => ResumeOffer = null;

    /// <summary>
    /// Switches the chat to a stored session: the agent context is loaded (and reduced if it is over the token limit),
    /// and the timeline is rebuilt from the same messages, so the next message continues the old conversation.
    /// </summary>
    public async Task ResumeSessionAsync(string sessionId)
    {
        if (_sessionResume == null) return;
        if (IsProcessing) { Note("Wait for the agent to finish (or stop it) before opening another session."); return; }

        try
        {
            var maxTokens = _configurationService?.Config.Agent?.MaxTokens ?? 0;
            var resumed = await _sessionResume.ResumeAsync(sessionId, maxTokens).ConfigureAwait(true);
            var info = (await _sessionResume.ListAsync().ConfigureAwait(true)).FirstOrDefault(i => i.SessionId == sessionId);

            SessionId = sessionId;
            _resumedCostBase = info?.CostUsd ?? 0m;
            ResumeOffer = null;
            Approvals.ResetSession();
            SessionIdChanged?.Invoke(this, EventArgs.Empty);

            Timeline.LoadTranscript(resumed.Transcript, keepWelcome: false);
            Note($"Resumed **{info?.Title ?? sessionId}** ({info?.MessageCount ?? resumed.Transcript.Count} messages)." +
                 (resumed.Note != null ? " " + resumed.Note : string.Empty));
        }
        catch (Exception ex)
        {
            Note($"**Could not open the session:** {ex.Message}");
        }
    }

    /// <summary>Keeps the session list's cost column up to date (best effort).</summary>
    private void RecordSessionCost(decimal sessionCostUsd)
    {
        if (_sessionResume == null || sessionCostUsd <= 0) return;
        _ = _sessionResume.RecordCostAsync(SessionId, _resumedCostBase + sessionCostUsd);
    }
}
