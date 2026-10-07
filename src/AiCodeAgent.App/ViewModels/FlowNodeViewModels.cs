using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AiCodeAgent.Core.Flows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>A character in the Flows palette.</summary>
public sealed class FlowPaletteItem
{
    public required string CharacterId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? BaseRole { get; init; }
    public string Avatar { get; init; } = string.Empty;
    public string Hint => string.IsNullOrEmpty(BaseRole) ? Description : $"{BaseRole} · {Description}";
}

/// <summary>A node (stage) on the Flows canvas, with its editable settings and live run state.</summary>
public sealed partial class FlowNodeViewModel : ObservableObject
{
    public const double Width = 190;
    public const double Height = 76;

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _characterId;
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _promptTemplate = string.Empty;
    /// <summary>Outcomes as typed in the inspector: "approved, rejected".</summary>
    [ObservableProperty] private string _outcomesText = string.Empty;
    [ObservableProperty] private bool _requireConfirmation;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Display label of the character ("🟣 Dana — .NET Developer"), or the role.</summary>
    [ObservableProperty] private string _characterLabel = string.Empty;
    [ObservableProperty] private string _avatar = "🤖";

    // Validation
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private string _problemText = string.Empty;

    // Run state
    [ObservableProperty] private FlowNodeState? _runState;
    [ObservableProperty] private int _runCount;
    [ObservableProperty] private string? _runOutcome;
    [ObservableProperty] private string? _runDetail;
    [ObservableProperty] private bool _isAwaitingApproval;
    private readonly StringBuilder _output = new();

    /// <summary>Live text the character produced in the current/last run.</summary>
    public string OutputText => _output.ToString();

    public IReadOnlyList<string> Outcomes =>
        OutcomesText.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public double InPortX => X;
    public double InPortY => Y + Height / 2;
    public double OutPortX => X + Width;
    public double OutPortY => Y + Height / 2;

    public bool IsRunning => RunState == FlowNodeState.Running;
    public bool IsDone => RunState == FlowNodeState.Done;
    public bool IsFailed => RunState == FlowNodeState.Failed;
    public bool IsSkipped => RunState == FlowNodeState.Skipped;
    public bool IsWaiting => RunState == FlowNodeState.Pending;
    public bool HasRunState => RunState != null;

    /// <summary>Badge shown on the card: ⏳ waiting, ▶ running, ✓ done, ✕ failed, – skipped.</summary>
    public string StateGlyph => RunState switch
    {
        FlowNodeState.Pending => "⏳",
        FlowNodeState.Running => "▶",
        FlowNodeState.Done => "✓",
        FlowNodeState.Failed => "✕",
        FlowNodeState.Skipped => "–",
        _ => string.Empty
    };

    public string StateText
    {
        get
        {
            if (RunState == null) return string.Empty;
            var text = RunState switch
            {
                FlowNodeState.Pending => IsAwaitingApproval ? "waiting for approval" : "waiting",
                FlowNodeState.Running => IsAwaitingApproval ? "waiting for approval" : "running",
                FlowNodeState.Done => RunOutcome != null ? $"done → {RunOutcome}" : "done",
                FlowNodeState.Failed => "failed",
                FlowNodeState.Skipped => "skipped",
                _ => string.Empty
            };
            if (RunCount > 1) text += $" · round {RunCount}";
            return text;
        }
    }

    /// <summary>Card subtitle: the character, or the run state while/after running.</summary>
    public string Subtitle => RunState == null ? CharacterLabel : $"{CharacterLabel} · {StateText}";

    partial void OnXChanged(double value)
    {
        OnPropertyChanged(nameof(InPortX));
        OnPropertyChanged(nameof(OutPortX));
    }

    partial void OnYChanged(double value)
    {
        OnPropertyChanged(nameof(InPortY));
        OnPropertyChanged(nameof(OutPortY));
    }

    partial void OnRunStateChanged(FlowNodeState? value) => RaiseRunProperties();
    partial void OnRunCountChanged(int value) => RaiseRunProperties();
    partial void OnRunOutcomeChanged(string? value) => RaiseRunProperties();
    partial void OnIsAwaitingApprovalChanged(bool value) => RaiseRunProperties();
    partial void OnCharacterLabelChanged(string value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnOutcomesTextChanged(string value) => OnPropertyChanged(nameof(Outcomes));

    private void RaiseRunProperties()
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(HasRunState));
    }

    internal void AppendOutput(string text)
    {
        _output.Append(text);
        OnPropertyChanged(nameof(OutputText));
    }

    internal void SetOutput(string text)
    {
        _output.Clear();
        _output.Append(text);
        OnPropertyChanged(nameof(OutputText));
    }

    internal void ResetRun()
    {
        RunState = null;
        RunCount = 0;
        RunOutcome = null;
        RunDetail = null;
        IsAwaitingApproval = false;
        SetOutput(string.Empty);
    }
}

