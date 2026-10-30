using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The Flows panel: draw a flow of characters on a canvas (nodes + arrows), edit each node and
/// edge, validate as you go, auto-layout, undo/redo, save it as a pipeline, and run it with live
/// per-node status. Pure state and logic; pointer handling lives in <c>FlowsView</c>.
/// </summary>
public partial class FlowEditorViewModel : ObservableObject
{
    public const double GridSize = 10;
    public const string AlwaysCondition = "(always)";

    private readonly SdlcPipelineLoader? _loader;
    private readonly SdlcPipelineRunner? _runner;
    private readonly ICharacterRegistry? _characters;
    private readonly ILogger<FlowEditorViewModel>? _logger;
    private readonly FlowRunStore? _runStore;
    private readonly ISkillRegistry? _skills;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private bool _restoring;
    private string? _lastEditKey;
    private CancellationTokenSource? _runCts;
    private Dictionary<string, CharacterInfo> _characterById = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FlowNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<FlowEdgeViewModel> Edges { get; } = new();
    public ObservableCollection<FlowPaletteItem> Palette { get; } = new();
    public ObservableCollection<string> Pipelines { get; } = new();
    /// <summary>The cards of the template gallery.</summary>
    public ObservableCollection<FlowTemplateCardViewModel> Templates { get; } = new();

    /// <summary>The bundled example flows, characters and skills the gallery installs from.</summary>
    public ExampleLibrary Examples { get; set; } = ExampleLibrary.FromEmbedded();

    /// <summary>The dropdown entries with their scope, grouped project, then global, then built-in.</summary>
    public ObservableCollection<PipelineListItem> PipelineItems { get; } = new();
    public ObservableCollection<FlowProblem> Problems { get; } = new();
    /// <summary>Choices for the selected edge's condition: "(always)", the source's outcomes, and "loop-exhausted" when it applies.</summary>
    public ObservableCollection<string> ConditionOptions { get; } = new();
    public ObservableCollection<string> CharacterOptions { get; } = new();
    /// <summary>Entries of the inspector's Model combo: inherit, the tiers, then the catalog's models.</summary>
    public ObservableCollection<string> ModelOptions { get; } = new(BuildModelOptions());
    /// <summary>Entries of the inspector's Provider combo: the session's provider, then the catalog's providers.</summary>
    public ObservableCollection<string> ProviderOptions { get; } = new(BuildProviderOptions());

    /// <summary>The tier aliases a step's <c>model</c> accepts.</summary>
    public static readonly string[] ModelTiers = { "fast", "balanced", "frontier" };

