using System;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace AiCodeAgent.App.Views;

/// <summary>
/// Renders the strokes from <see cref="AnnotationViewModel"/> onto a canvas and
/// forwards pointer input (press/move/release) to the view-model so it can
/// capture new strokes. The canvas is transparent and overlays the preview.
/// </summary>
public partial class AnnotationCanvasView : UserControl
{
    public AnnotationCanvasView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private AnnotationViewModel? Vm => DataContext as AnnotationViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm != null)
        {
            Vm.Strokes.CollectionChanged += (_, _) => RenderStrokes();
        }
        RenderStrokes();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null || !Vm.IsActive) return;
        var p = e.GetPosition(this);
        Vm.BeginStroke(p.X, p.Y);
        e.Pointer.Capture(this);
        RenderStrokes();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Vm == null || !Vm.IsActive) return;
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        var p = e.GetPosition(this);
        Vm.ContinueStroke(p.X, p.Y);
        RenderStrokes();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm == null) return;
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        Vm.EndStroke();
        e.Pointer.Capture(null);
        RenderStrokes();
    }

    /// <summary>
    /// Clears the canvas and redraws all committed strokes. A live preview of
    /// the in-progress stroke is drawn from the view-model's current stroke.
    /// </summary>
    private void RenderStrokes()
    {
        var canvas = this.FindControl<Canvas>("StrokeCanvas");
        if (canvas == null) return;
        canvas.Children.Clear();

        if (Vm == null) return;

        foreach (var stroke in Vm.Strokes)
        {
            DrawStroke(canvas, stroke);
        }
    }

    private static void DrawStroke(Canvas canvas, AnnotationStroke stroke)
    {
        if (stroke.Points.Count == 0) return;

        switch (stroke.Tool)
        {
            case AnnotationToolType.Pen:
            case AnnotationToolType.Highlighter:
                DrawPolyline(canvas, stroke);
                break;
            case AnnotationToolType.Arrow:
                DrawArrow(canvas, stroke);
                break;
            case AnnotationToolType.Text:
                DrawText(canvas, stroke);
                break;
        }
    }

    private static void DrawPolyline(Canvas canvas, AnnotationStroke stroke)
    {
        if (stroke.Points.Count < 2) return;
        var brush = Brush.Parse(stroke.Color);
        var poly = new PolylineGeometry(stroke.Points.Select(p => new global::Avalonia.Point(p.X, p.Y)).ToList(), false);
        var shape = new global::Avalonia.Controls.Shapes.Path
        {
            Data = poly,
            Stroke = brush,
            StrokeThickness = stroke.Width,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Opacity = stroke.Tool == AnnotationToolType.Highlighter ? 0.35 : 1.0
        };
        canvas.Children.Add(shape);
    }

    private static void DrawArrow(Canvas canvas, AnnotationStroke stroke)
    {
        if (stroke.Points.Count < 2) return;
        var brush = Brush.Parse(stroke.Color);
        var a = stroke.Points[0];
        var b = stroke.Points[1];
        var line = new global::Avalonia.Controls.Shapes.Line
        {
            StartPoint = new global::Avalonia.Point(a.X, a.Y),
            EndPoint = new global::Avalonia.Point(b.X, b.Y),
            Stroke = brush,
            StrokeThickness = stroke.Width,
            StrokeLineCap = PenLineCap.Round
        };
        canvas.Children.Add(line);

        // Arrowhead
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        double ux = dx / len, uy = dy / len;
        double headLen = 10 + stroke.Width * 2;
        double angle = Math.PI / 6;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double hx1 = b.X - headLen * (ux * cos - uy * sin);
        double hy1 = b.Y - headLen * (ux * sin + uy * cos);
        double hx2 = b.X - headLen * (ux * cos + uy * sin);
        double hy2 = b.Y - headLen * (-ux * sin + uy * cos);
        canvas.Children.Add(new global::Avalonia.Controls.Shapes.Line
        {
            StartPoint = new global::Avalonia.Point(b.X, b.Y),
            EndPoint = new global::Avalonia.Point(hx1, hy1),
            Stroke = brush,
            StrokeThickness = stroke.Width,
            StrokeLineCap = PenLineCap.Round
        });
        canvas.Children.Add(new global::Avalonia.Controls.Shapes.Line
        {
            StartPoint = new global::Avalonia.Point(b.X, b.Y),
            EndPoint = new global::Avalonia.Point(hx2, hy2),
            Stroke = brush,
            StrokeThickness = stroke.Width,
            StrokeLineCap = PenLineCap.Round
        });
    }

    private static void DrawText(Canvas canvas, AnnotationStroke stroke)
    {
        if (stroke.Points.Count == 0 || string.IsNullOrEmpty(stroke.Text)) return;
        var p = stroke.Points[0];
        var tb = new TextBlock
        {
            Text = stroke.Text,
            Foreground = Brush.Parse(stroke.Color),
            FontSize = Math.Max(stroke.Width * 8, 12),
            [Canvas.LeftProperty] = p.X,
            [Canvas.TopProperty] = p.Y
        };
        canvas.Children.Add(tb);
    }
}