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
            return $"{scope} · {skills}"
                   + (Info.IsTemplate ? " · template" : "")
                   + (Info.Extends != null ? $" · extends {Info.Extends}" : "")
                   + (Info.Overrides ? " · override" : "") + (Info.IsValid ? "" : " · invalid");
        }
    }
}

/// <summary>One row in the selected character's skill list (own, inherited from its template, removed, or missing).</summary>
public sealed partial class CharacterSkillChoice : ObservableObject
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>True for references to skills that do not exist (shown so the user can remove them).</summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Where the skill comes from for the selected character ("own" or "template:x"),
    /// or the inherited source of a skill the character removed.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>True when the skill is provided by the template the character extends.</summary>
    public bool IsInherited => Source != null && Source != CharacterRegistry.OwnSource;

    /// <summary>True for an inherited skill the character dropped with remove-skills (can be restored).</summary>
    public bool IsRemoved { get; init; }

    /// <summary>True when the character has this skill (not removed).</summary>
    public bool IsAssigned => !IsRemoved;

    /// <summary>Pinning is changed on the character's own skills; inherited ones follow their template.</summary>
    public bool CanTogglePin => IsAssigned && !IsInherited;

    public string Hint
    {
        get
        {
            if (IsMissing) return "missing skill — add it to the library or remove it";
            var from = IsInherited ? "from " + DescribeSource(Source!) : null;
            if (IsRemoved) return $"removed (was {from})";
            return from == null ? Description : $"{from} · {Description}";
        }
    }

    /// <summary>"template software-developer" style label.</summary>
    public static string DescribeSource(string source) =>
        source.StartsWith("template:", StringComparison.Ordinal) ? "template " + source["template:".Length..] : source;
}

/// <summary>A library skill offered by "Add skill" (not yet assigned to the selected character).</summary>
public sealed class LibrarySkillItem
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public SkillScope Scope { get; init; }
    public string Label => $"{Name} ({Scope.ToString().ToLowerInvariant()})";
    public override string ToString() => Name;
}

/// <summary>
/// The "Characters" tab of Project Knowledge: create, edit and delete characters
/// (Markdown files) and give each one its own skills with "＋ Add skill" (pick a skill
/// from the shared library or create a new one on the spot). Built-in role characters
/// are shown read-only; adding a skill to one creates a global override file that keeps its role.
/// </summary>
public partial class CharactersViewModel : ObservableObject
{
    private readonly ICharacterRegistry? _characters;
    private readonly ISkillRegistry? _skills;
    private readonly RolePresetLoader? _presets;
    private readonly ILogger<CharactersViewModel>? _logger;
    private readonly SkillMaintenance? _maintenance;
    private bool _syncingChecklist;

    public ObservableCollection<CharacterListItem> Characters { get; } = new();

    /// <summary>The selected character's skills: own, inherited from its template, removed (restorable) and missing.</summary>
    public ObservableCollection<CharacterSkillChoice> SkillChoices { get; } = new();

    /// <summary>Library skills "Add skill" can pick from (not yet assigned to the selected character).</summary>
    public ObservableCollection<LibrarySkillItem> AvailableSkills { get; } = new();

    /// <summary>Selector value meaning "extends nothing".</summary>
    public const string NoTemplate = "(none)";

    /// <summary>Templates/characters the selected (or new) character can extend; templates first.</summary>
    public ObservableCollection<string> ExtendsOptions { get; } = new() { NoTemplate };

    [ObservableProperty]
    private string _selectedExtends = NoTemplate;

    [ObservableProperty]
    private bool _selectedIsTemplate;

    private bool _syncingHeader;

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

    // "＋ Add skill" panel
    [ObservableProperty] private bool _isAddingSkill;
    [ObservableProperty] private bool _addSkillCreatesNew;
    [ObservableProperty] private LibrarySkillItem? _selectedLibrarySkill;
    [ObservableProperty] private string _libraryFilter = string.Empty;
    [ObservableProperty] private bool _addSkillPinned;
    [ObservableProperty] private string _newSkillName = string.Empty;
    [ObservableProperty] private string _newSkillDescription = string.Empty;
    [ObservableProperty] private string _newSkillInstructions = string.Empty;
    [ObservableProperty] private bool _newSkillIsProject;

    // New-character form
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string _newId = string.Empty;
    [ObservableProperty] private string _newDisplayName = string.Empty;
    [ObservableProperty] private string _newAvatar = string.Empty;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private string _newBaseRole = string.Empty;
    [ObservableProperty] private bool _newIsProject = true;
    [ObservableProperty] private string _newExtends = NoTemplate;
    [ObservableProperty] private bool _newIsTemplate;

