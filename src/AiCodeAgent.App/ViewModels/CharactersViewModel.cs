using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One row in the character list.</summary>
public sealed class CharacterListItem
{
    public required CharacterInfo Info { get; init; }
    public string Id => Info.Id;
    public string Label => Info.Label;
    public string Description => Info.Description;

    public string ScopeLabel
    {
        get
        {
            var scope = Info.Scope switch
            {
                SkillScope.BuiltIn => "Built-in",
                SkillScope.Project => "Project",
                _ => "Global"
            };
            var skills = Info.Skills == null ? "all skills" : $"{Info.Skills.Count + Info.PinnedSkills.Count} skill(s)";
            return $"{scope} · {skills}" + (Info.Overrides ? " · override" : "") + (Info.IsValid ? "" : " · invalid");
        }
    }
}

/// <summary>One checkbox row in the skill-assignment checklist of the selected character.</summary>
public sealed partial class CharacterSkillChoice : ObservableObject
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isAssigned;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>True for references to skills that do not exist (shown so the user can remove them).</summary>
    public bool IsMissing { get; init; }

    public string Hint => IsMissing ? "missing skill" : Description;
}

/// <summary>
/// The "Characters" tab of Project Knowledge: create, edit and delete characters
/// (Markdown files) and assign skills to them with a checklist. Built-in role
/// characters are shown read-only; ticking a skill for one creates a global
/// override file that keeps its role.
/// </summary>
public partial class CharactersViewModel : ObservableObject
{
    private readonly ICharacterRegistry? _characters;
    private readonly ISkillRegistry? _skills;
    private readonly RolePresetLoader? _presets;
    private readonly ILogger<CharactersViewModel>? _logger;
    private bool _syncingChecklist;

    public ObservableCollection<CharacterListItem> Characters { get; } = new();
    public ObservableCollection<CharacterSkillChoice> SkillChoices { get; } = new();

    /// <summary>Base roles offered in the new-character form ("" = generic agent).</summary>
    public ObservableCollection<string> BaseRoles { get; } = new();

    [ObservableProperty]
    private CharacterListItem? _selectedCharacter;

    [ObservableProperty]
    private string _selectedContent = string.Empty;

    [ObservableProperty]
    private bool _selectedIsDirty;

    [ObservableProperty]
    private string _selectedSummary = string.Empty;

    [ObservableProperty]
    private string _selectedProblems = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _skillFilter = string.Empty;

    // New-character form
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string _newId = string.Empty;
    [ObservableProperty] private string _newDisplayName = string.Empty;
    [ObservableProperty] private string _newAvatar = string.Empty;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private string _newBaseRole = string.Empty;
    [ObservableProperty] private bool _newIsProject = true;

    // Inline delete confirmation
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _deleteConfirmText = string.Empty;

    public CharactersViewModel(
        ICharacterRegistry? characters = null,
        ISkillRegistry? skills = null,
        RolePresetLoader? presets = null,
        ILogger<CharactersViewModel>? logger = null)
    {
        _characters = characters;
        _skills = skills;
        _presets = presets;
        _logger = logger;
        BaseRoles.Add(string.Empty);
        foreach (var p in _presets?.GetAllPresets().Select(p => p.Role).OrderBy(r => r, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())
            BaseRoles.Add(p);
    }

    public bool IsAvailable => _characters != null;

    /// <summary>True when the selected character is a built-in (no file to edit or delete).</summary>
    public bool SelectedIsBuiltIn => SelectedCharacter?.Info.IsBuiltIn == true;

    public bool CanEditSelected => SelectedCharacter != null && !SelectedIsBuiltIn;

    /// <summary>True when the selected character may use every skill (checklist ticks restrict it).</summary>
    public bool SelectedUsesAllSkills => SelectedCharacter?.Info.Skills == null && SelectedCharacter != null;

    partial void OnSelectedCharacterChanged(CharacterListItem? value)
    {
        OnPropertyChanged(nameof(SelectedIsBuiltIn));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(SelectedUsesAllSkills));
    }

    partial void OnSelectedContentChanged(string value) => SelectedIsDirty = true;

    partial void OnSkillFilterChanged(string value) => _ = RebuildChecklistAsync();

