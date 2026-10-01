using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// View-model for the freehand annotation overlay. Captures pen, highlighter,
/// arrow, and text strokes; supports undo/clear; and produces an
/// <see cref="AnnotationMessage"/> that can be attached to a chat message or
/// preview write-back.
/// </summary>
public partial class AnnotationViewModel : ObservableObject
{
    private readonly AnnotationMapper _mapper;
    private readonly List<AnnotationStroke> _undo = new();
    private List<AnnotationPoint>? _currentStroke;
    private AnnotationToolType _activeTool = AnnotationToolType.Pen;

    /// <summary>Strokes captured so far, in overlay pixel coordinates.</summary>
    public ObservableCollection<AnnotationStroke> Strokes { get; } = new();

    /// <summary>Whether the annotation overlay is visible and active.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The currently selected tool.</summary>
    public AnnotationToolType ActiveTool
    {
        get => _activeTool;
        set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(IsTextTool));
                OnPropertyChanged(nameof(IsPenTool));
                OnPropertyChanged(nameof(IsArrowTool));
                OnPropertyChanged(nameof(IsHighlighterTool));
                OnPropertyChanged(nameof(IsEraserTool));
            }
        }
    }

    [ObservableProperty]
    private string _color = "#FF3333";

    [ObservableProperty]
    private double _strokeWidth = 2.0;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Backing field for the pending note text.</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    public bool IsPenTool => ActiveTool == AnnotationToolType.Pen;
    public bool IsHighlighterTool => ActiveTool == AnnotationToolType.Highlighter;
    public bool IsArrowTool => ActiveTool == AnnotationToolType.Arrow;
    public bool IsTextTool => ActiveTool == AnnotationToolType.Text;
    public bool IsEraserTool => ActiveTool == AnnotationToolType.Eraser;

    /// <summary>Raised when the user commits the annotation to chat/preview.</summary>
    public event EventHandler<AnnotationMessage>? AnnotationCommitted;

    /// <summary>Raised when the user cancels/clears the overlay.</summary>
    public event EventHandler? Cancelled;

    public AnnotationViewModel(AnnotationMapper mapper)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <summary>Begin capturing a new stroke at the given overlay point.</summary>
    public void BeginStroke(double x, double y)
    {
        if (!IsActive) return;

        if (ActiveTool == AnnotationToolType.Eraser)
        {
            EraseAt(x, y);
            return;
        }

        if (ActiveTool == AnnotationToolType.Text)
        {
            // For text, anchor a single point; actual text captured via Text property.
            _currentStroke = new List<AnnotationPoint> { new(x, y) };
            return;
        }

        _currentStroke = new List<AnnotationPoint> { new(x, y) };
    }

    /// <summary>Continue the current stroke to the given overlay point.</summary>
    public void ContinueStroke(double x, double y)
    {
        if (!IsActive || _currentStroke == null) return;
        if (ActiveTool == AnnotationToolType.Arrow)
        {
            // Arrow only keeps start and current end.
            _currentStroke = new List<AnnotationPoint> { _currentStroke[0], new(x, y) };
            return;
        }
        _currentStroke.Add(new(x, y));
    }

    /// <summary>End the current stroke and commit it to <see cref="Strokes"/>.</summary>
    public void EndStroke()
    {
        if (_currentStroke == null) return;

        if (ActiveTool == AnnotationToolType.Text)
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                var stroke = new AnnotationStroke
                {
                    Tool = AnnotationToolType.Text,
                    Color = Color,
                    Width = StrokeWidth,
                    Points = _currentStroke.ToList(),
                    Text = Text
                };
                Strokes.Add(stroke);
                _undo.Add(stroke);
            }
        }
        else if (ActiveTool == AnnotationToolType.Arrow && _currentStroke.Count >= 2)
        {
            var stroke = new AnnotationStroke
            {
                Tool = AnnotationToolType.Arrow,
                Color = Color,
                Width = StrokeWidth,
                Points = _currentStroke.Take(2).ToList()
            };
            Strokes.Add(stroke);
            _undo.Add(stroke);
        }
        else if (_currentStroke.Count > 0)
        {
            var stroke = new AnnotationStroke
            {
                Tool = ActiveTool,
                Color = Color,
                Width = ActiveTool == AnnotationToolType.Highlighter ? Math.Max(StrokeWidth * 4, 8) : StrokeWidth,
                Points = _currentStroke.ToList()
            };
            Strokes.Add(stroke);
            _undo.Add(stroke);
        }

        _currentStroke = null;
    }

    private void EraseAt(double x, double y)
    {
        const double radius = 10;
        for (int i = Strokes.Count - 1; i >= 0; i--)
        {
            var s = Strokes[i];
            if (s.Points.Any(p => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) <= radius))
            {
                Strokes.RemoveAt(i);
            }
        }
    }

    /// <summary>Undo the most recent stroke.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (Strokes.Count > 0)
        {
            Strokes.RemoveAt(Strokes.Count - 1);
        }
    }

    /// <summary>Clear all strokes.</summary>
    [RelayCommand]
    private void Clear()
    {
        Strokes.Clear();
    }

    /// <summary>Cancel and hide the overlay, discarding strokes.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Strokes.Clear();
        _currentStroke = null;
        IsActive = false;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Commit the current strokes as an <see cref="AnnotationMessage"/> and
    /// raise <see cref="AnnotationCommitted"/>. The overlay stays active so the
    /// user can keep annotating, but strokes are cleared after commit.
    /// </summary>
    [RelayCommand]
    private void Commit()
    {
        if (Strokes.Count == 0)
            return;

        var bounds = AnnotationMapper.ComputeBounds(Strokes);
        var msg = new AnnotationMessage
        {
            Strokes = Strokes.ToList(),
            Note = Note,
            Bounds = bounds
        };

        AnnotationCommitted?.Invoke(this, msg);
        Strokes.Clear();
        Note = string.Empty;
    }

    /// <summary>
    /// Builds an <see cref="AnnotationMessage"/> from the current strokes
    /// without clearing them, useful for synchronous callers.
    /// </summary>
    public AnnotationMessage BuildMessage()
    {
        var bounds = AnnotationMapper.ComputeBounds(Strokes);
        return new AnnotationMessage
        {
            Strokes = Strokes.ToList(),
            Note = Note,
            Bounds = bounds
        };
    }
}