/// <summary>An arrow on the Flows canvas. Geometry is computed here so the view only draws strings.</summary>
public sealed partial class FlowEdgeViewModel : ObservableObject
{
    public FlowEdgeViewModel(FlowNodeViewModel from, FlowNodeViewModel to)
    {
        From = from;
        To = to;
        From.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FlowNodeViewModel.X) or nameof(FlowNodeViewModel.Y)) RaiseGeometry(); };
        To.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FlowNodeViewModel.X) or nameof(FlowNodeViewModel.Y)) RaiseGeometry(); };
    }

    public FlowNodeViewModel From { get; }
    public FlowNodeViewModel To { get; }

    /// <summary>Condition: an outcome of <see cref="From"/>, "loop-exhausted", or null (always).</summary>
    [ObservableProperty] private string? _when;
    /// <summary>Loop limit; 0 = none (a normal edge).</summary>
    [ObservableProperty] private int _maxLoops;
    /// <summary>Set from validation: this edge goes back to an earlier node.</summary>
    [ObservableProperty] private bool _isLoop;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _problemText = string.Empty;
    /// <summary>Lit up during a run once the edge was taken.</summary>
    [ObservableProperty] private bool _isTaken;
    [ObservableProperty] private int _loopsTaken;

    public string Label
    {
        get
        {
            if (IsLoop) return $"{When ?? "always"} ↺{(LoopsTaken > 0 ? $"{LoopsTaken}/" : "")}{MaxLoops}";
            return When ?? string.Empty;
        }
    }

    partial void OnWhenChanged(string? value) => RaiseLabel();
    partial void OnMaxLoopsChanged(int value) => RaiseLabel();
    partial void OnLoopsTakenChanged(int value) => RaiseLabel();
    partial void OnIsLoopChanged(bool value)
    {
        RaiseLabel();
        RaiseGeometry();
    }

    private void RaiseLabel()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(HasLabel));
    }

    private (double X1, double Y1, double C1X, double C1Y, double C2X, double C2Y, double X2, double Y2) Curve()
    {
        double x1 = From.OutPortX, y1 = From.OutPortY, x2 = To.InPortX, y2 = To.InPortY;
        if (ReferenceEquals(From, To))
        {
            // Self loop: a small arc above the card, from its right side back to its left side.
            var top = From.Y - 60;
            return (x1, y1, x1 + 50, top, x2 - 50, top, x2, y2);
        }
        if (IsLoop || x2 < x1 + 20)
        {
            // Going back: arc above both nodes so it does not run through other cards.
            var top = Math.Min(From.Y, To.Y) - 70;
            return (x1, y1, x1 + 90, top, x2 - 90, top, x2, y2);
        }
        var dx = Math.Max(40, (x2 - x1) / 2);
        return (x1, y1, x1 + dx, y1, x2 - dx, y2, x2, y2);
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>SVG-style path data of the curve.</summary>
    public string PathData
    {
        get
        {
            var c = Curve();
            return $"M {F(c.X1)},{F(c.Y1)} C {F(c.C1X)},{F(c.C1Y)} {F(c.C2X)},{F(c.C2Y)} {F(c.X2)},{F(c.Y2)}";
        }
    }

    /// <summary>Arrowhead triangle at the target, aligned with the curve's end tangent.</summary>
    public string ArrowData
    {
        get
        {
            var c = Curve();
            var dx = c.X2 - c.C2X;
            var dy = c.Y2 - c.C2Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) { dx = 1; dy = 0; len = 1; }
            dx /= len;
            dy /= len;
            const double size = 9;
            var bx = c.X2 - dx * size;
            var by = c.Y2 - dy * size;
            var px = -dy * size * 0.55;
            var py = dx * size * 0.55;
            return $"M {F(c.X2)},{F(c.Y2)} L {F(bx + px)},{F(by + py)} L {F(bx - px)},{F(by - py)} Z";
        }
    }

    /// <summary>Label position: the curve's midpoint.</summary>
    public double LabelX
    {
        get
        {
            var c = Curve();
            return 0.125 * c.X1 + 0.375 * c.C1X + 0.375 * c.C2X + 0.125 * c.X2 - 20;
        }
    }

    public double LabelY
    {
        get
        {
            var c = Curve();
            return 0.125 * c.Y1 + 0.375 * c.C1Y + 0.375 * c.C2Y + 0.125 * c.Y2 - 18;
        }
    }

    public bool HasLabel => !string.IsNullOrEmpty(Label);

    internal void RaiseGeometry()
    {
        OnPropertyChanged(nameof(PathData));
        OnPropertyChanged(nameof(ArrowData));
        OnPropertyChanged(nameof(LabelX));
        OnPropertyChanged(nameof(LabelY));
    }
}