    // ===================== Listing =====================

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_characters == null)
        {
            StatusText = "Characters are not available.";
            return;
        }
        var selectedId = SelectedCharacter?.Id;
        try
        {
            _characters.Refresh();
            var list = await _characters.ListAsync();
            Characters.Clear();
            foreach (var c in list)
                Characters.Add(new CharacterListItem { Info = c });
            StatusText = $"{list.Count(c => !c.IsBuiltIn)} custom, {list.Count(c => c.IsBuiltIn)} built-in";

            var again = selectedId == null ? null : Characters.FirstOrDefault(c => c.Id == selectedId);
            if (again != null) await SelectAsync(again, keepEditor: SelectedIsDirty);
            else if (selectedId != null) ClearSelection();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list characters");
            StatusText = $"Failed to list characters: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task SelectCharacterAsync(CharacterListItem? item) => SelectAsync(item, keepEditor: false);

    private async Task SelectAsync(CharacterListItem? item, bool keepEditor)
    {
        IsConfirmingDelete = false;
        SelectedCharacter = item;
        if (item == null) { ClearSelection(); return; }

        var c = item.Info;
        if (!keepEditor && _characters != null)
        {
            SelectedContent = c.IsBuiltIn ? BuiltInDescription(c) : await _characters.LoadAsync(c.Id);
            SelectedIsDirty = false;
        }

        var preset = c.BaseRole == null ? null : _presets?.GetPreset(c.BaseRole);
        var resolved = CharacterResolver.Apply(new AgentOptions(), c, preset);
        SelectedSummary =
            $"{c.Label}  ·  base role: {c.BaseRole ?? "-"}  ·  model: {c.Model ?? "default"}  ·  permission: {CharacterRegistry.FormatPermissionMode(resolved.PermissionMode)}\n" +
            $"Tools: {(resolved.EnabledTools.Count == 0 ? "all" : string.Join(", ", resolved.EnabledTools))}" +
            (resolved.DisabledTools.Count > 0 ? $" (minus {string.Join(", ", resolved.DisabledTools)})" : "");
        await UpdateProblemsAsync(c);
        await RebuildChecklistAsync();
    }

    private void ClearSelection()
    {
        SelectedCharacter = null;
        SelectedContent = string.Empty;
        SelectedIsDirty = false;
        SelectedSummary = string.Empty;
        SelectedProblems = string.Empty;
        SkillChoices.Clear();
    }

    private static string BuiltInDescription(CharacterInfo c) =>
        $"Built-in character '{c.Id}' (from the {c.BaseRole} role preset).\n\n{c.Description}\n\n" +
        "Tick skills on the right to customize it: that creates ~/.aiagent/characters/" + c.Id + ".md, which you can then edit here.";

    private async Task UpdateProblemsAsync(CharacterInfo c)
    {
        var lines = c.ValidationErrors.Select(e => "Error: " + e).Concat(c.Warnings.Select(w => "Warning: " + w)).ToList();
        if (_skills != null)
        {
            var known = new HashSet<string>((await _skills.ListAllAsync()).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            lines.AddRange(c.AllSkillReferences.Where(s => s != CharacterRegistry.AllSkillsToken && !known.Contains(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => $"Warning: skill '{s}' does not exist."));
        }
        SelectedProblems = string.Join("\n", lines);
    }

    /// <summary>Rebuild the skill checklist for the selected character (every skill + missing references).</summary>
    private async Task RebuildChecklistAsync()
    {
        foreach (var choice in SkillChoices) choice.PropertyChanged -= OnChoiceChanged;
        SkillChoices.Clear();
        var c = SelectedCharacter?.Info;
        if (c == null || _skills == null) return;

        var all = await _skills.ListAllAsync();
        var listed = new HashSet<string>(c.Skills ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var pinned = new HashSet<string>(c.PinnedSkills, StringComparer.OrdinalIgnoreCase);
        var filter = SkillFilter.Trim();

        var rows = all.Select(s => new CharacterSkillChoice { Name = s.Name, Description = s.Description }).ToList();
        rows.AddRange(listed.Concat(pinned)
            .Where(n => n != CharacterRegistry.AllSkillsToken && all.All(s => !string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new CharacterSkillChoice { Name = n, IsMissing = true }));

        _syncingChecklist = true;
        try
        {
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (filter.Length > 0 && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !row.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                row.IsAssigned = listed.Contains(row.Name) || pinned.Contains(row.Name);
                row.IsPinned = pinned.Contains(row.Name);
                row.PropertyChanged += OnChoiceChanged;
                SkillChoices.Add(row);
            }
        }
        finally
        {
            _syncingChecklist = false;
        }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingChecklist || sender is not CharacterSkillChoice choice) return;
        if (e.PropertyName is nameof(CharacterSkillChoice.IsAssigned) or nameof(CharacterSkillChoice.IsPinned))
        {
            // Chain changes so rapid clicks are written in order, one at a time.
            var previous = LastChoiceTask;
            var property = e.PropertyName;
            LastChoiceTask = previous.ContinueWith(_ => ApplyChoiceAsync(choice, property), UiSchedulerOrDefault()).Unwrap();
        }
    }

    /// <summary>The UI scheduler when there is a synchronization context, otherwise the default scheduler.</summary>
    private static TaskScheduler UiSchedulerOrDefault() =>
        System.Threading.SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;

    /// <summary>The most recent checklist write (awaitable by tests and callers that need the file updated).</summary>
    internal Task LastChoiceTask { get; private set; } = Task.CompletedTask;

    /// <summary>Persist one checklist change (assign/unassign/pin) to the character file.</summary>
    internal async Task ApplyChoiceAsync(CharacterSkillChoice choice, string? changedProperty)
    {
        if (_characters == null || SelectedCharacter == null) return;
        var id = SelectedCharacter.Id;
        if (SelectedIsDirty)
        {
            StatusText = "Save or reload your edits to this character before changing its skills.";
            await RebuildChecklistAsync();
            return;
        }
        try
        {
            // A character that may use every skill becomes restricted to an explicit list on first tick.
            if (SelectedCharacter.Info.Skills == null && !SelectedCharacter.Info.IsBuiltIn)
                await _characters.UnassignSkillsAsync(id, new[] { CharacterRegistry.AllSkillsToken });

            if (changedProperty == nameof(CharacterSkillChoice.IsPinned) && choice.IsPinned)
                await _characters.AssignSkillsAsync(id, new[] { choice.Name }, pinned: true);
            else if (choice.IsAssigned)
                await _characters.AssignSkillsAsync(id, new[] { choice.Name }, pinned: false);
            else
                await _characters.UnassignSkillsAsync(id, new[] { choice.Name });

            StatusText = $"Updated skills of '{id}'.";
        }
        catch (SkillValidationException ex)
        {
            StatusText = string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update skills of {Id}", id);
            StatusText = $"Failed to update skills: {ex.Message}";
        }
        await RefreshAsync();
    }

    // ===================== Edit / save =====================

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (_characters == null || SelectedCharacter == null || SelectedIsBuiltIn) return;
        try
        {
            var updated = await _characters.UpdateAsync(SelectedCharacter.Id, SelectedContent, SelectedCharacter.Info.Scope);
            SelectedIsDirty = false;
            StatusText = $"Saved '{updated.Id}'.";
            await RefreshAsync();
        }
        catch (SkillValidationException ex)
        {
            StatusText = "Not saved: " + string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save character {Id}", SelectedCharacter.Id);
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    // ===================== Create =====================

    [RelayCommand]
    public void BeginCreate()
    {
        NewId = NewDisplayName = NewAvatar = NewDescription = NewBaseRole = string.Empty;
        NewIsProject = true;
        IsCreating = true;
    }

    [RelayCommand]
    public void CancelCreate() => IsCreating = false;

    partial void OnNewDisplayNameChanged(string value)
    {
        // Suggest an id from the display name until the user types one.
        if (string.IsNullOrWhiteSpace(NewId) || NewId == _lastSuggestedId)
        {
            NewId = AiCodeAgent.Core.Context.LocalFileStore.ToKebab(value);
            _lastSuggestedId = NewId;
        }
    }
    private string _lastSuggestedId = string.Empty;

    [RelayCommand]
    public async Task CreateAsync()
    {
        if (_characters == null) return;
        try
        {
            var created = await _characters.CreateAsync(new CharacterDraft
            {
                Id = NewId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(NewDisplayName) ? null : NewDisplayName.Trim(),
                Avatar = string.IsNullOrWhiteSpace(NewAvatar) ? null : NewAvatar.Trim(),
                Description = NewDescription.Trim(),
                BaseRole = string.IsNullOrWhiteSpace(NewBaseRole) ? null : NewBaseRole
            }, NewIsProject ? SkillScope.Project : SkillScope.Global);
            IsCreating = false;
            await RefreshAsync();
            var item = Characters.FirstOrDefault(c => c.Id == created.Id);
            if (item != null) await SelectCharacterAsync(item);
            StatusText = $"Created '{created.Id}'. Tick skills on the right to give it abilities.";
        }
        catch (SkillValidationException ex)
        {
            StatusText = string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create character");
            StatusText = $"Failed to create character: {ex.Message}";
        }
    }

    // ===================== Delete =====================

    [RelayCommand]
    public void RequestDelete()
    {
        if (SelectedCharacter == null || SelectedIsBuiltIn) return;
        var c = SelectedCharacter.Info;
        DeleteConfirmText = $"Delete character '{c.Id}' ({c.Scope.ToString().ToLowerInvariant()})? Its file will be removed." +
                            (c.Overrides ? " The definition it overrides will be used again." : "");
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    public void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    public async Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;
        if (_characters == null || SelectedCharacter == null || SelectedIsBuiltIn) return;
        var id = SelectedCharacter.Id;
        try
        {
            var deleted = await _characters.DeleteAsync(id, SelectedCharacter.Info.Scope);
            StatusText = deleted ? $"Deleted '{id}'." : $"Nothing deleted for '{id}'.";
            ClearSelection();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete character {Id}", id);
            StatusText = $"Delete failed: {ex.Message}";
        }
    }
}