    private static IEnumerable<string> BuildModelOptions()
    {
        var options = new List<string> { FlowNodeViewModel.InheritModelLabel };
        options.AddRange(ModelTiers);
        try
        {
            options.AddRange(ModelCatalogLoader.Default.Models.Where(m => !m.Retired).Select(m => m.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !options.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }
        catch { /* no catalog: tiers and inherit still work */ }
        return options;
    }

    private static IEnumerable<string> BuildProviderOptions()
    {
        var options = new List<string> { FlowNodeViewModel.InheritProviderLabel };
        try
        {
            options.AddRange(ModelCatalogLoader.Default.Models.Select(m => m.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch { /* no catalog */ }
        return options;
    }

    /// <summary>A model or provider typed into a flow file by hand must show in its combo.</summary>
    private static void EnsureOption(ObservableCollection<string> options, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
            options.Add(value);
    }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _flowName = "new-flow";
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private int _maxParallel = FlowRunner.DefaultMaxParallel;
    /// <summary>Spend cap in USD as typed next to ▶ Run ("" = none).</summary>
    [ObservableProperty] private string _budgetText = string.Empty;
    /// <summary>Header text with the run total so far: "Run: 5.4k tok · $0.021 of $1.00".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunUsage))]
    private string _runUsageText = string.Empty;
    [ObservableProperty] private string? _selectedPipeline;
    [ObservableProperty] private FlowNodeViewModel? _selectedNode;
    [ObservableProperty] private FlowEdgeViewModel? _selectedEdge;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _paletteFilter = string.Empty;
    [ObservableProperty] private string _runTask = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _workingDirectory = Environment.CurrentDirectory;
    /// <summary>The loaded pipeline was linear (no edges); saving writes edges and turns it into a flow.</summary>
    [ObservableProperty] private bool _wasLinear;
    [ObservableProperty] private bool _isValid = true;
    [ObservableProperty] private string _wavesText = string.Empty;
    [ObservableProperty] private string _selectedCondition = AlwaysCondition;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _deletePrompt = string.Empty;

    /// <summary>The template gallery (＋ New) is showing instead of the canvas.</summary>
    [ObservableProperty] private bool _isGalleryOpen;

    /// <summary>The entry picked in the pipeline dropdown (kept in step with <see cref="SelectedPipeline"/>).</summary>
    [ObservableProperty] private PipelineListItem? _selectedPipelineItem;

    /// <summary>Where the open flow comes from (builtin, global or project); null for a new, unsaved flow.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeBadge))]
    private PipelineScope? _loadedScope;

    /// <summary>"overrides global" / "overrides built-in" when the open flow hides another one of the same name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverridesText))]
    private string _overridesText = string.Empty;

    /// <summary>Save was pressed for a flow that has no file yet: waiting for the choice Project / Global.</summary>
    [ObservableProperty] private bool _isChoosingSaveScope;

    /// <summary>Name of the flow that was last opened or saved; the open flow is saved back into its own file only while the name is unchanged.</summary>
    private string? _loadedName;

    /// <summary>While dragging a new arrow: where its loose end is (canvas coordinates).</summary>
    [ObservableProperty] private FlowNodeViewModel? _pendingEdgeSource;
    [ObservableProperty] private double _pendingEdgeX;
    [ObservableProperty] private double _pendingEdgeY;

    private readonly Dictionary<string, TaskCompletionSource<bool>> _approvals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApprovalFeedback> _gateFeedback = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<FlowHumanChoice>> _humanChoices = new(StringComparer.OrdinalIgnoreCase);

    public FlowEditorViewModel(
        SdlcPipelineLoader? loader = null,
        SdlcPipelineRunner? runner = null,
        ICharacterRegistry? characters = null,
        ILogger<FlowEditorViewModel>? logger = null,
        ISkillRegistry? skills = null,
        FlowRunStore? runStore = null)
    {
        _runStore = runStore;
        _skills = skills;
        _loader = loader;
        _runner = runner;
        _characters = characters;
        _logger = logger;
        if (_loader != null)
            _loader.Changed += (_, _) => RefreshPipelineListSafe();
    }

    public bool HasSelection => SelectedNode != null || SelectedEdge != null;

    /// <summary>The canvas cannot be edited: a run is in progress, or a past run is open for replay.</summary>
    public bool IsLocked => IsRunning || IsReplay || IsRemote;

    /// <summary><c>builtin</c>, <c>global</c> or <c>project</c> for the open flow; <c>new</c> before it is saved.</summary>
    public string ScopeBadge => LoadedScope?.Label() ?? "new";
    public bool HasOverridesText => OverridesText.Length > 0;
    /// <summary>True when a project folder is set, so a flow can be saved either into the project or globally.</summary>
    public bool CanChooseSaveScope => _loader?.ProjectDirectory != null;
    /// <summary>Project when the folder is a git repository (the flow is then shared with the team), otherwise global.</summary>
    public PipelineScope DefaultSaveScope => _loader?.DefaultSaveScope ?? PipelineScope.Global;
    public bool IsProjectSaveDefault => DefaultSaveScope == PipelineScope.Project;

    /// <summary>The working directory changed: flows in its <c>.aiagent/pipelines</c> are listed next to the global ones.</summary>
    public void SetProjectDirectory(string? directory)
    {
        // The loader raises Changed, which would post a second refresh; we refresh right below, so skip that one
        // (two refreshes running at once could modify the lists while the caller reads them).
        _suppressChangedRefresh = true;
        try { _loader?.SetProjectDirectory(directory); }
        finally { _suppressChangedRefresh = false; }
        RefreshPipelineList();
    }

    private bool _suppressChangedRefresh;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public bool CanRun => !IsRunning && !IsRemote && Nodes.Count > 0 && IsValid && _runner != null;
    public string PendingEdgePath => PendingEdgeSource == null
        ? string.Empty
        : $"M {PendingEdgeSource.OutPortX:0},{PendingEdgeSource.OutPortY:0} L {PendingEdgeX:0},{PendingEdgeY:0}";
    public bool IsDrawingEdge => PendingEdgeSource != null;

    /// <summary>The selected node's live output, or the last finished run's summary.</summary>
    public string SelectedOutput => IsReplay && SelectedNode != null ? ReplayMarkdown(SelectedNode.Id) : SelectedNode?.OutputText ?? string.Empty;

    /// <summary>Raised by "Open in chat": (title, Markdown). The host shows it in the chat.</summary>
    public event Action<string, string>? OpenInChatRequested;

    [RelayCommand]
    private void OpenSelectedOutputInChat()
    {
        if (SelectedNode is { } node && !string.IsNullOrWhiteSpace(node.OutputText))
            OpenInChatRequested?.Invoke($"Flow step: {node.Name}", node.OutputText.Trim());
    }

    partial void OnSelectedNodeChanged(FlowNodeViewModel? oldValue, FlowNodeViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;
            SelectedEdge = null;
        }
        _lastEditKey = null;
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedOutput));
    }

    partial void OnSelectedEdgeChanged(FlowEdgeViewModel? oldValue, FlowEdgeViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;
            SelectedNode = null;
        }
        RebuildConditionOptions();
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnSelectedConditionChanged(string value)
    {
        if (_restoring || SelectedEdge == null) return;
        var when = value == AlwaysCondition || string.IsNullOrEmpty(value) ? null : value;
        if (string.Equals(SelectedEdge.When, when, StringComparison.Ordinal)) return;
        Snapshot();
        SelectedEdge.When = when;
        Changed();
    }

    partial void OnPendingEdgeSourceChanged(FlowNodeViewModel? value)
    {
        OnPropertyChanged(nameof(IsDrawingEdge));
        OnPropertyChanged(nameof(PendingEdgePath));
    }

    partial void OnPendingEdgeXChanged(double value) => OnPropertyChanged(nameof(PendingEdgePath));
    partial void OnPendingEdgeYChanged(double value) => OnPropertyChanged(nameof(PendingEdgePath));
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(IsLocked));
        RerunFromHereCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnPaletteFilterChanged(string value) => RebuildPalette();
    partial void OnFlowNameChanged(string value) => MarkDirtyFromHeader();
    partial void OnDescriptionChanged(string value) => MarkDirtyFromHeader();
    partial void OnMaxParallelChanged(int value) => MarkDirtyFromHeader();
    partial void OnBudgetTextChanged(string value) => MarkDirtyFromHeader();

    public bool HasRunUsage => RunUsageText.Length > 0;
    public bool HasUsageRows => UsageRows.Count > 0;

    /// <summary>The step runs of the last finished flow run (step, round, model, tokens, cost, time) and a total line.</summary>
    public ObservableCollection<FlowUsageRowViewModel> UsageRows { get; } = new();

    /// <summary>The budget cap typed next to ▶ Run; null when empty, zero or not a number.</summary>
    public decimal? BudgetUsd =>
        decimal.TryParse((BudgetText ?? string.Empty).Trim().TrimStart('$').Replace(',', '.'), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private void MarkDirtyFromHeader()
    {
        if (_restoring) return;
        IsDirty = true;
    }

    // ============================================================ open / list

    /// <summary>Show the panel: load characters and pipelines; open the given (or first flow) pipeline.</summary>
    public async Task OpenAsync(string? workingDirectory = null, string? pipelineName = null)
    {
        if (!string.IsNullOrEmpty(workingDirectory)) WorkingDirectory = workingDirectory;
        IsVisible = true;
        await RefreshCharactersAsync();
        RefreshPipelineList();
        if (pipelineName != null)
            Load(pipelineName);
        else if (Nodes.Count == 0)
        {
            var firstFlow = _loader?.GetAllPipelines().FirstOrDefault(p => p.IsFlow);
            if (firstFlow != null) Load(firstFlow.Name);
            else NewFlow();
        }
    }

    [RelayCommand]
    public void Close() => IsVisible = false;

    // ============================================================ template gallery

    /// <summary>＋ New: show the gallery of ready-made flows (with a "Blank flow" card).</summary>
    [RelayCommand]
    public async Task OpenGalleryAsync()
    {
        if (IsRunning) return;
        LeaveReplay(restore: true);
        await RefreshCharactersAsync();
        await RebuildTemplatesAsync();
        IsGalleryOpen = true;
        StatusText = "Pick a template to copy into your project, or start with a blank flow.";
    }

    [RelayCommand]
    public void CloseGallery()
    {
        IsGalleryOpen = false;
        foreach (var card in Templates) card.IsConfirming = false;
    }

    [RelayCommand]
    public void StartBlankFlow()
    {
        CloseGallery();
        NewFlow();
    }

    private async Task RebuildTemplatesAsync()
    {
        var existing = _characterById.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? existingSkills = null;
        if (_skills != null)
        {
            try { existingSkills = (await _skills.ListAllAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not list skills for the template gallery"); }
        }
        Templates.Clear();
        foreach (var template in Examples.Templates())
            Templates.Add(new FlowTemplateCardViewModel(template, Examples.Plan(template, existing, existingSkills), existing));
    }

    /// <summary>
    /// <b>Use template</b>. When the project lacks characters the flow needs, the card first asks whether to create them
    /// from the examples; otherwise the flow is copied into the project right away.
    /// </summary>
    [RelayCommand]
    public async Task UseTemplateAsync(FlowTemplateCardViewModel? card)
    {
        if (card == null || IsRunning) return;
        LeaveReplay(restore: true);
        if (card.HasMissing && _characters != null)
        {
            foreach (var other in Templates) other.IsConfirming = false;
            var inProject = _loader?.ProjectDirectory != null;
            var names = string.Join(", ", card.Plan.CharactersToCreate);
            var skills = card.Plan.SkillsToCreate.Count > 0 ? $" and {card.Plan.SkillsToCreate.Count} skill(s)" : string.Empty;
            card.ConfirmText = $"Create {card.Plan.CharactersToCreate.Count} missing character(s){skills} ({names}) in " +
                               (inProject ? "this project's .aiagent folder?" : "your global .aiagent folder?");
            card.IsConfirming = true;
            return;
        }
        await ApplyTemplateAsync(card, createMissing: false);
    }

    [RelayCommand]
    public Task CreateAndUseTemplateAsync(FlowTemplateCardViewModel? card) =>
        card == null ? Task.CompletedTask : ApplyTemplateAsync(card, createMissing: true);

    /// <summary>Copy the flow without creating the missing characters; the editor then outlines the steps that lack one.</summary>
    [RelayCommand]
    public Task UseTemplateAnywayAsync(FlowTemplateCardViewModel? card) =>
        card == null ? Task.CompletedTask : ApplyTemplateAsync(card, createMissing: false);

    [RelayCommand]
    public void CancelTemplate(FlowTemplateCardViewModel? card)
    {
        if (card != null) card.IsConfirming = false;
    }

    private async Task ApplyTemplateAsync(FlowTemplateCardViewModel card, bool createMissing)
    {
        if (IsRunning) return;
        LeaveReplay(restore: false);
        card.IsConfirming = false;
        try
        {
            var created = string.Empty;
            if (createMissing && _characters != null && !card.Plan.NothingToCreate)
            {
                var inProject = _loader?.ProjectDirectory != null;
                var charactersDir = inProject
                    ? _characters.ProjectCharactersDirectory ?? _characters.GlobalCharactersDirectory
                    : _characters.GlobalCharactersDirectory;
                var skillsDir = _skills == null ? null : inProject
                    ? _skills.ProjectSkillsDirectory ?? _skills.GlobalSkillsDirectory
                    : _skills.GlobalSkillsDirectory;
                Examples.Install(card.Plan, charactersDir, skillsDir);
                _characters.Refresh();
                _skills?.Refresh();
                await RefreshCharactersAsync();
                created = $" Created {card.Plan.CharactersToCreate.Count} character(s) in {charactersDir}.";
            }

            var flow = card.Template.Flow with { Name = UniqueName(card.Template.Id) };
            flow = FlowLayout.Apply(flow, FlowGraph.Build(flow));
            IsGalleryOpen = false;
            if (_loader != null)
            {
                var scope = _loader.ProjectDirectory != null ? PipelineScope.Project : PipelineScope.Global;
                var path = await _loader.SavePipelineAsync(flow, scope);
                RefreshPipelineList();
                Load(flow.Name);
                RunTask = card.SampleTask;
                StatusText = $"Copied template '{card.Template.Title}' to {path}.{created} Adapt it, then run it with the sample task.";
            }
            else
            {
                LoadDefinition(flow);
                RunTask = card.SampleTask;
                IsDirty = true;
                StatusText = $"Opened template '{card.Template.Title}'.{created}";
            }
            Revalidate();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to use template {Template}", card.Template.Id);
            StatusText = $"Could not use the template: {ex.Message}";
        }
    }

    public async Task RefreshCharactersAsync()
    {
        if (_characters == null) return;
        try
        {
            _characters.Refresh();
            var list = await _characters.ListAsync();
            _characterById = list.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            CharacterOptions.Clear();
            foreach (var c in list.Where(c => !c.IsTemplate && c.IsValid))
                CharacterOptions.Add(c.Id);
            RebuildPalette();
            foreach (var n in Nodes) ApplyCharacterLabel(n);
            Revalidate();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list characters for the Flows panel");
            StatusText = $"Could not load characters: {ex.Message}";
        }
    }

    /// <summary>Palette id of the "Human decision" entry (it is not a character).</summary>
    public const string HumanItemId = "@human";

    private void RebuildPalette()
    {
        Palette.Clear();
        var filter = PaletteFilter.Trim();
        if (filter.Length == 0 || "human decision approval".Contains(filter, StringComparison.OrdinalIgnoreCase))
            Palette.Add(new FlowPaletteItem
            {
                CharacterId = HumanItemId,
                Label = "Human decision",
                Description = "Runs no agent: asks you to choose an outcome (and leave a note)",
                BaseRole = null,
                Avatar = "🧑"
            });
        foreach (var c in _characterById.Values.Where(c => !c.IsTemplate && c.IsValid).OrderBy(c => c.IsBuiltIn).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.Length > 0 && !c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Palette.Add(new FlowPaletteItem
            {
                CharacterId = c.Id,
                Label = c.DisplayName, // the avatar is its own column in the row: Label would repeat it
                Description = c.Description,
                BaseRole = c.BaseRole,
                Avatar = string.IsNullOrEmpty(c.Avatar) ? "🤖" : c.Avatar
            });
        }
    }

    private void RefreshPipelineListSafe()
    {
        if (_suppressChangedRefresh) return;
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RefreshPipelineList();
            else Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPipelineList);
        }
        catch
        {
            RefreshPipelineList();
        }
    }

    private void RefreshPipelineList()
    {
        if (_loader == null) return;
        var keep = SelectedPipeline;
        _restoring = true;
        try
        {
            Pipelines.Clear();
            PipelineItems.Clear();
            // Project flows first, then global, then built-in; flows before linear pipelines, then by name.
            var ordered = _loader.GetEntries()
                .OrderByDescending(x => x.Scope)
                .ThenByDescending(x => x.Pipeline.IsFlow)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
            // Look the selection up in a local list, not in the live collection: a refresh posted to the UI thread can
            // overlap one running on the caller's thread (tests, startup), and a half-filled collection must not throw here.
            var items = new List<PipelineListItem>();
            foreach (var entry in ordered)
            {
                Pipelines.Add(entry.Name);
                var item = new PipelineListItem(entry.Name, entry.ScopeLabel, entry.OverridesText);
                PipelineItems.Add(item);
                items.Add(item);
            }
            SelectedPipeline = keep != null && Pipelines.Contains(keep) ? keep : null;
            SelectedPipelineItem = SelectedPipeline == null
                ? null
                : items.FirstOrDefault(i => string.Equals(i.Name, SelectedPipeline, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoring = false;
        }
        RefreshScopeInfo();
    }

    /// <summary>Update the scope badge and the "overrides ..." text from the loader's view of the open flow.</summary>
    private void RefreshScopeInfo()
    {
        var entry = _loadedName != null ? _loader?.GetEntry(_loadedName) : null;
        LoadedScope = entry?.Scope;
        OverridesText = entry?.OverridesText ?? string.Empty;
        OnPropertyChanged(nameof(CanChooseSaveScope));
        OnPropertyChanged(nameof(DefaultSaveScope));
        OnPropertyChanged(nameof(IsProjectSaveDefault));
    }

    partial void OnSelectedPipelineItemChanged(PipelineListItem? value)
    {
        if (_restoring || value == null) return;
        SelectedPipeline = value.Name;
    }

    partial void OnSelectedPipelineChanged(string? value)
    {
        if (!_restoring)
        {
            var item = value == null ? null : PipelineItems.FirstOrDefault(i => string.Equals(i.Name, value, StringComparison.OrdinalIgnoreCase));
            if (item != null && !ReferenceEquals(item, SelectedPipelineItem))
            {
                _restoring = true;
                SelectedPipelineItem = item;
                _restoring = false;
            }
        }
        if (_restoring || value == null || string.Equals(value, FlowName, StringComparison.OrdinalIgnoreCase) && !IsDirty) return;
        Load(value);
    }

    [RelayCommand]
    public void NewFlow()
    {
        if (IsRunning) return;
        LeaveReplay(restore: false);
        _restoring = true;
        try
        {
            FlowName = UniqueName("new-flow");
            Description = string.Empty;
            MaxParallel = FlowRunner.DefaultMaxParallel;
            BudgetText = string.Empty;
            Nodes.Clear();
            Edges.Clear();
            SelectedNode = null;
            SelectedEdge = null;
            WasLinear = false;
            SelectedPipeline = null;
        }
        finally
        {
            _restoring = false;
        }
        _undo.Clear();
        _redo.Clear();
        RaiseUndo();
        IsDirty = false;
        IsChoosingSaveScope = false;
        _restoring = true;
        SelectedPipelineItem = null;
        _restoring = false;
        _loadedName = null;
        RefreshScopeInfo();
        Revalidate();
        StatusText = "New flow. Drag characters from the left onto the canvas, then connect them.";
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; _loader?.GetPipeline(name) != null; i++) name = $"{baseName}-{i}";
        return name;
    }

    /// <summary>Open a saved pipeline. A linear pipeline is shown as a chain.</summary>
    public void Load(string name)
    {
        if (IsRunning) return;
        LeaveReplay(restore: false);
        var pipeline = _loader?.GetPipeline(name);
        if (pipeline == null)
        {
            StatusText = $"Pipeline '{name}' not found.";
            return;
        }
        LoadDefinition(pipeline);
        _undo.Clear();
        _redo.Clear();
        RaiseUndo();
        IsDirty = false;
        RefreshHistory();
        _restoring = true;
        SelectedPipeline = pipeline.Name;
        SelectedPipelineItem = PipelineItems.FirstOrDefault(i => string.Equals(i.Name, pipeline.Name, StringComparison.OrdinalIgnoreCase));
        _restoring = false;
        IsChoosingSaveScope = false;
        _loadedName = pipeline.Name;
        RefreshScopeInfo();
        StatusText = WasLinear
            ? $"'{pipeline.Name}' is a linear pipeline (one shared history). Saving it here turns it into a flow."
            : $"Opened flow '{pipeline.Name}'.";
    }

    /// <summary>Replace the canvas with a definition (no undo entry).</summary>
    public void LoadDefinition(SdlcPipelineDefinition pipeline)
    {
        _restoring = true;
        try
        {
            var graph = FlowGraph.Build(pipeline);
            // Positions: saved ones, else an automatic layout.
            var needLayout = pipeline.Stages.Any(s => s.X == null || s.Y == null);
            var layout = needLayout ? FlowLayout.Compute(graph) : null;

            foreach (var n in Nodes) n.PropertyChanged -= OnNodePropertyChanged;
            Nodes.Clear();
            Edges.Clear();
            SelectedNode = null;
            SelectedEdge = null;

            FlowName = pipeline.Name;
            Description = pipeline.Description ?? string.Empty;
            MaxParallel = pipeline.MaxParallel ?? FlowRunner.DefaultMaxParallel;
            BudgetText = pipeline.MaxBudgetUsd is > 0 ? pipeline.MaxBudgetUsd.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            WasLinear = !pipeline.IsFlow;

            foreach (var node in graph.Nodes)
            {
                var s = node.Stage;
                var pos = layout != null && layout.TryGetValue(node.Id, out var p) ? p : (s.X ?? 40, s.Y ?? 40);
                var vm = new FlowNodeViewModel
                {
                    Id = node.Id,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? node.Id : s.Name,
                    Kind = s.IsHuman ? "human" : string.Empty,
                    CharacterId = s.Character,
                    Role = s.Role,
                    Model = s.Model?.Trim() ?? string.Empty,
                    Provider = s.Provider?.Trim() ?? string.Empty,
                    PromptTemplate = s.PromptTemplate,
                    OutcomesText = string.Join(", ", s.Outcomes ?? new List<string>()),
                    RequireConfirmation = s.RequireConfirmation,
                    Enabled = s.Enabled,
                    X = pos.Item1,
                    Y = pos.Item2
                };
                ApplyCharacterLabel(vm);
                EnsureOption(ModelOptions, vm.Model);
                EnsureOption(ProviderOptions, vm.Provider);
                vm.PropertyChanged += OnNodePropertyChanged;
                Nodes.Add(vm);
            }
            foreach (var e in graph.Edges)
            {
                var from = Nodes.First(n => n.Id.Equals(e.From, StringComparison.OrdinalIgnoreCase));
                var to = Nodes.First(n => n.Id.Equals(e.To, StringComparison.OrdinalIgnoreCase));
                Edges.Add(new FlowEdgeViewModel(from, to) { When = e.When, MaxLoops = e.MaxLoops, IsLoop = e.IsLoop });
            }
            // Edges the graph dropped (unknown nodes) cannot be drawn; keep the user informed.
            if (pipeline.Edges != null && pipeline.Edges.Count > graph.Edges.Count)
                StatusText = $"{pipeline.Edges.Count - graph.Edges.Count} edge(s) point to missing nodes and were dropped.";
        }
        finally
        {
            _restoring = false;
        }
        Revalidate();
    }

    private void ApplyCharacterLabel(FlowNodeViewModel vm)
    {
        if (vm.IsHuman)
        {
            vm.CharacterLabel = "You decide";
            vm.Avatar = "🧑";
        }
        else if (vm.CharacterId != null && _characterById.TryGetValue(vm.CharacterId, out var c))
        {
            vm.CharacterLabel = c.DisplayName; // the node shows the avatar next to it
            vm.Avatar = string.IsNullOrEmpty(c.Avatar) ? "🤖" : c.Avatar;
        }
        else
        {
            vm.CharacterLabel = vm.CharacterId ?? (string.IsNullOrEmpty(vm.Role) ? "(no character)" : vm.Role);
            vm.Avatar = "🤖";
        }
    }

    // ============================================================ definition

    /// <summary>The canvas as a pipeline definition (always a flow: edges are written).</summary>
    public SdlcPipelineDefinition ToDefinition() => new()
    {
        Name = FlowName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        MaxParallel = MaxParallel == FlowRunner.DefaultMaxParallel ? null : MaxParallel,
        MaxBudgetUsd = BudgetUsd,
        Stages = Nodes.Select(n => new SdlcStageDefinition
        {
            Id = n.Id,
            Name = n.Name,
            Kind = n.IsHuman ? "human" : null,
            Character = n.IsHuman || string.IsNullOrWhiteSpace(n.CharacterId) ? null : n.CharacterId,
            Role = n.Role ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(n.Model) ? null : n.Model.Trim(),
            Provider = string.IsNullOrWhiteSpace(n.Provider) ? null : n.Provider.Trim(),
            PromptTemplate = n.PromptTemplate ?? string.Empty,
            Outcomes = n.Outcomes.Count == 0 ? null : n.Outcomes.ToList(),
            RequireConfirmation = n.RequireConfirmation,
            Enabled = n.Enabled,
            X = Math.Round(n.X),
            Y = Math.Round(n.Y)
        }).ToList(),
        Edges = Edges.Select(e => new FlowEdge
        {
            From = e.From.Id,
            To = e.To.Id,
            When = e.When,
            MaxLoops = e.MaxLoops > 0 ? e.MaxLoops : null
        }).ToList()
    };

    private FlowGraph BuildGraph() =>
        FlowGraph.Build(ToDefinition(), _characters == null ? null : id => _characterById.TryGetValue(id, out var c) ? c : null);

    /// <summary>Re-run validation and push problems onto nodes and edges.</summary>
    public void Revalidate()
    {
        CaptureBaseline();
        if (Nodes.Count == 0)
        {
            // An empty canvas is a starting point, not a list of problems.
            Problems.Clear();
            IsValid = false;
            WavesText = string.Empty;
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(CanRun));
            return;
        }
        var def = ToDefinition();
        var graph = FlowGraph.Build(def, _characters == null ? null : id => _characterById.TryGetValue(id, out var c) ? c : null);
        Problems.Clear();
        foreach (var p in graph.Problems) Problems.Add(p);
        foreach (var n in Nodes)
        {
            var mine = graph.ProblemsFor(n.Id).ToList();
            n.HasError = mine.Any(p => p.IsError);
            n.HasWarning = !n.HasError && mine.Count > 0;
            n.ProblemText = string.Join("\n", mine.Select(p => p.Message));
        }
        for (var i = 0; i < Edges.Count; i++)
        {
            var mine = graph.Problems.Where(p => p.EdgeIndex == i).ToList();
            Edges[i].HasError = mine.Any(p => p.IsError);
            Edges[i].ProblemText = string.Join("\n", mine.Select(p => p.Message));
            var ge = graph.Edges.FirstOrDefault(e => e.Index == i);
            Edges[i].IsLoop = ge?.IsLoop == true;
        }
        IsValid = graph.IsValid && Nodes.Count > 0;
        WavesText = graph.Waves.Count > 0 ? graph.DescribeWaves() : string.Empty;
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(CanRun));
        RebuildConditionOptions();
    }

    private void RebuildConditionOptions()
    {
        _restoring = true;
        try
        {
            ConditionOptions.Clear();
            ConditionOptions.Add(AlwaysCondition);
            var edge = SelectedEdge;
            if (edge != null)
            {
                foreach (var o in edge.From.Outcomes) ConditionOptions.Add(o);
                if (Edges.Any(e => ReferenceEquals(e.From, edge.From) && e.IsLoop) && !edge.IsLoop)
                    ConditionOptions.Add(FlowGraph.LoopExhausted);
                if (edge.When != null && !ConditionOptions.Contains(edge.When)) ConditionOptions.Add(edge.When);
            }
            SelectedCondition = edge?.When ?? AlwaysCondition;
        }
        finally
        {
            _restoring = false;
        }
    }

    // ============================================================ editing

    private void Changed()
    {
        IsDirty = true;
        Revalidate();
    }

    /// <summary>Record the current state for undo (call before a change).</summary>
    public void Snapshot()
    {
        if (_restoring) return;
        _undo.Push(JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions));
        _redo.Clear();
        _lastEditKey = null;
        RaiseUndo();
    }

