using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.App.Services;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One row in the skill list.</summary>
public sealed class SkillListItem
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string FilePath { get; init; }
    public bool IsProjectSkill { get; init; }
    public bool DisableModelInvocation { get; init; }
    public bool ShadowsGlobal { get; init; }
    public bool IsValid { get; init; } = true;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    /// <summary>Characters (ids) that list or pin this skill.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();

    /// <summary>Human-readable scope for display — "Project" / "Global", plus manual-only / override / invalid / usage notes.</summary>
    public string ScopeLabel =>
        (IsProjectSkill ? "Project" : "Global")
        + (DisableModelInvocation ? " · manual only" : string.Empty)
        + (ShadowsGlobal ? " · overrides global" : string.Empty)
        + (IsValid ? string.Empty : " · invalid")
        + (UsedBy.Count > 0 ? $" · used by {UsedBy.Count}" : string.Empty);

    public string TagsLabel => Tags.Count == 0 ? string.Empty : "#" + string.Join(" #", Tags);
}

/// <summary>
/// Powers the "Project Knowledge" panel: view/edit the project's memory
/// file (AGENTS.md / AGENT.md / AIAGENT.md — see
/// <see cref="ProjectMemoryLoader.CandidateFileNames"/>), run the same
/// diagnostics the CLI's <c>/doctor</c> command exposes, and browse, view,
/// or create custom skill <c>SKILL.md</c> files — all without leaving the
/// desktop app. The CLI has had equivalent commands (<c>/init</c>,
/// <c>/doctor</c>, <c>/memory</c>, <c>/skills</c>, <c>/skill</c>) for a
/// while; this is the GUI counterpart.
/// </summary>
public partial class ProjectKnowledgeViewModel : ObservableObject
{
    private readonly IProjectMemoryLoader? _memoryLoader;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly AgentService? _agentService;
    private readonly ILogger<ProjectKnowledgeViewModel>? _logger;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _activeTabIndex; // 0 = Memory, 1 = Skills, 2 = Characters

    /// <summary>True when the Memory tab should be shown — avoids needing an XAML value converter for a plain two-tab switch.</summary>
    public bool IsMemoryTabActive => ActiveTabIndex == 0;

    /// <summary>True when the Skills tab should be shown.</summary>
    public bool IsSkillsTabActive => ActiveTabIndex == 1;

    /// <summary>True when the Characters tab should be shown.</summary>
    public bool IsCharactersTabActive => ActiveTabIndex == 2;

