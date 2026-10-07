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
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
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
    public ObservableCollection<FlowProblem> Problems { get; } = new();
    /// <summary>Choices for the selected edge's condition: "(always)", the source's outcomes, and "loop-exhausted" when it applies.</summary>
    public ObservableCollection<string> ConditionOptions { get; } = new();
    public ObservableCollection<string> CharacterOptions { get; } = new();

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _flowName = "new-flow";
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private int _maxParallel = FlowRunner.DefaultMaxParallel;
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

    /// <summary>While dragging a new arrow: where its loose end is (canvas coordinates).</summary>
    [ObservableProperty] private FlowNodeViewModel? _pendingEdgeSource;
    [ObservableProperty] private double _pendingEdgeX;
    [ObservableProperty] private double _pendingEdgeY;

    private readonly Dictionary<string, TaskCompletionSource<bool>> _approvals = new(StringComparer.OrdinalIgnoreCase);

    public FlowEditorViewModel(
        SdlcPipelineLoader? loader = null,
        SdlcPipelineRunner? runner = null,
        ICharacterRegistry? characters = null,
        ILogger<FlowEditorViewModel>? logger = null)
    {
        _loader = loader;
        _runner = runner;
        _characters = characters;
        _logger = logger;
        if (_loader != null)
            _loader.Changed += (_, _) => RefreshPipelineListSafe();
    }

    public bool HasSelection => SelectedNode != null || SelectedEdge != null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public bool CanRun => !IsRunning && Nodes.Count > 0 && IsValid && _runner != null;
    public string PendingEdgePath => PendingEdgeSource == null
        ? string.Empty
        : $"M {PendingEdgeSource.OutPortX:0},{PendingEdgeSource.OutPortY:0} L {PendingEdgeX:0},{PendingEdgeY:0}";
    public bool IsDrawingEdge => PendingEdgeSource != null;

    /// <summary>The selected node's live output, or the last finished run's summary.</summary>
    public string SelectedOutput => SelectedNode?.OutputText ?? string.Empty;

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
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnPaletteFilterChanged(string value) => RebuildPalette();
    partial void OnFlowNameChanged(string value) => MarkDirtyFromHeader();
    partial void OnDescriptionChanged(string value) => MarkDirtyFromHeader();
    partial void OnMaxParallelChanged(int value) => MarkDirtyFromHeader();

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

    private void RebuildPalette()
    {
        Palette.Clear();
        var filter = PaletteFilter.Trim();
        foreach (var c in _characterById.Values.Where(c => !c.IsTemplate && c.IsValid).OrderBy(c => c.IsBuiltIn).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.Length > 0 && !c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Palette.Add(new FlowPaletteItem
            {
                CharacterId = c.Id,
                Label = c.Label,
                Description = c.Description,
                BaseRole = c.BaseRole,
                Avatar = string.IsNullOrEmpty(c.Avatar) ? "🤖" : c.Avatar
            });
        }
    }

    private void RefreshPipelineListSafe()
    {
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
        Pipelines.Clear();
        foreach (var p in _loader.GetAllPipelines().OrderByDescending(p => p.IsFlow).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Pipelines.Add(p.Name);
        _restoring = true;
        SelectedPipeline = keep != null && Pipelines.Contains(keep) ? keep : null;
        _restoring = false;
    }

    partial void OnSelectedPipelineChanged(string? value)
    {
        if (_restoring || value == null || string.Equals(value, FlowName, StringComparison.OrdinalIgnoreCase) && !IsDirty) return;
        Load(value);
    }

    [RelayCommand]
    public void NewFlow()
    {
        if (IsRunning) return;
        _restoring = true;
        try
        {
            FlowName = UniqueName("new-flow");
            Description = string.Empty;
            MaxParallel = FlowRunner.DefaultMaxParallel;
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
        _restoring = true;
        SelectedPipeline = pipeline.Name;
        _restoring = false;
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
            WasLinear = !pipeline.IsFlow;

            foreach (var node in graph.Nodes)
            {
                var s = node.Stage;
                var pos = layout != null && layout.TryGetValue(node.Id, out var p) ? p : (s.X ?? 40, s.Y ?? 40);
                var vm = new FlowNodeViewModel
                {
                    Id = node.Id,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? node.Id : s.Name,
                    CharacterId = s.Character,
                    Role = s.Role,
                    PromptTemplate = s.PromptTemplate,
                    OutcomesText = string.Join(", ", s.Outcomes ?? new List<string>()),
                    RequireConfirmation = s.RequireConfirmation,
                    Enabled = s.Enabled,
                    X = pos.Item1,
                    Y = pos.Item2
                };
                ApplyCharacterLabel(vm);
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
        if (vm.CharacterId != null && _characterById.TryGetValue(vm.CharacterId, out var c))
        {
            vm.CharacterLabel = c.Label;
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
        Stages = Nodes.Select(n => new SdlcStageDefinition
        {
            Id = n.Id,
            Name = n.Name,
            Character = string.IsNullOrWhiteSpace(n.CharacterId) ? null : n.CharacterId,
            Role = n.Role ?? string.Empty,
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
            case nameof(FlowNodeViewModel.Role):
            case nameof(FlowNodeViewModel.CharacterId):
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
        var c = _characterById.TryGetValue(characterId, out var info) ? info : null;
        var baseId = LocalFileStore.ToKebab(characterId);
        if (string.IsNullOrEmpty(baseId)) baseId = "step";
        var id = baseId;
        for (var i = 2; Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); i++) id = $"{baseId}-{i}";
        var node = new FlowNodeViewModel
        {
            Id = id,
            Name = c?.DisplayName ?? characterId,
            CharacterId = characterId,
            Role = string.Empty,
            PromptTemplate = "Task: {task}\n\nDescribe what this step must do.",
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
        Snapshot();
        _moving = true;
        SelectedNode = node;
    }

    public void MoveNode(FlowNodeViewModel node, double x, double y)
    {
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
        if (IsRunning) return;
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
        if (SelectedEdge == null || SelectedEdge.MaxLoops == value) return;
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
        if (from.Count == 0 || IsRunning) return;
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

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (_loader == null) return;
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
            await _loader.SavePipelineAsync(def);
            var converted = WasLinear;
            WasLinear = false;
            IsDirty = false;
            RefreshPipelineList();
            _restoring = true;
            SelectedPipeline = name;
            _restoring = false;
            var issues = Problems.Count(p => p.IsError);
            StatusText = $"Saved '{name}' to {_loader.GetPipelinePath(name)}" +
                         (converted ? " — it is now a flow." : ".") +
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
        if (_loader == null || !System.IO.File.Exists(_loader.GetPipelinePath(FlowName))) return;
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
        if (_loader.DeletePipeline(name))
        {
            RefreshPipelineList();
            if (_loader.GetPipeline(name) != null)
            {
                Load(name); // a built-in it overrode is back
                StatusText = $"Deleted your '{name}'; the built-in version is shown again.";
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

    // ============================================================ run

    [RelayCommand]
    public async Task RunAsync()
    {
        if (_runner == null) { StatusText = "Running flows is not available."; return; }
        if (IsRunning) return;
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
                               cancellationToken: _runCts.Token, maxParallel: MaxParallel))
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
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Stop()
    {
        _runCts?.Cancel();
        foreach (var tcs in _approvals.Values) tcs.TrySetResult(false);
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
        tcs.TrySetResult(approved);
    }

    public void ResetRunState()
    {
        foreach (var n in Nodes) n.ResetRun();
        foreach (var e in Edges)
        {
            e.IsTaken = false;
            e.LoopsTaken = 0;
        }
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
                    node.SetOutput(status.Iteration > 1 ? $"— round {status.Iteration} —\n" : string.Empty);
                    // A node that runs again un-lights the arrows out of it until it finishes.
                    foreach (var e in Edges.Where(e => ReferenceEquals(e.From, node) && !e.IsLoop)) e.IsTaken = false;
                }
                if (status.State is FlowNodeState.Failed or FlowNodeState.Skipped && status.Detail != null)
                    node.AppendOutput($"\n[{status.State.ToString().ToLowerInvariant()}: {status.Detail}]");
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
                node.AppendOutput($"\n[tool] {tool.Call.Name}\n");
                break;

            case AgentTaggedEvent { Inner: AgentFinishedEvent finished } tagged when FindNode(tagged.AgentId) is { } node:
                // The final answer replaces the streamed text (it is the clean version).
                if (!string.IsNullOrWhiteSpace(finished.Response.Content))
                    node.SetOutput((node.RunCount > 1 ? $"— round {node.RunCount} —\n" : string.Empty) + finished.Response.Content);
                if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
                break;

            case AgentTaggedEvent { Inner: StatusUpdateEvent st } tagged when FindNode(tagged.AgentId) is { } node:
                node.AppendOutput($"\n[{st.Status}] {st.Detail}\n");
                break;

            case AgentTaggedEvent { Inner: AgentErrorEvent err } tagged when FindNode(tagged.AgentId) is { } node:
                node.AppendOutput($"\n[error] {err.Error.Message}\n");
                break;

            case AgentTaggedEvent { Inner: ApprovalRequestEvent approval } tagged when FindNode(tagged.AgentId) is { } node:
                if (approval.Call.Name == "pipeline_stage")
                {
                    // Confirmation gate of a step: answered on the node card.
                    _approvals[node.Id] = approval.Approval;
                    node.IsAwaitingApproval = true;
                    SelectedNode = node;
                    StatusText = $"'{node.Name}' is waiting for your approval to start.";
                }
                else
                {
                    // Tool approvals inside a step use the same buttons.
                    _approvals[node.Id] = approval.Approval;
                    node.IsAwaitingApproval = true;
                    node.AppendOutput($"\n[approval needed] {approval.Call.Name}\n");
                    StatusText = $"'{node.Name}' asks to run {approval.Call.Name}.";
                }
                break;

            case FlowFinishedEvent finishedFlow:
                StatusText = $"Flow {finishedFlow.Result.Status.ToString().ToLowerInvariant()}: {finishedFlow.Result.Message}";
                break;

            case StatusUpdateEvent { Status: "Flow invalid" } invalid:
                StatusText = "Flow is invalid: " + invalid.Detail;
                break;
        }
    }

    private FlowNodeViewModel? FindNode(string id) =>
        Nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
