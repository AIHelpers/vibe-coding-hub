using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Characters and skills in the desktop chat: pick the character that answers
/// (header selector or <c>/character</c>), list its skills (<c>/skills</c>), and
/// activate a skill for the next message (<c>/skill</c>).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Selector entry meaning "no character" (plain agent).</summary>
    public const string DefaultCharacterLabel = "Default agent";

    private ICharacterRegistry? _characterRegistry;
    private RolePresetLoader? _presetLoader;
    private ISkillRegistry? _chatSkillRegistry;
    private readonly List<SkillInvocation> _pendingSkills = new();

    /// <summary>Header selector items: <see cref="DefaultCharacterLabel"/> followed by character ids.</summary>
    public ObservableCollection<string> CharacterOptions { get; } = new() { DefaultCharacterLabel };

    [ObservableProperty]
    private string _selectedCharacterName = DefaultCharacterLabel;

    /// <summary>True when a character (not the default agent) answers chat messages.</summary>
    public bool HasActiveCharacter => !string.Equals(SelectedCharacterName, DefaultCharacterLabel, StringComparison.Ordinal);

    /// <summary>Names of skills queued with <c>/skill</c> for the next message (shown under the input box).</summary>
    public string PendingSkillsText => _pendingSkills.Count == 0 ? string.Empty : "Skills for next message: " + string.Join(", ", _pendingSkills.Select(s => s.Name));

    partial void OnSelectedCharacterNameChanged(string value) => OnPropertyChanged(nameof(HasActiveCharacter));

    private void InitializeCharacters(ICharacterRegistry? characters, RolePresetLoader? presets, ISkillRegistry? skills)
    {
        _characterRegistry = characters;
        _presetLoader = presets;
        _chatSkillRegistry = skills;
        RefreshCharacterOptions();
        if (_characterRegistry != null)
            _characterRegistry.Changed += (_, _) => PostToUi(RefreshCharacterOptions);
    }

    private static void PostToUi(Action action)
    {
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
            else Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
        catch
        {
            action();
        }
    }

    /// <summary>Reload the selector from the registry, keeping the current choice when it still exists.</summary>
    public void RefreshCharacterOptions()
    {
        if (_characterRegistry == null) return;
        var ids = _characterRegistry.ListAsync().GetAwaiter().GetResult()
            .Where(c => c.IsValid && !c.IsTemplate) // templates are bases for other characters, not chat partners
            .Select(c => c.Id)
            .ToList();
        var current = SelectedCharacterName;
        CharacterOptions.Clear();
        CharacterOptions.Add(DefaultCharacterLabel);
        foreach (var id in ids) CharacterOptions.Add(id);
        SelectedCharacterName = CharacterOptions.Contains(current) ? current : DefaultCharacterLabel;
    }

    /// <summary>The active character, or null for the default agent.</summary>
    private CharacterInfo? ActiveCharacter()
    {
        if (!HasActiveCharacter || _characterRegistry == null) return null;
        var c = _characterRegistry.GetAsync(SelectedCharacterName).GetAwaiter().GetResult();
        return c is { IsValid: true } ? c : null;
    }

    /// <summary>
    /// Apply the active character to a chat turn's options. The permission mode the
    /// user picked in the header stays unless the character sets one explicitly.
    /// </summary>
    internal AgentOptions ApplyActiveCharacter(AgentOptions options)
    {
        var character = ActiveCharacter();
        if (character == null) return options;
        var preset = character.BaseRole == null ? null : _presetLoader?.GetPreset(character.BaseRole);
        return CharacterResolver.Apply(options, character, preset, overwrite: true, useBaseRolePermission: false);
    }

    /// <summary>Prefix queued <c>/skill</c> instructions to the outgoing message and clear the queue.</summary>
    internal string ApplyPendingSkills(string userMessage)
    {
        if (_pendingSkills.Count == 0) return userMessage;
        var text = SkillInvocation.ComposeUserMessage(_pendingSkills, userMessage);
        _pendingSkills.Clear();
        OnPropertyChanged(nameof(PendingSkillsText));
        return text;
    }

    /// <summary>Handles <c>/character</c>, <c>/characters</c>, <c>/skill</c> and <c>/skills</c>. Returns true when handled.</summary>
    internal async Task<bool> TryHandleCharacterCommandAsync(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (parts[0].ToLowerInvariant())
        {
            case "/characters":
                await ListCharactersAsync().ConfigureAwait(true);
                return true;

            case "/character":
                if (arg.Length == 0)
                {
                    var active = ActiveCharacter();
                    Note(active == null
                        ? "No character active (default agent). Use `/characters` to list them, `/character <id>` to switch."
                        : $"Active character: **{active.Label}** (`{active.Id}`) — {active.Description}");
                    return true;
                }
                if (arg.Equals("none", StringComparison.OrdinalIgnoreCase) || arg.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCharacterName = DefaultCharacterLabel;
                    Note("Back to the default agent.");
                    return true;
                }
                await SwitchCharacterAsync(arg).ConfigureAwait(true);
                return true;

            case "/skills":
                await ListSkillsAsync(arg.Equals("--all", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(true);
                return true;

            case "/skill":
                if (arg.Length == 0) { Note("Usage: `/skill <name>` — its instructions are sent with your next message."); return true; }
                await QueueSkillAsync(arg).ConfigureAwait(true);
                return true;
        }
        return false;
    }

    private async Task SwitchCharacterAsync(string id)
    {
        if (_characterRegistry == null) { Note("Characters are not available."); return; }
        var c = await _characterRegistry.GetAsync(id).ConfigureAwait(true);
        if (c == null) { Note($"Character `{id}` not found. Use `/characters` to list them."); return; }
        if (!c.IsValid) { Note($"Character `{id}` is invalid: {string.Join(" ", c.ValidationErrors)}"); return; }
        if (!CharacterOptions.Contains(c.Id)) CharacterOptions.Add(c.Id);
        SelectedCharacterName = c.Id;
        var skills = c.Skills == null ? "all skills" : c.Skills.Count == 0 ? "no skills" : string.Join(", ", c.Skills);
        Note($"Now chatting with **{c.Label}** — {c.Description}\nSkills: {skills}" +
             (c.PinnedSkills.Count > 0 ? $"; pinned: {string.Join(", ", c.PinnedSkills)}" : ""));
    }

    private async Task ListCharactersAsync()
    {
        if (_characterRegistry == null) { Note("Characters are not available."); return; }
        var list = await _characterRegistry.ListAsync().ConfigureAwait(true);
        var lines = list.Select(c =>
            $"- {(c.Id == SelectedCharacterName ? "**" : "")}`{c.Id}`{(c.Id == SelectedCharacterName ? "** (active)" : "")} — {c.Description}" +
            $" _({c.Scope.ToString().ToLowerInvariant()}{(c.IsValid ? "" : ", invalid")})_");
        Note("Characters:\n" + string.Join("\n", lines) + "\n\nSwitch with `/character <id>`; manage them in Project Knowledge → Characters.");
    }

    private async Task ListSkillsAsync(bool all)
    {
        if (_chatSkillRegistry == null) { Note("Skills are not available."); return; }
        var skills = await _chatSkillRegistry.ListAsync().ConfigureAwait(true);
        var character = all ? null : ActiveCharacter();
        if (character?.Skills != null)
        {
            var allowed = new HashSet<string>(character.AllSkillReferences, StringComparer.OrdinalIgnoreCase);
            skills = skills.Where(s => allowed.Contains(s.Name)).ToList();
        }
        if (skills.Count == 0)
        {
            Note(character != null ? $"`{character.Id}` has no skills yet. Assign some in Project Knowledge → Characters." : "No skills yet. Create one in Project Knowledge → Skills.");
            return;
        }
        var header = character != null ? $"Skills for **{character.Label}**:" : "Skills:";
        Note(header + "\n" + string.Join("\n", skills.Select(s => $"- `{s.Name}` — {s.Description}")) + "\n\nUse `/skill <name>` to send one with your next message.");
    }

    private async Task QueueSkillAsync(string name)
    {
        if (_chatSkillRegistry == null) { Note("Skills are not available."); return; }
        var invocation = await _chatSkillRegistry.InvokeAsync(name).ConfigureAwait(true);
        if (invocation == null) { Note($"Skill `{name}` not found. Use `/skills` to list them."); return; }
        _pendingSkills.RemoveAll(s => string.Equals(s.Name, invocation.Name, StringComparison.OrdinalIgnoreCase));
        _pendingSkills.Add(invocation);
        OnPropertyChanged(nameof(PendingSkillsText));
        Note($"Skill `{invocation.Name}` will be sent with your next message.");
    }
}