    private void RaiseUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoring || sender is not FlowNodeViewModel node) return;
        switch (e.PropertyName)
        {
            case nameof(FlowNodeViewModel.Name):
            case nameof(FlowNodeViewModel.PromptTemplate):
            case nameof(FlowNodeViewModel.OutcomesText):
            case nameof(FlowNodeViewModel.RequireConfirmation):
            case nameof(FlowNodeViewModel.Enabled):
            case nameof(FlowNodeViewModel.Kind):
            case nameof(FlowNodeViewModel.Role):
            case nameof(FlowNodeViewModel.CharacterId):
            case nameof(FlowNodeViewModel.Model):
            case nameof(FlowNodeViewModel.Provider):
                // Typing in a box is one undo step per field, not one per keystroke.
                var key = node.Id + "/" + e.PropertyName;
                if (_lastEditKey != key)
                {
                    _undo.Push(_beforeEdit ?? JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions));
                    _redo.Clear();
                    _lastEditKey = key;
                    RaiseUndo();
                }
                if (e.PropertyName == nameof(FlowNodeViewModel.CharacterId)) ApplyCharacterLabel(node);
                Changed();
                break;
        }
    }

    /// <summary>State after the last completed change: what a text edit's undo step goes back to.</summary>
    private string? _beforeEdit;

    private void CaptureBaseline() => _beforeEdit = JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions);

    /// <summary>Add a node for <paramref name="characterId"/> at a canvas position (snapped to the grid).</summary>
    public FlowNodeViewModel AddNode(string characterId, double x, double y)
    {
        Snapshot();
        var human = characterId == HumanItemId;
        var c = !human && _characterById.TryGetValue(characterId, out var info) ? info : null;
        var baseId = human ? "human" : LocalFileStore.ToKebab(characterId);
        if (string.IsNullOrEmpty(baseId)) baseId = "step";
        var id = baseId;
        for (var i = 2; Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); i++) id = $"{baseId}-{i}";
        var node = new FlowNodeViewModel
        {
            Id = id,
            Name = human ? "Human decision" : c?.DisplayName ?? characterId,
            CharacterId = human ? null : characterId,
            Role = string.Empty,
            Kind = human ? "human" : string.Empty,
            OutcomesText = human ? "approved, rejected" : string.Empty,
            PromptTemplate = human
                ? "Review the result above and choose how the flow continues."
                : "Task: {task}\n\nDescribe what this step must do.",
            X = Snap(Math.Max(0, x)),
            Y = Snap(Math.Max(0, y))
        };
        ApplyCharacterLabel(node);
        _restoring = true;
        node.PropertyChanged += OnNodePropertyChanged;
        Nodes.Add(node);
        _restoring = false;
        SelectedNode = node;
        Changed();
        StatusText = $"Added '{node.Name}'. Drag from its right handle to another step to connect them.";
        return node;
    }

    /// <summary>Palette "＋": add the character to the right of the selected node (or the right-most one).</summary>
    [RelayCommand]
    public void AddFromPalette(FlowPaletteItem? item)
    {
        if (IsLocked) return;
        if (item == null) return;
        var anchor = SelectedNode ?? Nodes.OrderByDescending(n => n.X).FirstOrDefault();
        var x = anchor == null ? 40 : anchor.X + FlowLayout.DefaultColumnWidth;
        var y = anchor?.Y ?? 40;
        while (Nodes.Any(n => Math.Abs(n.X - x) < FlowNodeViewModel.Width && Math.Abs(n.Y - y) < FlowNodeViewModel.Height))
            y += FlowLayout.DefaultRowHeight;
        var from = SelectedNode;
        var node = AddNode(item.CharacterId, x, y);
        if (from != null) Connect(from, node, snapshot: false);
    }

    public static double Snap(double v) => Math.Round(v / GridSize) * GridSize;

    private bool _moving;

    /// <summary>Start dragging a node (one undo step per drag).</summary>
    public void BeginMove(FlowNodeViewModel node)
    {
        if (IsLocked) { SelectedNode = node; return; }
        Snapshot();
        _moving = true;
        SelectedNode = node;
    }

    public void MoveNode(FlowNodeViewModel node, double x, double y)
    {
        if (IsLocked) return;
        _restoring = true;
        node.X = Snap(Math.Max(0, x));
        node.Y = Snap(Math.Max(0, y));
        _restoring = false;
    }

    public void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        // Drop the undo entry when the node did not actually move.
        if (_undo.Count > 0 && _undo.Peek() == JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions))
        {
            _undo.Pop();
            RaiseUndo();
            return;
        }
        IsDirty = true;
        CaptureBaseline();
    }

    /// <summary>Connect two nodes. Returns null (with a status message) when the edge is not allowed.</summary>
    public FlowEdgeViewModel? Connect(FlowNodeViewModel from, FlowNodeViewModel to, bool snapshot = true)
    {
        if (IsLocked) return null;
        if (Edges.Any(e => ReferenceEquals(e.From, from) && ReferenceEquals(e.To, to) && e.When == null))
        {
            StatusText = $"{from.Id} → {to.Id} already exists.";
            return null;
        }
        if (snapshot) Snapshot();
        var edge = new FlowEdgeViewModel(from, to);
        // Going back to an earlier step (or to itself) makes a loop: it needs a condition and a limit.
        var def = ToDefinition();
        def.Edges!.Add(new FlowEdge { From = from.Id, To = to.Id });
        var closesCycle = ReferenceEquals(from, to) || FlowGraph.Build(def).Problems.Any(p => p.IsError && p.Message.StartsWith("Cycle", StringComparison.Ordinal));
        if (closesCycle)
        {
            edge.MaxLoops = 2;
            edge.When = from.Outcomes.Count > 0 ? from.Outcomes[^1] : null;
            StatusText = from.Outcomes.Count > 0
                ? $"Loop {from.Id} → {to.Id} on '{edge.When}', up to 2 times. Change it in the inspector."
                : $"Loop {from.Id} → {to.Id}, up to 2 times. Give {from.Id} outcomes (e.g. approved, rejected) to loop only on one of them.";
        }
        else
        {
            StatusText = $"Connected {from.Id} → {to.Id}.";
        }
        Edges.Add(edge);
        SelectedEdge = edge;
        Changed();
        return edge;
    }

    public void BeginEdge(FlowNodeViewModel from, double x, double y)
    {
        if (IsLocked) return;
        PendingEdgeSource = from;
        PendingEdgeX = x;
        PendingEdgeY = y;
    }

    public void DragEdge(double x, double y)
    {
        PendingEdgeX = x;
        PendingEdgeY = y;
    }

    /// <summary>Finish a dragged arrow on <paramref name="target"/> (null = dropped on empty canvas: cancel).</summary>
    public FlowEdgeViewModel? EndEdge(FlowNodeViewModel? target)
    {
        var from = PendingEdgeSource;
        PendingEdgeSource = null;
        if (from == null || target == null) return null;
        return Connect(from, target);
    }

    /// <summary>Topmost node under a canvas point.</summary>
    public FlowNodeViewModel? NodeAt(double x, double y) =>
        Nodes.LastOrDefault(n => x >= n.X && x <= n.X + FlowNodeViewModel.Width && y >= n.Y && y <= n.Y + FlowNodeViewModel.Height);

    [RelayCommand]
    public void DeleteSelected()
    {
        if (IsLocked) return;
        if (SelectedEdge != null)
        {
            Snapshot();
            var edge = SelectedEdge;
            SelectedEdge = null;
            Edges.Remove(edge);
            StatusText = $"Removed {edge.From.Id} → {edge.To.Id}.";
            Changed();
        }
        else if (SelectedNode != null)
        {
            Snapshot();
            var node = SelectedNode;
            SelectedNode = null;
            foreach (var e in Edges.Where(e => ReferenceEquals(e.From, node) || ReferenceEquals(e.To, node)).ToList())
                Edges.Remove(e);
            node.PropertyChanged -= OnNodePropertyChanged;
            Nodes.Remove(node);
            StatusText = $"Removed '{node.Name}' and its arrows.";
            Changed();
        }
    }

    /// <summary>Change the selected edge's loop limit (0 = not a loop).</summary>
    public void SetMaxLoops(int value)
    {
        if (SelectedEdge == null || SelectedEdge.MaxLoops == value || IsLocked) return;
        Snapshot();
        SelectedEdge.MaxLoops = Math.Max(0, value);
        Changed();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        SelectedNode = null;
        SelectedEdge = null;
    }

    [RelayCommand]
    public void AutoLayout()
    {
        if (IsLocked) return;
        var graph = FlowGraph.Build(ToDefinition());
        var positions = FlowLayout.Compute(graph);
        Snapshot();
        _restoring = true;
        foreach (var n in Nodes)
            if (positions.TryGetValue(n.Id, out var p)) { n.X = p.X; n.Y = p.Y; }
        _restoring = false;
        IsDirty = true;
        CaptureBaseline();
        StatusText = graph.Waves.Count > 0 ? $"Arranged by execution order: {graph.DescribeWaves()}" : "Arranged (fix the cycle to order by execution).";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo() => Restore(_undo, _redo, "Undone.");

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo() => Restore(_redo, _undo, "Redone.");

    private void Restore(Stack<string> from, Stack<string> to, string message)
    {
        if (from.Count == 0 || IsLocked) return;
        to.Push(JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions));
        var json = from.Pop();
        var selectedId = SelectedNode?.Id;
        var def = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;
        var wasLinear = WasLinear;
        LoadDefinition(def with { Edges = def.Edges ?? new List<FlowEdge>() });
        WasLinear = wasLinear;
        if (selectedId != null) SelectedNode = Nodes.FirstOrDefault(n => n.Id == selectedId);
        _lastEditKey = null;
        IsDirty = true;
        RaiseUndo();
        StatusText = message;
    }

    // ============================================================ save / delete

    /// <summary>
    /// Save. A flow that already lives in a project or global file is written back to it. Any other flow (new, renamed,
    /// or a built-in) asks where to go when a project folder is set: <b>Project (shared via git)</b> or
    /// <b>Global (only me)</b>; without a project folder it is saved globally.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (_loader == null || IsReplay) return;
        var name = FlowName.Trim();
        if (!LocalFileStore.IsValidName(name))
        {
            StatusText = $"Name '{name}' must be kebab-case (letters, numbers, hyphens), e.g. feature-team.";
            return;
        }
        if (_loadedName != null && string.Equals(_loadedName, name, StringComparison.OrdinalIgnoreCase) &&
            LoadedScope is PipelineScope.Global or PipelineScope.Project)
        {
            await SaveToScopeAsync(LoadedScope!.Value);
            return;
        }
        if (CanChooseSaveScope)
        {
            IsConfirmingDelete = false;
            IsChoosingSaveScope = true;
            StatusText = $"Where should '{name}' be saved? Project flows are shared with your team through git; global flows are only yours.";
            return;
        }
        await SaveToScopeAsync(PipelineScope.Global);
    }

    [RelayCommand]
    public Task SaveToProjectAsync() => SaveToScopeAsync(PipelineScope.Project);

    [RelayCommand]
    public Task SaveToGlobalAsync() => SaveToScopeAsync(PipelineScope.Global);

    [RelayCommand]
    public void CancelSaveScope()
    {
        IsChoosingSaveScope = false;
        StatusText = "Save cancelled.";
    }

    /// <summary>Write the open flow to the project (<c>.aiagent/pipelines</c> in the working directory) or the global folder.</summary>
    public async Task SaveToScopeAsync(PipelineScope scope)
    {
        IsChoosingSaveScope = false;
        if (_loader == null || IsReplay) return;
        var name = FlowName.Trim();
        if (!LocalFileStore.IsValidName(name))
        {
            StatusText = $"Name '{name}' must be kebab-case (letters, numbers, hyphens), e.g. feature-team.";
            return;
        }
        Revalidate();
        try
        {
            var def = ToDefinition();
            var path = await _loader.SavePipelineAsync(def, scope);
            var converted = WasLinear;
            WasLinear = false;
            IsDirty = false;
            _loadedName = name;
            RefreshPipelineList();
            _restoring = true;
            SelectedPipeline = name;
            SelectedPipelineItem = PipelineItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            _restoring = false;
            RefreshScopeInfo();
            var issues = Problems.Count(p => p.IsError);
            var hiddenBy = _loader.GetEntry(name) is { } inForce && inForce.Scope > scope ? inForce.Scope : (PipelineScope?)null;
            StatusText = $"Saved '{name}' as a {scope.Label()} flow to {path}" +
                         (converted ? " — it is now a flow." : ".") +
                         (hiddenBy is { } h ? $" A {h.Label()} flow with this name still takes precedence." : "") +
                         (issues > 0 ? $" It has {issues} problem(s) to fix before it can run." : "");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save flow {Name}", name);
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RequestDelete()
    {
        if (_loader == null || IsReplay) return;
        var entry = _loader.GetEntry(FlowName);
        if (entry?.FilePath is not { } path || !System.IO.File.Exists(path)) return;
        if (entry.Scope == PipelineScope.Pack)
        {
            StatusText = $"'{entry.Name}' comes from the pack '{entry.PackName}' and is read-only. Save a copy to change it, or remove the pack.";
            return;
        }
        IsChoosingSaveScope = false;
        DeletePrompt = $"Delete the {entry.ScopeLabel} flow file {System.IO.Path.GetFileName(path)}?" +
                       (entry.Scope == PipelineScope.Project ? " It is shared with your team through git." : "");
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    public void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    public void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (_loader == null) return;
        var name = FlowName;
        var deleted = _loader.GetEntry(name);
        if (_loader.DeletePipeline(name))
        {
            RefreshPipelineList();
            if (_loader.GetEntry(name) is { } shown)
            {
                Load(name); // the global or built-in flow it overrode is back
                StatusText = $"Deleted your {deleted?.ScopeLabel} '{name}'; the {shown.ScopeLabel} version is shown again.";
            }
            else
            {
                NewFlow();
                StatusText = $"Deleted '{name}'.";
            }
        }
    }

    [RelayCommand]
    public void Validate()
    {
        Revalidate();
        var errors = Problems.Count(p => p.IsError);
        StatusText = errors == 0
            ? $"No problems. {WavesText}"
            : $"{errors} problem(s): {string.Join(" ", Problems.Where(p => p.IsError).Take(3).Select(p => p.Message))}";
    }

    // ============================================================ history and replay

    /// <summary>Saved runs of the open flow, newest first.</summary>
    public ObservableCollection<FlowRunListItem> History { get; } = new();
    public bool HasHistory => History.Count > 0;

    /// <summary>The run picked in the History dropdown; picking one opens it in replay mode.</summary>
    [ObservableProperty] private FlowRunListItem? _selectedRun;

    /// <summary>A past run is open: the canvas is read-only and shows how that run ended.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(SelectedOutput))]
    private bool _isReplay;

    /// <summary>"Replay · 29 Oct 18:02 · succeeded" while a past run is open.</summary>
    [ObservableProperty] private string _replayTitle = string.Empty;

    private FlowRunRecord? _replayRecord;
    private readonly Dictionary<string, string> _replayMarkdown = new(StringComparer.OrdinalIgnoreCase);
    private string? _beforeReplay;
    private bool _dirtyBeforeReplay;
    private bool _suppressRunSelection;

    partial void OnIsReplayChanged(bool value) => RerunFromHereCommand.NotifyCanExecuteChanged();

    partial void OnSelectedRunChanged(FlowRunListItem? value)
    {
        if (_suppressRunSelection || value == null) return;
        OpenRun(value.RunId);
    }

    private void SetSelectedRun(FlowRunListItem? item)
    {
        _suppressRunSelection = true;
        try { SelectedRun = item; }
        finally { _suppressRunSelection = false; }
    }

    /// <summary>Re-reads the saved runs of the open flow.</summary>
    public void RefreshHistory()
    {
        try
        {
            History.Clear();
            if (_runStore != null && !string.IsNullOrWhiteSpace(FlowName))
                foreach (var run in _runStore.List(FlowName.Trim()))
                    History.Add(new FlowRunListItem(run));
            SetSelectedRun(IsReplay && _replayRecord != null ? History.FirstOrDefault(h => h.RunId == _replayRecord.Id) : null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not list the run history of {Flow}", FlowName);
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>Opens a saved run of the open flow: the canvas shows the flow as it was, coloured by how each step ended.</summary>
    public void OpenRun(string runId)
    {
        if (_runStore == null || IsRunning) return;
        var flowName = IsReplay && _replayRecord != null ? _replayRecord.FlowName : FlowName.Trim();
        var record = _runStore.Load(flowName, runId);
        if (record == null)
        {
            StatusText = "That run could not be read (its files may have been deleted).";
            RefreshHistory();
            return;
        }

        if (!IsReplay)
        {
            _beforeReplay = JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions);
            _dirtyBeforeReplay = IsDirty;
        }
        _replayRecord = record;
        _replayMarkdown.Clear();
        LoadDefinition(record.Definition);
        IsReplay = true;
        ApplyReplayState(record);
        ReplayTitle = $"Replay · {record.StartedAt.ToLocalTime():d MMM HH:mm} · {FlowUsageFormat.StatusName(record.Status)}";
        StatusText = $"Replay of the run from {record.StartedAt.ToLocalTime():d MMM HH:mm}: {FlowUsageFormat.StatusName(record.Status)} — {record.Message}";
        SelectedNode = Nodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedOutput));
        SetSelectedRun(History.FirstOrDefault(h => h.RunId == runId));
        RerunFromHereCommand.NotifyCanExecuteChanged();
    }

    private void ApplyReplayState(FlowRunRecord record)
    {
        ResetRunState();
        foreach (var node in Nodes)
        {
            var steps = record.Steps.Where(s => s.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Round).ToList();
            if (steps.Count == 0)
            {
                node.RunState = FlowNodeState.Skipped;
                node.RunDetail = "not run";
                continue;
            }
            var last = steps[^1];
            node.RunDetail = last.Reused ? "reused" : last.Detail;
            node.RunCount = steps.Max(s => s.Round);
            node.RunOutcome = last.Outcome;
            node.RunState = last.State;
            var usage = steps.Where(s => s.Usage != null).Select(s => s.Usage!).ToList();
            if (usage.Count > 0) node.UsageText = FlowUsageFormat.Badge(FlowUsageTotals.Sum(usage));
        }
        foreach (var edge in Edges)
        {
            edge.IsTaken = !edge.IsLoop && edge.From.RunState == FlowNodeState.Done
                           && edge.To.RunState is FlowNodeState.Done or FlowNodeState.Failed
                           && (edge.When == null || string.Equals(edge.When, edge.From.RunOutcome, StringComparison.OrdinalIgnoreCase));
        }

        var usages = record.Steps.Where(s => s.Usage != null).Select(s => s.Usage!).ToList();
        var result = new FlowRunResult(record.Status, record.Message, Array.Empty<FlowNodeRunSummary>()) { StepRuns = usages, BudgetUsd = record.BudgetUsd };
        UsageRows.Clear();
        foreach (var u in usages) UsageRows.Add(FlowUsageRowViewModel.From(u));
        if (UsageRows.Count > 0) UsageRows.Add(FlowUsageRowViewModel.Total(result));
        RunUsageText = usages.Count > 0 ? FlowUsageFormat.Header(result.Usage, record.BudgetUsd) : string.Empty;
        OnPropertyChanged(nameof(HasUsageRows));
        RunTask = record.Task;
    }

    private string ReplayMarkdown(string nodeId)
    {
        if (_replayRecord == null) return string.Empty;
        if (_replayMarkdown.TryGetValue(nodeId, out var cached)) return cached;
        var record = _replayRecord;
        var text = FlowRunMarkdown.Step(record, nodeId, step => _runStore?.LoadOutput(record.FlowName, record.Id, step));
        _replayMarkdown[nodeId] = text;
        return text;
    }

    /// <summary>Closes replay mode and brings back the flow that was open before.</summary>
    [RelayCommand]
    public void ExitReplay()
    {
        if (!IsReplay) return;
        LeaveReplay(restore: true);
        StatusText = "Back to the flow.";
    }

    private void LeaveReplay(bool restore)
    {
        if (!IsReplay) return;
        IsReplay = false;
        ReplayTitle = string.Empty;
        _replayRecord = null;
        _replayMarkdown.Clear();
        ResetRunState();
        if (restore && _beforeReplay != null)
        {
            var before = JsonSerializer.Deserialize<SdlcPipelineDefinition>(_beforeReplay, SdlcPipelineLoader.FileJsonOptions);
            if (before != null) LoadDefinition(before);
            IsDirty = _dirtyBeforeReplay;
        }
        _beforeReplay = null;
        SetSelectedRun(null);
        OnPropertyChanged(nameof(SelectedOutput));
    }

    private bool CanRerunFromHere => IsReplay && !IsRunning && SelectedNode != null && _runner != null;

    /// <summary>
    /// Starts a new run from the selected step of the open past run: the steps before it keep their recorded output
    /// (shown as "reused") and the step and everything after it run again, on the flow as it was in that run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRerunFromHere))]
    public async Task RerunFromHereAsync()
    {
        if (_replayRecord is not { } record || _runStore == null || SelectedNode is not { } from) return;
        var plan = _runStore.BuildReplay(record, from.Id, out var error);
        if (plan == null)
        {
            StatusText = error ?? "This run cannot be re-run from here.";
            return;
        }

        // The canvas already shows the flow of that run: leave replay mode without switching back to the other version.
        var task = record.Task;
        IsReplay = false;
        ReplayTitle = string.Empty;
        _replayRecord = null;
        _replayMarkdown.Clear();
        _beforeReplay = null;
        SetSelectedRun(null);
        IsDirty = true;
        RunTask = task;
        await RunCoreAsync(plan);
    }

    // ============================================================ run

    [RelayCommand]
    public Task RunAsync() => RunCoreAsync(null);

    private async Task RunCoreAsync(FlowReplayPlan? replay)
    {
        if (_runner == null) { StatusText = "Running flows is not available."; return; }
        if (IsRunning || IsReplay) return;
        Revalidate();
        if (!IsValid)
        {
            StatusText = "Fix the problems first: " + string.Join(" ", Problems.Where(p => p.IsError).Take(2).Select(p => p.Message));
            return;
        }
        if (string.IsNullOrWhiteSpace(RunTask))
        {
            StatusText = "Describe the task for the flow first.";
            return;
        }

        ResetRunState();
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        StatusText = "Running…";
        var definition = ToDefinition();
        try
        {
            await foreach (var evt in _runner.RunAsync(definition, RunTask.Trim(), Guid.NewGuid().ToString(), WorkingDirectory,
                               cancellationToken: _runCts.Token, maxParallel: MaxParallel, maxBudgetUsd: BudgetUsd, replay: replay))
            {
                ApplyRunEvent(evt);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Flow run failed");
            StatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            foreach (var tcs in _approvals.Values) tcs.TrySetResult(false);
            _approvals.Clear();
            _gateFeedback.Clear();
            foreach (var choice in _humanChoices.Values) choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
            _humanChoices.Clear();
            IsEnteringGateNote = false;
            GateNote = string.Empty;
            _runCts?.Dispose();
            _runCts = null;
            RefreshHistory();
        }
    }

    [RelayCommand]
    public void Stop()
    {
        _runCts?.Cancel();
        _remoteCancel?.Invoke();
        foreach (var tcs in _approvals.Values) tcs.TrySetResult(false);
        foreach (var choice in _humanChoices.Values) choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
        StatusText = "Stopping…";
    }

    /// <summary>Answer the confirmation gate of the selected node.</summary>
    [RelayCommand]
    public void Approve(FlowNodeViewModel? node) => AnswerGate(node ?? SelectedNode, true);

    [RelayCommand]
    public void Decline(FlowNodeViewModel? node) => AnswerGate(node ?? SelectedNode, false);

    private void AnswerGate(FlowNodeViewModel? node, bool approved)
    {
        if (node == null || !_approvals.Remove(node.Id, out var tcs)) return;
        node.IsAwaitingApproval = false;
        node.IsAwaitingGate = false;
        _gateFeedback.Remove(node.Id);
        IsEnteringGateNote = false;
        GateNote = string.Empty;
        tcs.TrySetResult(approved);
    }

    /// <summary>The note typed for "Approve with note" (also used as the note of a human step).</summary>
    [ObservableProperty] private string _gateNote = string.Empty;

    /// <summary>The note box of the gate is open.</summary>
    [ObservableProperty] private bool _isEnteringGateNote;

    /// <summary>Gate: "Approve with note…" opens the note box.</summary>
    [RelayCommand]
    public void BeginGateNote() => IsEnteringGateNote = true;

    /// <summary>Gate: approve; the note (if any) is added to the step's prompt as a reviewer note.</summary>
    [RelayCommand]
    public void ApproveWithNote(FlowNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node != null && _gateFeedback.TryGetValue(node.Id, out var feedback) && !string.IsNullOrWhiteSpace(GateNote))
            feedback.Text = GateNote.Trim();
        AnswerGate(node, true);
    }

    /// <summary>Gate: do not run this step, carry on with the rest of the flow.</summary>
    [RelayCommand]
    public void SkipStep(FlowNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node != null && _gateFeedback.TryGetValue(node.Id, out var feedback)) feedback.Skip = true;
        AnswerGate(node, false);
    }

    /// <summary>Gate: stop the whole flow here.</summary>
    [RelayCommand]
    public void StopAtGate(FlowNodeViewModel? node) => AnswerGate(node ?? SelectedNode, false);

    /// <summary>Human step: choose an outcome (null = a step without outcomes just continues). The note box is sent along.</summary>
    [RelayCommand]
    public void Choose(string? outcome) => AnswerHuman(SelectedNode, new FlowHumanChoice(outcome, string.IsNullOrWhiteSpace(GateNote) ? null : GateNote.Trim()));

    /// <summary>Human step: stop the whole flow.</summary>
    [RelayCommand]
    public void StopHuman() => AnswerHuman(SelectedNode, new FlowHumanChoice(null, null, Stop: true));

    private void AnswerHuman(FlowNodeViewModel? node, FlowHumanChoice choice)
    {
        if (node == null || !_humanChoices.Remove(node.Id, out var tcs)) return;
        node.IsAwaitingChoice = false;
        GateNote = string.Empty;
        if (!choice.Stop)
            node.AppendOutput($"\n\n**You chose:** {choice.Outcome ?? "continue"}" + (choice.Note != null ? $" — {choice.Note}" : "") + "\n");
        tcs.TrySetResult(choice);
        if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
    }

    public void ResetRunState()
    {
        foreach (var n in Nodes) n.ResetRun();
        foreach (var e in Edges)
        {
            e.IsTaken = false;
            e.LoopsTaken = 0;
        }
        RunUsageText = string.Empty;
        UsageRows.Clear();
        OnPropertyChanged(nameof(HasUsageRows));
        OnPropertyChanged(nameof(SelectedOutput));
    }

    /// <summary>Map one flow event onto node and edge state (public for tests and replays).</summary>
    public void ApplyRunEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case FlowStartedEvent started:
                StatusText = $"Running: {string.Join(" · ", started.Waves.Select((w, i) => $"{i + 1}: {string.Join(" ∥ ", w)}"))}";
                break;

            case FlowNodeStatusEvent status when FindNode(status.NodeId) is { } node:
                node.RunState = status.State;
                node.RunCount = status.Iteration;
                node.RunOutcome = status.Outcome;
                node.RunDetail = status.Detail;
                if (status.State == FlowNodeState.Running && status.Iteration > 0)
                {
                    node.SetOutput(status.Iteration > 1 ? AgentOutputText.RoundHeader(status.Iteration) : string.Empty);
                    // A node that runs again un-lights the arrows out of it until it finishes.
                    foreach (var e in Edges.Where(e => ReferenceEquals(e.From, node) && !e.IsLoop)) e.IsTaken = false;
                }
                if (status.State is FlowNodeState.Failed or FlowNodeState.Skipped && status.Detail != null)
                    node.AppendOutput(AgentOutputText.FailureNote(status.State.ToString(), status.Detail));
                if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
                break;

            case FlowEdgeTakenEvent taken:
                var edge = Edges.FirstOrDefault(e => e.From.Id.Equals(taken.From, StringComparison.OrdinalIgnoreCase) &&
                                                     e.To.Id.Equals(taken.To, StringComparison.OrdinalIgnoreCase) &&
                                                     (e.IsLoop == taken.IsLoop) &&
                                                     string.Equals(e.When, taken.When, StringComparison.OrdinalIgnoreCase));
                if (edge != null)
                {
                    edge.IsTaken = true;
                    if (taken.IsLoop) edge.LoopsTaken = taken.LoopCount;
                }
                break;

            case AgentTaggedEvent { Inner: TextDeltaEvent delta } tagged when FindNode(tagged.AgentId) is { } node:
                node.AppendOutput(delta.Delta);
                if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
                break;

            case AgentTaggedEvent { Inner: ToolCallStartEvent tool } tagged when FindNode(tagged.AgentId) is { } node:
                node.AppendOutput(AgentOutputText.ToolNote(tool.Call.Name));
                break;

            case AgentTaggedEvent { Inner: AgentFinishedEvent finished } tagged when FindNode(tagged.AgentId) is { } node:
                // The final answer replaces the streamed text (it is the clean version).
                if (!string.IsNullOrWhiteSpace(finished.Response.Content))
                    node.SetOutput((node.RunCount > 1 ? AgentOutputText.RoundHeader(node.RunCount) : string.Empty) + finished.Response.Content);
                if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
                break;

            case AgentTaggedEvent { Inner: StatusUpdateEvent st } tagged when FindNode(tagged.AgentId) is { } node:
                if (AgentOutputText.StatusNote(st) is { } statusNote) node.AppendOutput(statusNote);
                break;

            case AgentTaggedEvent { Inner: AgentErrorEvent err } tagged when FindNode(tagged.AgentId) is { } node:
                node.AppendOutput(AgentOutputText.ErrorNote(err.Error.Message));
                break;

            case AgentTaggedEvent { Inner: ApprovalRequestEvent approval } tagged when FindNode(tagged.AgentId) is { } node:
                if (approval.Call.Name == "pipeline_stage")
                {
                    // Confirmation gate of a step: answered in the inspector (Approve / with note / Skip / Stop).
                    _approvals[node.Id] = approval.Approval;
                    _gateFeedback[node.Id] = approval.Feedback;
                    node.IsAwaitingApproval = true;
                    node.IsAwaitingGate = true;
                    IsEnteringGateNote = false;
                    GateNote = string.Empty;
                    SelectedNode = node;
                    StatusText = $"'{node.Name}' is waiting for your approval to start.";
                }
                else
                {
                    // Tool approvals inside a step use the same buttons.
                    _approvals[node.Id] = approval.Approval;
                    node.IsAwaitingApproval = true;
                    node.AppendOutput(AgentOutputText.ApprovalNote(approval.Call.Name));
                    StatusText = $"'{node.Name}' asks to run {approval.Call.Name}.";
                }
                break;

            case AgentTaggedEvent { Inner: FlowHumanRequestEvent human } when FindNode(human.NodeId) is { } humanNode:
                // A human step: it runs no agent, it waits here for a choice.
                _humanChoices[humanNode.Id] = human.Choice;
                humanNode.IsAwaitingChoice = true;
                humanNode.AppendOutput($"**Waiting for your decision**\n\n{human.Prompt}\n");
                GateNote = string.Empty;
                SelectedNode = humanNode;
                OnPropertyChanged(nameof(SelectedOutput));
                StatusText = $"'{humanNode.Name}' is waiting for your decision.";
                break;

            case FlowUsageEvent usage when FindNode(usage.NodeId) is { } usedNode:
                usedNode.UsageText = FlowUsageFormat.Badge(usage.Node);
                RunUsageText = FlowUsageFormat.Header(usage.Run, usage.BudgetUsd);
                break;

            case FlowFinishedEvent finishedFlow:
                StatusText = $"Flow {FlowUsageFormat.StatusName(finishedFlow.Result.Status)}: {finishedFlow.Result.Message}";
                UsageRows.Clear();
                foreach (var stepRun in finishedFlow.Result.StepRuns) UsageRows.Add(FlowUsageRowViewModel.From(stepRun));
                if (UsageRows.Count > 0) UsageRows.Add(FlowUsageRowViewModel.Total(finishedFlow.Result));
                OnPropertyChanged(nameof(HasUsageRows));
                if (finishedFlow.Result.StepRuns.Count > 0)
                    RunUsageText = FlowUsageFormat.Header(finishedFlow.Result.Usage, finishedFlow.Result.BudgetUsd);
                break;

            case StatusUpdateEvent { Status: "Flow invalid" } invalid:
                StatusText = "Flow is invalid: " + invalid.Detail;
                break;

            case RemoteRequestResolvedEvent resolved:
                ApplyRemoteResolved(resolved);
                break;
        }
    }

    private FlowNodeViewModel? FindNode(string id) =>
        Nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry of the pipeline dropdown: the flow name and where it comes from.</summary>
/// <param name="Name">Pipeline name.</param>
/// <param name="Scope"><c>project</c>, <c>global</c> or <c>builtin</c>.</param>
/// <param name="Overrides">"overrides global" / "overrides built-in", or null.</param>
public sealed record PipelineListItem(string Name, string Scope, string? Overrides)
{
    public bool HasOverrides => Overrides != null;
}