    // Inline delete confirmation
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _deleteConfirmText = string.Empty;

    public CharactersViewModel(
        ICharacterRegistry? characters = null,
        ISkillRegistry? skills = null,
        RolePresetLoader? presets = null,
        ILogger<CharactersViewModel>? logger = null,
        SkillMaintenance? maintenance = null)
    {
        _characters = characters;
        _skills = skills;
        _presets = presets;
        _logger = logger;
        _maintenance = maintenance ?? (characters != null && skills != null ? new SkillMaintenance(skills, characters) : null);
        BaseRoles.Add(string.Empty);
        foreach (var p in _presets?.GetAllPresets().Select(p => p.Role).OrderBy(r => r, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())
            BaseRoles.Add(p);
    }

    public bool IsAvailable => _characters != null;

    /// <summary>True when the selected character is a built-in (no file to edit or delete).</summary>
    public bool SelectedIsBuiltIn => SelectedCharacter?.Info.IsBuiltIn == true;

    public bool CanEditSelected => SelectedCharacter != null && !SelectedIsBuiltIn;

    /// <summary>True when the selected character may use every skill (adding a skill gives it an explicit list).</summary>
    public bool SelectedUsesAllSkills => SelectedCharacter?.Info.Skills == null && SelectedCharacter != null;

    /// <summary>True when the selected character has a skill list to show.</summary>
    public bool HasSkillChoices => SkillChoices.Count > 0;

    /// <summary>Title of the right-hand column, e.g. "Skills of Dana".</summary>
    public string SkillsHeader => SelectedCharacter == null ? "Skills" : $"Skills of {SelectedCharacter.Info.DisplayName}";

    /// <summary>Pick-from-library mode of the "Add skill" panel (the opposite of <see cref="AddSkillCreatesNew"/>).</summary>
    public bool AddSkillFromLibrary
    {
        get => !AddSkillCreatesNew;
        set => AddSkillCreatesNew = !value;
    }

    public bool HasAvailableSkills => AvailableSkills.Count > 0;

    /// <summary>Explains what adding a skill changes for characters that may currently use every skill.</summary>
    public string AddSkillNote => SelectedUsesAllSkills
        ? "This character can use every skill right now. After you add a skill it uses only the skills you add."
        : string.Empty;

    partial void OnSelectedCharacterChanged(CharacterListItem? value)
    {
        OnPropertyChanged(nameof(SelectedIsBuiltIn));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(SelectedUsesAllSkills));
        OnPropertyChanged(nameof(SkillsHeader));
        OnPropertyChanged(nameof(AddSkillNote));
    }

    partial void OnAddSkillCreatesNewChanged(bool value) => OnPropertyChanged(nameof(AddSkillFromLibrary));

    private bool _suppressLibraryRebuild;

    partial void OnLibraryFilterChanged(string value)
    {
        if (!_suppressLibraryRebuild) _ = RebuildAvailableSkillsAsync();
    }

    partial void OnSelectedContentChanged(string value) => SelectedIsDirty = true;

    partial void OnSelectedExtendsChanged(string value)
    {
        if (_syncingHeader || SelectedCharacter == null) return;
        var parent = value == NoTemplate || string.IsNullOrEmpty(value) ? null : value;
        Enqueue(() => ApplyCharacterEditAsync(id => _characters!.SetExtendsAsync(id, parent),
            parent == null ? "no longer extends a template" : $"now extends '{parent}'"));
    }

    partial void OnSelectedIsTemplateChanged(bool value)
    {
        if (_syncingHeader || SelectedCharacter == null) return;
        Enqueue(() => ApplyCharacterEditAsync(id => _characters!.SetTemplateAsync(id, value),
            value ? "is now a template" : "is a regular character"));
    }

    /// <summary>Run writes one at a time, in click order. Returns the queued write.</summary>
    private Task Enqueue(Func<Task> work)
    {
        var previous = LastChoiceTask;
        LastChoiceTask = previous.ContinueWith(_ => work(), UiSchedulerOrDefault()).Unwrap();
        return LastChoiceTask;
    }

    /// <summary>Apply a frontmatter change to the selected character (guarded against unsaved edits), then refresh.</summary>
    private Task<bool> ApplyCharacterEditAsync(Func<string, Task<CharacterInfo>> change, string description) =>
        WriteSelectedAsync(async id =>
        {
            await change(id);
            return $"'{id}' {description}.";
        });

