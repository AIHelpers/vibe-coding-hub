using System;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace AiCodeAgent.App.Views;

/// <summary>
/// Pointer and keyboard handling for the Flows canvas: drag characters from the palette, move
/// nodes, drag arrows from a node's output handle, select arrows, pan and zoom. All state changes
/// go through <see cref="FlowEditorViewModel"/>; this class only translates input.
/// </summary>
public partial class FlowsView : UserControl
{
    private const string CharacterFormat = "application/x-vibe-flow-character";
    private const double MinZoom = 0.3;
    private const double MaxZoom = 2.0;

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);

    private Border? _viewport;
    private Canvas? _surface;

    private enum Gesture { None, MoveNode, DrawEdge, Pan }
    private Gesture _gesture;
    private FlowNodeViewModel? _dragNode;
    private Point _dragOffset;
    private Point _panStart;
    private Point _panOrigin;

    public FlowsView()
    {
        AvaloniaXamlLoader.Load(this);
        _viewport = this.FindControl<Border>("Viewport");
        _surface = this.FindControl<Canvas>("Surface");
        if (_surface != null)
        {
            var transforms = new TransformGroup();
            transforms.Children.Add(_scale);
            transforms.Children.Add(_translate);
            _surface.RenderTransform = transforms;
        }
        if (_viewport != null)
        {
            // handledEventsToo: node cards and edge paths are inside the viewport and may mark events handled.
            _viewport.AddHandler(PointerPressedEvent, OnCanvasPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _viewport.AddHandler(PointerMovedEvent, OnCanvasMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            _viewport.AddHandler(PointerReleasedEvent, OnCanvasReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            _viewport.AddHandler(PointerWheelChangedEvent, OnCanvasWheel, RoutingStrategies.Bubble, handledEventsToo: true);
            _viewport.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            _viewport.AddHandler(DragDrop.DropEvent, OnDrop);
        }
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private FlowEditorViewModel? Vm => DataContext as FlowEditorViewModel;

    /// <summary>Pointer position in canvas coordinates (undoes pan and zoom).</summary>
    private Point CanvasPoint(PointerEventArgs e) => _surface == null ? e.GetPosition(this) : e.GetPosition(_surface);

    // ============================================================ palette drag & drop

    private async void OnPaletteItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: FlowPaletteItem item } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var data = new DataObject();
        data.Set(CharacterFormat, item.CharacterId);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch (Exception)
        {
            // Drag-and-drop can be unavailable (e.g. headless); the ＋ button still works.
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(CharacterFormat) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.Get(CharacterFormat) is not string characterId) return;
        var p = _surface == null ? e.GetPosition(this) : e.GetPosition(_surface);
        // Drop point = card center.
        Vm.AddNode(characterId, p.X - FlowNodeViewModel.Width / 2, p.Y - FlowNodeViewModel.Height / 2);
        Focus();
    }

    // ============================================================ canvas gestures

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null || _gesture != Gesture.None) return;
        // Only react once (the handler is registered for tunnel and bubble).
        if (e.Route == RoutingStrategies.Bubble && e.Handled) return;

        var point = e.GetCurrentPoint(_viewport);
        var source = e.Source as Visual;
        Focus();

        if (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && FindData<FlowNodeViewModel>(source) == null))
        {
            BeginPan(e);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        var canvasPoint = CanvasPoint(e);

        // Output handle: start an arrow.
        if (source is Ellipse ellipse && ellipse.Classes.Contains("outport") && FindData<FlowNodeViewModel>(source) is { } fromNode)
        {
            if (Vm.IsRunning) return;
            _gesture = Gesture.DrawEdge;
            Vm.BeginEdge(fromNode, canvasPoint.X, canvasPoint.Y);
            e.Pointer.Capture(_viewport);
            e.Handled = true;
            return;
        }

        // Node card: select and start moving.
        if (FindData<FlowNodeViewModel>(source) is { } node)
        {
            if (Vm.IsRunning)
            {
                Vm.SelectedNode = node; // still allowed: shows the node's output
                e.Handled = true;
                return;
            }
            _gesture = Gesture.MoveNode;
            _dragNode = node;
            _dragOffset = new Point(canvasPoint.X - node.X, canvasPoint.Y - node.Y);
            Vm.BeginMove(node);
            e.Pointer.Capture(_viewport);
            e.Handled = true;
            return;
        }

        // Arrow (its wide transparent hit path).
        if (source is Path path && path.Classes.Contains("edgeHit") && path.DataContext is FlowEdgeViewModel edge)
        {
            Vm.SelectedEdge = edge;
            e.Handled = true;
            return;
        }

        // Empty canvas: clear the selection and pan.
        if (source is Visual v && IsInsideInspectorlessCanvas(v))
        {
            Vm.ClearSelection();
            BeginPan(e);
        }
    }

    private bool IsInsideInspectorlessCanvas(Visual v)
    {
        // Anything inside the viewport that is not a node or an arrow counts as empty canvas.
        for (Visual? cur = v; cur != null; cur = cur.GetVisualParent())
            if (ReferenceEquals(cur, _viewport)) return true;
        return false;
    }

    private void BeginPan(PointerPressedEventArgs e)
    {
        _gesture = Gesture.Pan;
        _panStart = e.GetPosition(_viewport);
        _panOrigin = new Point(_translate.X, _translate.Y);
        e.Pointer.Capture(_viewport);
        e.Handled = true;
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (Vm == null) return;
        switch (_gesture)
        {
            case Gesture.MoveNode when _dragNode != null:
                var p = CanvasPoint(e);
                Vm.MoveNode(_dragNode, p.X - _dragOffset.X, p.Y - _dragOffset.Y);
                break;
            case Gesture.DrawEdge:
                var q = CanvasPoint(e);
                Vm.DragEdge(q.X, q.Y);
                break;
            case Gesture.Pan:
                var now = e.GetPosition(_viewport);
                _translate.X = _panOrigin.X + (now.X - _panStart.X);
                _translate.Y = _panOrigin.Y + (now.Y - _panStart.Y);
                break;
        }
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm == null) return;
        switch (_gesture)
        {
            case Gesture.MoveNode:
                Vm.EndMove();
                break;
            case Gesture.DrawEdge:
                var p = CanvasPoint(e);
                Vm.EndEdge(Vm.NodeAt(p.X, p.Y));
                break;
        }
        _gesture = Gesture.None;
        _dragNode = null;
        e.Pointer.Capture(null);
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_surface == null || _viewport == null) return;
        var before = CanvasPoint(e);
        var zoom = Math.Clamp(_scale.ScaleX * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), MinZoom, MaxZoom);
        _scale.ScaleX = _scale.ScaleY = zoom;
        // Keep the canvas point under the pointer where it was.
        var screen = e.GetPosition(_viewport);
        _translate.X = screen.X - before.X * zoom;
        _translate.Y = screen.Y - before.Y * zoom;
        e.Handled = true;
    }

    private void OnResetView(object? sender, RoutedEventArgs e)
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _translate.X = _translate.Y = 0;
    }

    // ============================================================ inspector + keyboard

    private void OnMaxLoopsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (Vm?.SelectedEdge == null || e.NewValue == null) return;
        Vm.SetMaxLoops((int)e.NewValue.Value);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        // Typing in a text box keeps its own keys.
        if (e.Source is TextBox) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                Vm.DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                if (Vm.IsDrawingEdge) Vm.EndEdge(null);
                else Vm.ClearSelection();
                _gesture = Gesture.None;
                e.Handled = true;
                break;
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when ctrl:
                if (Vm.RedoCommand.CanExecute(null)) Vm.Redo();
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                if (Vm.UndoCommand.CanExecute(null)) Vm.Undo();
                e.Handled = true;
                break;
            case Key.S when ctrl:
                _ = Vm.SaveAsync();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The nearest data context of type <typeparamref name="T"/> from <paramref name="source"/> up to the viewport.</summary>
    private T? FindData<T>(Visual? source) where T : class
    {
        for (var cur = source; cur != null && !ReferenceEquals(cur, _viewport); cur = cur.GetVisualParent())
            if (cur is StyledElement { DataContext: T data }) return data;
        return null;
    }
}