    partial void OnActiveTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMemoryTabActive));
        OnPropertyChanged(nameof(IsSkillsTabActive));
        OnPropertyChanged(nameof(IsCharactersTabActive));
    }

    [RelayCommand]
    private void ShowMemoryTab() => ActiveTabIndex = 0;

    [RelayCommand]
    private void ShowSkillsTab() => ActiveTabIndex = 1;

    [RelayCommand]
    private async Task ShowCharactersTabAsync()
    {
        ActiveTabIndex = 2;
        await Characters.RefreshAsync();
    }

    /// <summary>The Characters tab (agent personas with their own skills).</summary>
    public CharactersViewModel Characters { get; }

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    // ---- Memory tab ----

    [ObservableProperty]
    private string _memoryFilePath = string.Empty;

    [ObservableProperty]
    private string _memoryContent = string.Empty;

    [ObservableProperty]
    private bool _memoryFileExists;

    [ObservableProperty]
    private bool _memoryIsDirty;

    [ObservableProperty]
    private string _memoryStatusText = string.Empty;

    public ObservableCollection<DoctorCheck> DoctorChecks { get; } = new();

    // ---- Skills tab ----

    /// <summary>Skills shown in the list (after the search filter).</summary>
    public ObservableCollection<SkillListItem> Skills { get; } = new();

    private List<SkillListItem> _allSkills = new();

    [ObservableProperty]
    private string _skillFilter = string.Empty;

    partial void OnSkillFilterChanged(string value) => ApplySkillFilter();

    [ObservableProperty]
    private SkillListItem? _selectedSkill;

    [ObservableProperty]
    private string _selectedSkillContent = string.Empty;

    [ObservableProperty]
    private bool _selectedSkillIsDirty;

    /// <summary>Validation errors/warnings and character usage of the selected skill.</summary>
    [ObservableProperty]
    private string _selectedSkillDetails = string.Empty;

    [ObservableProperty]
    private string _skillsStatusText = string.Empty;

    // New-skill creation form
    [ObservableProperty]
    private bool _isCreatingSkill;

    [ObservableProperty]
    private string _newSkillName = string.Empty;

    [ObservableProperty]
    private string _newSkillDescription = string.Empty;

    [ObservableProperty]
    private bool _newSkillIsProject = true;

    // Rename form
    [ObservableProperty]
    private bool _isRenamingSkill;

    [ObservableProperty]
    private string _renameSkillName = string.Empty;

    // Inline delete confirmation
    [ObservableProperty]
    private bool _isConfirmingSkillDelete;

    [ObservableProperty]
    private string _skillDeleteConfirmText = string.Empty;

    /// <summary>When true (default), deleting a skill also removes it from the characters that use it.</summary>
    [ObservableProperty]
    private bool _removeSkillFromCharacters = true;

    public bool HasSelectedSkill => SelectedSkill != null;

    partial void OnSelectedSkillChanged(SkillListItem? value) => OnPropertyChanged(nameof(HasSelectedSkill));

    private readonly ICharacterRegistry? _characterRegistry;
    private readonly SkillMaintenance? _maintenance;

    public ProjectKnowledgeViewModel(
        IProjectMemoryLoader? memoryLoader = null,
        ISkillRegistry? skillRegistry = null,
        AgentService? agentService = null,
        ILogger<ProjectKnowledgeViewModel>? logger = null,
        ICharacterRegistry? characterRegistry = null,
        SkillMaintenance? maintenance = null,
        CharactersViewModel? characters = null)
    {
        _memoryLoader = memoryLoader;
        _skillRegistry = skillRegistry;
        _agentService = agentService;
        _logger = logger;
        _characterRegistry = characterRegistry;
        _maintenance = maintenance ?? (skillRegistry != null && characterRegistry != null ? new SkillMaintenance(skillRegistry, characterRegistry) : null);
        Characters = characters ?? new CharactersViewModel(characterRegistry, skillRegistry);

        // Files edited elsewhere (editor, git) show up live while the panel is open.
        if (_skillRegistry != null)
            _skillRegistry.Changed += (_, _) => OnRegistryChanged(skills: true);
        if (_characterRegistry != null)
            _characterRegistry.Changed += (_, _) => OnRegistryChanged(skills: false);
    }

    private void OnRegistryChanged(bool skills)
    {
        if (!IsVisible) return;
        void Run()
        {
            if (skills) _ = RefreshSkillsAsync();
            else if (IsCharactersTabActive) _ = Characters.RefreshAsync();
        }
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Run();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Run);
        }
        catch
        {
            // No UI dispatcher (tests): refresh on next open instead.
        }
    }

    /// <summary>Opens the panel for the given working directory and loads all tabs.</summary>
    public async Task OpenAsync(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        IsVisible = true;
        await RefreshMemoryAsync();
        await RefreshSkillsAsync();
        await Characters.RefreshAsync();
    }

    [RelayCommand]
    public void Close() => IsVisible = false;

    // ================= Memory =================

    [RelayCommand]
    public async Task RefreshMemoryAsync()
    {
        if (_memoryLoader == null || string.IsNullOrEmpty(WorkingDirectory))
            return;

        try
        {
            var memory = await _memoryLoader.LoadAsync(WorkingDirectory);
            if (memory != null)
            {
                MemoryFilePath = memory.FilePath;
                MemoryContent = memory.RawContent;
                MemoryFileExists = true;
                MemoryStatusText = $"Loaded {Path.GetFileName(memory.FilePath)}";
            }
            else
            {
                MemoryFilePath = Path.Combine(WorkingDirectory, "AGENTS.md");
                MemoryContent = string.Empty;
                MemoryFileExists = false;
                MemoryStatusText = "No AGENTS.md / AGENT.md / AIAGENT.md found yet — click \"Create\" to add one.";
            }
            MemoryIsDirty = false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load project memory for {Dir}", WorkingDirectory);
            MemoryStatusText = $"Failed to load: {ex.Message}";
        }

        await RunDoctorAsync();
    }

    partial void OnMemoryContentChanged(string value) => MemoryIsDirty = true;

    /// <summary>Creates AGENTS.md via the loader's template when none exists yet.</summary>
    [RelayCommand]
    public async Task CreateMemoryFileAsync()
    {
        if (_memoryLoader == null || string.IsNullOrEmpty(WorkingDirectory))
            return;

        try
        {
            var path = await _memoryLoader.InitAsync(WorkingDirectory);
            await RefreshMemoryAsync();
            if (_agentService != null)
                await _agentService.RefreshProjectMemoryAsync(WorkingDirectory);
            MemoryStatusText = $"Created {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create project memory file");
            MemoryStatusText = $"Failed to create: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveMemoryAsync()
    {
        if (string.IsNullOrEmpty(MemoryFilePath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(MemoryFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(MemoryFilePath, MemoryContent);
            MemoryFileExists = true;
            MemoryIsDirty = false;
            MemoryStatusText = $"Saved {Path.GetFileName(MemoryFilePath)}";
            await RunDoctorAsync();
            // Push the edit into the live agent session immediately — without
            // this, the next chat turn would still use the stale cached copy
            // AgentService loaded earlier for this same working directory.
            if (_agentService != null)
                await _agentService.RefreshProjectMemoryAsync(WorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save project memory file");
            MemoryStatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RunDoctorAsync()
    {
        DoctorChecks.Clear();
        if (_memoryLoader == null || string.IsNullOrEmpty(WorkingDirectory))
            return;

        try
        {
            var report = await _memoryLoader.DiagnoseAsync(WorkingDirectory);
            foreach (var check in report.Checks)
                DoctorChecks.Add(check);
            if (_skillRegistry != null)
            {
                foreach (var check in await SkillDoctor.DiagnoseAsync(_skillRegistry, _characterRegistry))
                    DoctorChecks.Add(check);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run project doctor checks");
        }
    }

    // ================= Skills =================

    [RelayCommand]
    public async Task RefreshSkillsAsync()
    {
        if (_skillRegistry == null)
        {
            Skills.Clear();
            _allSkills.Clear();
            SkillsStatusText = "Skill registry not available.";
            return;
        }

        try
        {
            _skillRegistry.Refresh();
            var skills = await _skillRegistry.ListAllAsync();
            var items = new List<SkillListItem>();
            foreach (var s in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var usedBy = _characterRegistry == null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : (await _characterRegistry.FindSkillReferencesAsync(s.Name)).Select(r => r.CharacterId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                items.Add(new SkillListItem
                {
                    Name = s.Name,
                    Description = s.Description,
                    FilePath = s.FilePath ?? string.Empty,
                    IsProjectSkill = s.Scope == SkillScope.Project,
                    DisableModelInvocation = s.DisableModelInvocation,
                    ShadowsGlobal = s.ShadowsGlobal,
                    IsValid = s.IsValid,
                    Tags = s.Tags,
                    Problems = s.ValidationErrors.Select(e => "Error: " + e).Concat(s.Warnings.Select(w => "Warning: " + w)).ToList(),
                    UsedBy = usedBy
                });
            }
            _allSkills = items;
            ApplySkillFilter();

            // Keep the selection (and unsaved edits) across refreshes.
            if (SelectedSkill != null)
            {
                var again = _allSkills.FirstOrDefault(s => s.Name == SelectedSkill.Name);
                if (again == null)
                {
                    SelectedSkill = null;
                    SelectedSkillContent = string.Empty;
                    SelectedSkillIsDirty = false;
                    SelectedSkillDetails = string.Empty;
                }
                else
                {
                    SelectedSkill = again;
                    UpdateSelectedSkillDetails();
                }
            }

            SkillsStatusText = _allSkills.Count == 0
                ? "No skills yet — click \"New Skill\" to create one."
                : $"{_allSkills.Count} skill(s)";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list skills");
            SkillsStatusText = $"Failed to list skills: {ex.Message}";
        }
    }

    /// <summary>Filter the list by name, description or tag (case-insensitive substring).</summary>
    private void ApplySkillFilter()
    {
        var filter = SkillFilter.Trim().TrimStart('#');
        Skills.Clear();
        foreach (var item in _allSkills)
        {
            if (filter.Length == 0
                || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                Skills.Add(item);
        }
    }

    private void UpdateSelectedSkillDetails()
    {
        if (SelectedSkill == null) { SelectedSkillDetails = string.Empty; return; }
        var lines = new List<string>();
        lines.Add(SelectedSkill.UsedBy.Count == 0 ? "Not used by any character." : "Used by: " + string.Join(", ", SelectedSkill.UsedBy));
        lines.AddRange(SelectedSkill.Problems);
        SelectedSkillDetails = string.Join("\n", lines);
    }

    [RelayCommand]
    public async Task SelectSkillAsync(SkillListItem? item)
    {
        IsConfirmingSkillDelete = false;
        IsRenamingSkill = false;
        SelectedSkill = item;
        SelectedSkillIsDirty = false;
        UpdateSelectedSkillDetails();
        if (item == null || string.IsNullOrEmpty(item.FilePath))
        {
            SelectedSkillContent = string.Empty;
            SelectedSkillIsDirty = false;
            return;
        }

        try
        {
            SelectedSkillContent = await File.ReadAllTextAsync(item.FilePath);
            SelectedSkillIsDirty = false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read skill file {Path}", item.FilePath);
            SelectedSkillContent = string.Empty;
            SkillsStatusText = $"Failed to read skill: {ex.Message}";
        }
    }

    partial void OnSelectedSkillContentChanged(string value) => SelectedSkillIsDirty = true;

    /// <summary>Saves the editor content through the registry, which validates frontmatter before writing.</summary>
    [RelayCommand]
    public async Task SaveSelectedSkillAsync()
    {
        if (SelectedSkill == null || string.IsNullOrEmpty(SelectedSkill.FilePath))
            return;

        try
        {
            if (_skillRegistry != null)
            {
                await _skillRegistry.UpdateAsync(SelectedSkill.Name, SelectedSkillContent,
                    SelectedSkill.IsProjectSkill ? SkillScope.Project : SkillScope.Global);
            }
            else
            {
                await File.WriteAllTextAsync(SelectedSkill.FilePath, SelectedSkillContent);
            }
            SelectedSkillIsDirty = false;
            await RefreshSkillsAsync();
            SkillsStatusText = $"Saved {SelectedSkill?.Name}";
        }
        catch (SkillValidationException ex)
        {
            SkillsStatusText = "Not saved: " + string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save skill {Name}", SelectedSkill.Name);
            SkillsStatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void BeginCreateSkill()
    {
        NewSkillName = string.Empty;
        NewSkillDescription = string.Empty;
        NewSkillIsProject = true;
        IsCreatingSkill = true;
    }

    [RelayCommand]
    public void CancelCreateSkill() => IsCreatingSkill = false;

    /// <summary>
    /// Creates <c>&lt;dir&gt;/&lt;name&gt;/SKILL.md</c> through the registry,
    /// so it is immediately visible and invocable, and selects it for editing.
    /// </summary>
    [RelayCommand]
    public async Task CreateSkillAsync()
    {
        var name = NormalizeSkillName(NewSkillName);
        if (string.IsNullOrEmpty(name))
        {
            SkillsStatusText = "Skill name is required (letters, numbers, hyphens).";
            return;
        }
        if (_skillRegistry == null)
        {
            SkillsStatusText = "Skill registry not available.";
            return;
        }

        var description = string.IsNullOrWhiteSpace(NewSkillDescription)
            ? "Describe when to use this skill in one line."
            : NewSkillDescription.Trim();

        try
        {
            await _skillRegistry.CreateAsync(new SkillDraft
            {
                Name = name,
                Description = description,
                Body = SkillTemplate(name)
            }, NewSkillIsProject ? SkillScope.Project : SkillScope.Global);

            IsCreatingSkill = false;
            await RefreshSkillsAsync();
            var created = _allSkills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (created != null)
                await SelectSkillAsync(created);
            SkillsStatusText = $"Created skill '{name}'";
        }
        catch (SkillValidationException ex)
        {
            SkillsStatusText = string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create skill {Name}", name);
            SkillsStatusText = $"Failed to create skill: {ex.Message}";
        }
    }

    private static string SkillTemplate(string name) =>
        $"""
        # {name}

        Write the skill's instructions here. The agent sees the description above in its
        skill list and loads these instructions with the use_skill tool when a task matches
        (or you can send them yourself with "/skill {name}").

        ## When to use this skill

        -

        ## Steps

        1.
        """;

    // ---- Rename ----

    [RelayCommand]
    public void BeginRenameSkill()
    {
        if (SelectedSkill == null) return;
        IsConfirmingSkillDelete = false;
        RenameSkillName = SelectedSkill.Name;
        IsRenamingSkill = true;
    }

    [RelayCommand]
    public void CancelRenameSkill() => IsRenamingSkill = false;

    /// <summary>Renames the skill folder and frontmatter, and updates every character that uses it.</summary>
    [RelayCommand]
    public async Task ConfirmRenameSkillAsync()
    {
        if (SelectedSkill == null || _skillRegistry == null) return;
        var oldName = SelectedSkill.Name;
        var newName = NormalizeSkillName(RenameSkillName);
        var scope = SelectedSkill.IsProjectSkill ? SkillScope.Project : SkillScope.Global;
        try
        {
            int updated;
            if (_maintenance != null)
                updated = (await _maintenance.RenameAsync(oldName, newName, scope)).CharactersUpdated;
            else
            {
                await _skillRegistry.RenameAsync(oldName, newName, scope);
                updated = 0;
            }
            IsRenamingSkill = false;
            SelectedSkill = null;
            await RefreshSkillsAsync();
            var item = _allSkills.FirstOrDefault(s => s.Name == newName);
            if (item != null) await SelectSkillAsync(item);
            SkillsStatusText = $"Renamed '{oldName}' to '{newName}'" + (updated > 0 ? $"; updated {updated} character(s)." : ".");
        }
        catch (SkillValidationException ex)
        {
            SkillsStatusText = string.Join(" ", ex.Errors);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to rename skill {Name}", oldName);
            SkillsStatusText = $"Rename failed: {ex.Message}";
        }
    }

    // ---- Delete ----

    [RelayCommand]
    public void RequestDeleteSkill()
    {
        if (SelectedSkill == null) return;
        IsRenamingSkill = false;
        var usedBy = SelectedSkill.UsedBy.Count == 0 ? string.Empty : $" It is used by: {string.Join(", ", SelectedSkill.UsedBy)}.";
        SkillDeleteConfirmText = $"Delete skill '{SelectedSkill.Name}' ({(SelectedSkill.IsProjectSkill ? "project" : "global")})? Its folder will be removed.{usedBy}";
        RemoveSkillFromCharacters = true;
        IsConfirmingSkillDelete = true;
    }

    [RelayCommand]
    public void CancelDeleteSkill() => IsConfirmingSkillDelete = false;

    [RelayCommand]
    public async Task ConfirmDeleteSkillAsync()
    {
        IsConfirmingSkillDelete = false;
        if (SelectedSkill == null || _skillRegistry == null) return;
        var name = SelectedSkill.Name;
        var scope = SelectedSkill.IsProjectSkill ? SkillScope.Project : SkillScope.Global;
        try
        {
            string message;
            if (_maintenance != null)
            {
                var result = await _maintenance.DeleteAsync(name, scope, RemoveSkillFromCharacters);
                message = !result.Deleted ? $"Nothing deleted for '{name}'."
                    : $"Deleted '{name}'." + (result.CharactersUpdated > 0 ? $" Removed it from {result.CharactersUpdated} character(s)." : "");
            }
            else
            {
                message = await _skillRegistry.DeleteAsync(name, scope) ? $"Deleted '{name}'." : $"Nothing deleted for '{name}'.";
            }
            SelectedSkill = null;
            SelectedSkillContent = string.Empty;
            SelectedSkillIsDirty = false;
            await RefreshSkillsAsync();
            SkillsStatusText = message;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete skill {Name}", name);
            SkillsStatusText = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>Kebab-cases a skill name and strips anything that isn't a letter, digit, or hyphen.</summary>
    internal static string NormalizeSkillName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var lowered = input.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        var chars = lowered.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        var result = new string(chars).Trim('-');
        while (result.Contains("--"))
            result = result.Replace("--", "-");
        return result;
    }
}
