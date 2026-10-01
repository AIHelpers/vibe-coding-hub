using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Rewind / fork and reasoning-effort controls for the chat.</summary>
public partial class ChatViewModel
{
    private ConversationRewinder? _rewinder;
    private IRewindPrompt? _rewindPrompt;

    /// <summary>Raised after a fork switched this chat to a different session id.</summary>
    public event EventHandler? SessionIdChanged;

    /// <summary>Reasoning-effort choices shown in the header. "Default" leaves the provider's own behaviour alone.</summary>
    public ObservableCollection<string> EffortOptions { get; } = new() { "Default", "Off", "Low", "Medium", "High" };

    [ObservableProperty]
    private string _reasoningEffortName = "Default";

    /// <summary>The effort to send with each agent run; null means provider default.</summary>
    public ReasoningEffort? ReasoningEffortValue => ReasoningEffortName switch
    {
        "Off" => ReasoningEffort.Off,
        "Low" => ReasoningEffort.Low,
        "Medium" => ReasoningEffort.Medium,
        "High" => ReasoningEffort.High,
        _ => null
    };

    public bool CanRewind => _rewinder != null;

    /// <summary>Extra folders the user added to the task (from settings); they become allowed paths for the agent.</summary>
    private System.Collections.Generic.List<string> AdditionalFolders =>
        (_configurationService?.Config.Agent?.AdditionalFolders ?? new System.Collections.Generic.List<string>())
        .Where(System.IO.Directory.Exists).ToList();

    private void InitializeRewind(ConversationRewinder? rewinder, IRewindPrompt? prompt)
    {
        _rewinder = rewinder;
        _rewindPrompt = prompt;
    }

    private void Note(string text) =>
        Messages.Add(new ChatMessage { Role = "Assistant", Content = text, Timestamp = DateTime.Now });

    /// <summary>Opens the rewind dialog (also bound to the header button and /rewind).</summary>
    [RelayCommand]
    private async Task OpenRewindAsync()
    {
        if (IsProcessing) { Note("Wait for the agent to finish (or stop it) before rewinding."); return; }
        if (_rewinder == null) { Note("Rewind is not available in this build."); return; }

        var points = await _rewinder.ListPointsAsync(SessionId).ConfigureAwait(true);
        if (points.Count == 0) { Note("Nothing to rewind to yet: send a message first."); return; }

        if (_rewindPrompt == null)
        {
            Note("Your messages:\n" + string.Join("\n", points.Select(p => $"{p.Number}. {p.Preview} ({p.FilesChanged} file(s))")) +
                 "\n\nUse `/rewind N [chat|code|both]` or `/fork N`.");
            return;
        }

        var choice = await _rewindPrompt.ChooseAsync(points).ConfigureAwait(true);
        if (choice != null) await ApplyRewindAsync(choice.Number, choice.Action).ConfigureAwait(true);
    }

    private async Task ApplyRewindAsync(int number, RewindAction action)
    {
        if (_rewinder == null) return;
        try
        {
            if (action == RewindAction.Fork)
            {
                var (newId, message) = await _rewinder.ForkAsync(SessionId, number).ConfigureAwait(true);
                if (newId == null) { Note(message); return; }
                SessionId = newId;
                SessionIdChanged?.Invoke(this, EventArgs.Empty);
                await ReloadTranscriptAsync().ConfigureAwait(true);
                Note(message + " You are now in the fork.");
                return;
            }

            var mode = action switch { RewindAction.Chat => RewindMode.Conversation, RewindAction.Code => RewindMode.Code, _ => RewindMode.Both };
            var result = await _rewinder.RewindAsync(SessionId, number, mode).ConfigureAwait(true);
            if (!result.Success) { Note(result.Message); return; }

            if (mode != RewindMode.Code) await ReloadTranscriptAsync().ConfigureAwait(true);
            Note(result.Message + (result.SkippedFiles is { Count: > 0 } ? $" Could not restore: {string.Join(", ", result.SkippedFiles)}." : ""));

            // Put the undone message back in the box so it can be edited and resent.
            if (!string.IsNullOrEmpty(result.RestoredUserText)) InputText = result.RestoredUserText!;
        }
        catch (Exception ex)
        {
            Note($"**Rewind failed:** {ex.Message}");
        }
    }

    /// <summary>Rebuilds the visible chat from the session's stored messages (keeps the welcome message).</summary>
    private async Task ReloadTranscriptAsync()
    {
        if (_rewinder == null) return;
        var transcript = await _rewinder.GetTranscriptAsync(SessionId).ConfigureAwait(true);

        var welcome = Messages.FirstOrDefault();
        Messages.Clear();
        ToolCallCards.Clear();
        if (welcome is { Role: "Assistant" }) Messages.Add(welcome);

        foreach (var m in transcript)
        {
            if (string.IsNullOrWhiteSpace(m.Content)) continue; // tool-call-only assistant turns
            if (m.Role == MessageRole.User) Messages.Add(new ChatMessage { Role = "User", Content = m.Content, Timestamp = m.Timestamp });
            else if (m.Role == MessageRole.Assistant) Messages.Add(new ChatMessage { Role = "Assistant", Content = m.Content, Timestamp = m.Timestamp });
        }
    }

    /// <summary>Handles /rewind, /fork and /effort typed in the chat box. Returns true when the input was consumed.</summary>
    private async Task<bool> TryHandleSessionCommandAsync(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        switch (parts[0].ToLowerInvariant())
        {
            case "/rewind":
                if (parts.Length == 1) { await OpenRewindAsync().ConfigureAwait(true); return true; }
                if (!int.TryParse(parts[1], out var n)) { Note("Usage: `/rewind [N [chat|code|both]]`"); return true; }
                var action = parts.Length > 2 ? parts[2].ToLowerInvariant() switch
                {
                    "chat" or "conversation" => RewindAction.Chat,
                    "code" or "files" => RewindAction.Code,
                    _ => RewindAction.Both
                } : RewindAction.Both;
                if (IsProcessing) { Note("Wait for the agent to finish before rewinding."); return true; }
                await ApplyRewindAsync(n, action).ConfigureAwait(true);
                return true;

            case "/fork":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var f)) { Note("Usage: `/fork N` (N is the message number from `/rewind`)"); return true; }
                if (IsProcessing) { Note("Wait for the agent to finish before forking."); return true; }
                await ApplyRewindAsync(f, RewindAction.Fork).ConfigureAwait(true);
                return true;

            case "/effort":
                if (parts.Length == 1) { Note($"Reasoning effort: **{ReasoningEffortName}**. Use `/effort off|low|medium|high|default`."); return true; }
                var wanted = EffortOptions.FirstOrDefault(o => o.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (wanted == null) { Note("Usage: `/effort off|low|medium|high|default`"); return true; }
                ReasoningEffortName = wanted;
                Note($"Reasoning effort set to **{wanted}**.");
                return true;
        }
        return false;
    }
}