    /// <summary>
    /// Run one write for the selected character: refuses while the editor has unsaved changes,
    /// reports validation errors in the status line, then refreshes. <paramref name="change"/>
    /// returns the success message. Returns true when the write succeeded.
    /// </summary>
    private async Task<bool> WriteSelectedAsync(Func<string, Task<string>> change)
    {
        if (_characters == null || SelectedCharacter == null) return false;
        var id = SelectedCharacter.Id;
        if (SelectedIsDirty)
        {
            await RefreshAsync();
            StatusText = "Save or reload your edits to this character first.";
            return false;
        }
        var ok = false;
        try
        {
            StatusText = await change(id);
            ok = true;
        }
        catch (SkillValidationException ex)
        {
            StatusText = string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update character {Id}", id);
            StatusText = $"Update failed: {ex.Message}";
        }
        var message = StatusText;
        await RefreshAsync();
        StatusText = message;
        return ok;
    }

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
            StatusText = $"{list.Count(c => !c.IsBuiltIn)} custom ({list.Count(c => c.IsTemplate)} template(s)), {list.Count(c => c.IsBuiltIn)} built-in";
            RebuildExtendsOptions(list);

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

    private void RebuildExtendsOptions(IReadOnlyList<CharacterInfo> list)
    {
        _syncingHeader = true;
        try
        {
            var keepSelected = SelectedExtends;
            var keepNew = NewExtends;
            ExtendsOptions.Clear();
            ExtendsOptions.Add(NoTemplate);
            foreach (var c in list.Where(c => c.IsValid).OrderByDescending(c => c.IsTemplate).ThenBy(c => c.IsBuiltIn).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
                ExtendsOptions.Add(c.Id);
            SelectedExtends = ExtendsOptions.Contains(keepSelected) ? keepSelected : NoTemplate;
            NewExtends = ExtendsOptions.Contains(keepNew) ? keepNew : NoTemplate;
        }
        finally
        {
            _syncingHeader = false;
        }
    }

    [RelayCommand]
    public Task SelectCharacterAsync(CharacterListItem? item) => SelectAsync(item, keepEditor: false);

    private async Task SelectAsync(CharacterListItem? item, bool keepEditor)
    {
        IsConfirmingDelete = false;
        if (item?.Id != SelectedCharacter?.Id) IsAddingSkill = false;
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
            (c.Extends != null ? $"Extends: {string.Join(" → ", c.InheritanceChain.DefaultIfEmpty(c.Extends))}\n" : "") +
            $"Tools: {(resolved.EnabledTools.Count == 0 ? "all" : string.Join(", ", resolved.EnabledTools))}" +
            (resolved.DisabledTools.Count > 0 ? $" (minus {string.Join(", ", resolved.DisabledTools)})" : "");

        _syncingHeader = true;
        try
        {
            SelectedExtends = c.Extends != null && ExtendsOptions.Contains(c.Extends) ? c.Extends : NoTemplate;
            SelectedIsTemplate = c.IsTemplate;
        }
        finally
        {
            _syncingHeader = false;
        }
        await UpdateProblemsAsync(c);
        await RebuildChecklistAsync();
        await RebuildAvailableSkillsAsync();
    }

    private void ClearSelection()
    {
        SelectedCharacter = null;
        SelectedContent = string.Empty;
        SelectedIsDirty = false;
        SelectedSummary = string.Empty;
        SelectedProblems = string.Empty;
        foreach (var choice in SkillChoices) choice.PropertyChanged -= OnChoiceChanged;
        SkillChoices.Clear();
        AvailableSkills.Clear();
        IsAddingSkill = false;
        OnPropertyChanged(nameof(HasSkillChoices));
        OnPropertyChanged(nameof(HasAvailableSkills));
    }

    private static string BuiltInDescription(CharacterInfo c) =>
        $"Built-in character '{c.Id}' (from the {c.BaseRole} role preset).\n\n{c.Description}\n\n" +
        "Click \"＋ Add skill\" on the right to give it its own skills: that creates ~/.aiagent/characters/" + c.Id + ".md, which you can then edit here.";

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

    /// <summary>Rebuild the selected character's skill list: its effective skills, removed inherited skills and missing references.</summary>
    private async Task RebuildChecklistAsync()
    {
        foreach (var choice in SkillChoices) choice.PropertyChanged -= OnChoiceChanged;
        SkillChoices.Clear();
        var c = SelectedCharacter?.Info;
        if (c == null || _skills == null || c.Skills == null)
        {
            OnPropertyChanged(nameof(HasSkillChoices));
            return;
        }

        var library = (await _skills.ListAllAsync()).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var pinned = new HashSet<string>(c.PinnedSkills, StringComparer.OrdinalIgnoreCase);
        var removed = c.RemoveSkills
            .Where(r => c.InheritedSkillSources.ContainsKey(r) && !c.SkillSources.ContainsKey(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CharacterSkillChoice Row(string name, string? source, bool isRemoved) => new()
        {
            Name = name,
            Description = library.TryGetValue(name, out var info) ? info.Description : string.Empty,
            IsMissing = !library.ContainsKey(name),
            Source = source,
            IsRemoved = isRemoved
        };

        var rows = c.Skills.Concat(c.PinnedSkills)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => Row(n, c.SkillSources.TryGetValue(n, out var src) ? src : CharacterRegistry.OwnSource, false))
            .Concat(removed.Select(n => Row(n, c.InheritedSkillSources[n], true)));

        _syncingChecklist = true;
        try
        {
            // Own skills first, then inherited, then removed; alphabetical inside each group.
            foreach (var row in rows.OrderBy(r => r.IsRemoved).ThenBy(r => r.IsInherited).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                row.IsPinned = pinned.Contains(row.Name);
                row.PropertyChanged += OnChoiceChanged;
                SkillChoices.Add(row);
            }
        }
        finally
        {
            _syncingChecklist = false;
        }
        OnPropertyChanged(nameof(HasSkillChoices));
    }

    /// <summary>Library skills the selected character does not have yet, filtered by <see cref="LibraryFilter"/>.</summary>
    private async Task RebuildAvailableSkillsAsync()
    {
        var keep = SelectedLibrarySkill?.Name;
        AvailableSkills.Clear();
        var c = SelectedCharacter?.Info;
        if (c != null && _skills != null)
        {
            var has = new HashSet<string>(c.Skills == null ? Array.Empty<string>() : c.AllSkillReferences, StringComparer.OrdinalIgnoreCase);
            var filter = LibraryFilter.Trim();
            foreach (var s in (await _skills.ListAllAsync()).Where(s => s.IsValid && !has.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (filter.Length > 0 && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                AvailableSkills.Add(new LibrarySkillItem { Name = s.Name, Description = s.Description, Scope = s.Scope });
            }
        }
        SelectedLibrarySkill = AvailableSkills.FirstOrDefault(s => string.Equals(s.Name, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasAvailableSkills));
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingChecklist || sender is not CharacterSkillChoice choice) return;
        if (e.PropertyName == nameof(CharacterSkillChoice.IsPinned))
        {
            var pin = choice.IsPinned;
            Enqueue(() => ApplyCharacterEditAsync(id => _characters!.AssignSkillsAsync(id, new[] { choice.Name }, pinned: pin),
                pin ? $"always keeps '{choice.Name}' in context" : $"loads '{choice.Name}' on demand"));
        }
    }

    /// <summary>The UI scheduler when there is a synchronization context, otherwise the default scheduler.</summary>
    private static TaskScheduler UiSchedulerOrDefault() =>
        System.Threading.SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;

    /// <summary>The most recent checklist write (awaitable by tests and callers that need the file updated).</summary>
    internal Task LastChoiceTask { get; private set; } = Task.CompletedTask;

    /// <summary>Remove a skill from the selected character (an inherited one is dropped for this character only).</summary>
    [RelayCommand]
    public Task RemoveSkillAsync(CharacterSkillChoice? choice)
    {
        if (choice == null || SelectedCharacter == null) return Task.CompletedTask;
        return Enqueue(() => ApplyCharacterEditAsync(id => _characters!.UnassignSkillsAsync(id, new[] { choice.Name }),
            choice.IsInherited ? $"no longer uses '{choice.Name}' (still in its template)" : $"no longer has '{choice.Name}'"));
    }

    /// <summary>Bring back an inherited skill the character removed.</summary>
    [RelayCommand]
    public Task RestoreSkillAsync(CharacterSkillChoice? choice)
    {
        if (choice == null || SelectedCharacter == null) return Task.CompletedTask;
        return Enqueue(() => ApplyCharacterEditAsync(id => _characters!.AssignSkillsAsync(id, new[] { choice.Name }),
            $"uses '{choice.Name}' again"));
    }

    // ===================== ＋ Add skill =====================

    /// <summary>Open the "Add skill" panel for the selected character.</summary>
    [RelayCommand]
    public async Task BeginAddSkillAsync()
    {
        if (SelectedCharacter == null || _maintenance == null) return;
        _suppressLibraryRebuild = true;
        LibraryFilter = string.Empty;
        _suppressLibraryRebuild = false;
        await RebuildAvailableSkillsAsync();
        SelectedLibrarySkill = null;
        AddSkillPinned = false;
        NewSkillName = NewSkillDescription = NewSkillInstructions = string.Empty;
        NewSkillIsProject = SelectedCharacter.Info.Scope == SkillScope.Project && _skills?.ProjectSkillsDirectory != null;
        AddSkillCreatesNew = AvailableSkills.Count == 0;
        IsAddingSkill = true;
    }

    [RelayCommand]
    public void CancelAddSkill() => IsAddingSkill = false;

    /// <summary>
    /// Add the picked library skill, or create the new skill and add it, to the selected character.
    /// The panel stays open (with the error in the status line) when something is wrong.
    /// </summary>
    [RelayCommand]
    public Task ConfirmAddSkillAsync()
    {
        if (SelectedCharacter == null || _maintenance == null) return Task.CompletedTask;

        string name;
        SkillDraft? draft = null;
        if (AddSkillCreatesNew)
        {
            name = ProjectKnowledgeViewModel.NormalizeSkillName(NewSkillName);
            if (string.IsNullOrEmpty(name))
            {
                StatusText = "Enter a name for the new skill (letters, numbers, hyphens).";
                return Task.CompletedTask;
            }
            if (string.IsNullOrWhiteSpace(NewSkillDescription))
            {
                StatusText = "Describe when the new skill should be used: the agent picks skills by their description.";
                return Task.CompletedTask;
            }
            draft = new SkillDraft
            {
                Name = name,
                Description = NewSkillDescription.Trim(),
                Body = string.IsNullOrWhiteSpace(NewSkillInstructions) ? null : NewSkillInstructions.Trim() + "\n"
            };
        }
        else
        {
            if (SelectedLibrarySkill == null)
            {
                StatusText = HasAvailableSkills ? "Pick a skill from the library list." : "The library has no other skills. Switch to \"Create new\".";
                return Task.CompletedTask;
            }
            name = SelectedLibrarySkill.Name;
        }

        var pinned = AddSkillPinned;
        var scope = NewSkillIsProject ? SkillScope.Project : SkillScope.Global;
        return Enqueue(async () =>
        {
            var ok = await WriteSelectedAsync(async id =>
            {
                var result = await _maintenance.AddSkillToCharacterAsync(id, name, pinned, draft, scope);
                return $"Added '{result.Skill.Name}' to '{id}'{(pinned ? " (pinned)" : "")}." +
                       (result.CreatedSkill ? $" Created it in the {result.Skill.Scope.ToString().ToLowerInvariant()} skill library." : "") +
                       (draft != null && !result.CreatedSkill ? " It already existed in the library, so the existing skill was used." : "") +
                       (result.WasAllSkills ? $" '{id}' now uses only the skills added to it." : "");
            });
            if (ok) IsAddingSkill = false;
        });
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
        NewIsTemplate = false;
        NewExtends = NoTemplate;
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
                BaseRole = string.IsNullOrWhiteSpace(NewBaseRole) ? null : NewBaseRole,
                Extends = NewExtends == NoTemplate || string.IsNullOrWhiteSpace(NewExtends) ? null : NewExtends,
                IsTemplate = NewIsTemplate
            }, NewIsProject ? SkillScope.Project : SkillScope.Global);
            IsCreating = false;
            await RefreshAsync();
            var item = Characters.FirstOrDefault(c => c.Id == created.Id);
            if (item != null) await SelectCharacterAsync(item);
            StatusText = created.IsTemplate
                ? $"Created template '{created.Id}'. Add its common skills with \"＋ Add skill\"; other characters can then extend it."
                : $"Created '{created.Id}'. Click \"＋ Add skill\" to give it its own skills.";
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
        var children = Characters.Where(x => string.Equals(x.Info.Extends, c.Id, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
        DeleteConfirmText = $"Delete character '{c.Id}' ({c.Scope.ToString().ToLowerInvariant()})? Its file will be removed." +
                            (c.Overrides ? " The definition it overrides will be used again." : "") +
                            (children.Count > 0 ? $" {string.Join(", ", children)} extend it and will lose what they inherit." : "");
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
