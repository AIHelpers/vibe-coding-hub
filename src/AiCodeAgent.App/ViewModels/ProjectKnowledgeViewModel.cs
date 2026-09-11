using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>Human-readable scope for display — "Project" / "Global", optionally noting manual-only invocation.</summary>
    public string ScopeLabel =>
        (IsProjectSkill ? "Project" : "Global") + (DisableModelInvocation ? " · manual only" : string.Empty);
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
    private int _activeTabIndex; // 0 = Memory, 1 = Skills

    /// <summary>True when the Memory tab should be shown — avoids needing an XAML value converter for a plain two-tab switch.</summary>
    public bool IsMemoryTabActive => ActiveTabIndex == 0;

    /// <summary>True when the Skills tab should be shown.</summary>
    public bool IsSkillsTabActive => ActiveTabIndex == 1;

    partial void OnActiveTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMemoryTabActive));
        OnPropertyChanged(nameof(IsSkillsTabActive));
    }

    [RelayCommand]
    private void ShowMemoryTab() => ActiveTabIndex = 0;

    [RelayCommand]
    private void ShowSkillsTab() => ActiveTabIndex = 1;

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

    public ObservableCollection<SkillListItem> Skills { get; } = new();

    [ObservableProperty]
    private SkillListItem? _selectedSkill;

    [ObservableProperty]
    private string _selectedSkillContent = string.Empty;

    [ObservableProperty]
    private bool _selectedSkillIsDirty;

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

    public ProjectKnowledgeViewModel(
        IProjectMemoryLoader? memoryLoader = null,
        ISkillRegistry? skillRegistry = null,
        AgentService? agentService = null,
        ILogger<ProjectKnowledgeViewModel>? logger = null)
    {
        _memoryLoader = memoryLoader;
        _skillRegistry = skillRegistry;
        _agentService = agentService;
        _logger = logger;
    }

    /// <summary>Opens the panel for the given working directory and loads both tabs.</summary>
    public async Task OpenAsync(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        IsVisible = true;
        await RefreshMemoryAsync();
        await RefreshSkillsAsync();
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
        Skills.Clear();
        if (_skillRegistry == null)
        {
            SkillsStatusText = "Skill registry not available.";
            return;
        }

        try
        {
            _skillRegistry.Refresh();
            var skills = await _skillRegistry.ListAsync();
            var projectDir = _skillRegistry.ProjectSkillsDirectory;

            foreach (var s in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isProject = !string.IsNullOrEmpty(projectDir) && s.FilePath != null &&
                                 s.FilePath.StartsWith(projectDir!, StringComparison.OrdinalIgnoreCase);
                Skills.Add(new SkillListItem
                {
                    Name = s.Name,
                    Description = s.Description,
                    FilePath = s.FilePath ?? string.Empty,
                    IsProjectSkill = isProject,
                    DisableModelInvocation = s.DisableModelInvocation
                });
            }
            SkillsStatusText = Skills.Count == 0
                ? "No skills yet — click \"New Skill\" to create one."
                : $"{Skills.Count} skill(s)";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list skills");
            SkillsStatusText = $"Failed to list skills: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SelectSkillAsync(SkillListItem? item)
    {
        SelectedSkill = item;
        SelectedSkillIsDirty = false;
        if (item == null || string.IsNullOrEmpty(item.FilePath))
        {
            SelectedSkillContent = string.Empty;
            return;
        }

        try
        {
            SelectedSkillContent = await File.ReadAllTextAsync(item.FilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read skill file {Path}", item.FilePath);
            SelectedSkillContent = string.Empty;
            SkillsStatusText = $"Failed to read skill: {ex.Message}";
        }
    }

    partial void OnSelectedSkillContentChanged(string value) => SelectedSkillIsDirty = true;

    [RelayCommand]
    public async Task SaveSelectedSkillAsync()
    {
        if (SelectedSkill == null || string.IsNullOrEmpty(SelectedSkill.FilePath))
            return;

        try
        {
            await File.WriteAllTextAsync(SelectedSkill.FilePath, SelectedSkillContent);
            SelectedSkillIsDirty = false;
            SkillsStatusText = $"Saved {SelectedSkill.Name}";
            _skillRegistry?.Refresh();
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
    /// Writes a new <c>&lt;dir&gt;/&lt;name&gt;/SKILL.md</c> with proper
    /// frontmatter, refreshes the registry so it's immediately visible and
    /// invocable, and selects it for editing.
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

        var baseDir = NewSkillIsProject
            ? (_skillRegistry?.ProjectSkillsDirectory ?? Path.Combine(WorkingDirectory, ".aiagent", "skills"))
            : (_skillRegistry?.GlobalSkillsDirectory ?? SkillDirectoryFallback());

        var skillDir = Path.Combine(baseDir, name);
        var skillFile = Path.Combine(skillDir, "SKILL.md");

        if (File.Exists(skillFile))
        {
            SkillsStatusText = $"A skill named '{name}' already exists.";
            return;
        }

        try
        {
            Directory.CreateDirectory(skillDir);
            var description = string.IsNullOrWhiteSpace(NewSkillDescription)
                ? "Describe when to use this skill in one line."
                : NewSkillDescription.Trim();

            var template =
                $"""
                ---
                name: {name}
                description: {description}
                ---

                # {name}

                Write the skill's instructions here — the full content of this file is
                injected into the agent's context when the skill is invoked (via
                "/skill {name}" in the CLI, or automatically when the model decides
                it's relevant, unless disable-model-invocation is set above).

                ## When to use this skill

                -

                ## Steps

                1.
                """;

            await File.WriteAllTextAsync(skillFile, template);
            _skillRegistry?.Refresh();
            IsCreatingSkill = false;
            await RefreshSkillsAsync();

            var created = Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (created != null)
                await SelectSkillAsync(created);

            SkillsStatusText = $"Created skill '{name}'";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create skill {Name}", name);
            SkillsStatusText = $"Failed to create skill: {ex.Message}";
        }
    }

    private static string SkillDirectoryFallback()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = string.IsNullOrEmpty(home) ? ".aiagent" : Path.Combine(home, ".aiagent");
        return Path.Combine(dir, "skills");
    }

    /// <summary>Kebab-cases a skill name and strips anything that isn't a letter, digit, or hyphen.</summary>
    private static string NormalizeSkillName(string input)
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
