using System;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using Avalonia;
using Avalonia.Controls;
using Shape = Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>The Flows panel renders its canvas and turns pointer gestures into view-model edits.</summary>
public class FlowsViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flows-view-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private (Window Window, FlowsView View, FlowEditorViewModel Vm) Show()
    {
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters);
        vm.OpenAsync(_root).GetAwaiter().GetResult();
        vm.NewFlow();
        var view = new FlowsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Window coordinates of a canvas point (the canvas starts unpanned, unzoomed).</summary>
    private static Point ToWindow(Window window, FlowsView view, double x, double y)
    {
        var surface = view.FindControl<Canvas>("Surface")!;
        return surface.TranslatePoint(new Point(x, y), window)!.Value;
    }

    [AvaloniaFact]
    public void Canvas_RendersNodesEdgesAndLabels()
    {
        var (window, view, vm) = Show();
        var a = vm.AddNode("reviewer", 40, 60);
        var b = vm.AddNode("implementer", 340, 60);
        a.OutcomesText = "approved, rejected";
        vm.Connect(a, b);
        vm.SelectedCondition = "approved";
        Pump();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Reviewer", texts);
        Assert.Contains("Implementer", texts);
        Assert.Contains("approved", texts);
        // Edge path + arrowhead geometry was produced from the view model's path data.
        var paths = view.GetVisualDescendants().OfType<Shape.Path>().Where(p => p.Classes.Contains("edge")).ToList();
        Assert.Single(paths);
        Assert.NotNull(paths[0].Data);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingANode_MovesIt()
    {
        var (window, view, vm) = Show();
        var node = vm.AddNode("planner", 100, 100);
        Pump();

        var start = ToWindow(window, view, node.X + 60, node.Y + 30);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(150, 80));
        window.MouseUp(start + new Point(150, 80), MouseButton.Left);
        Pump();

        Assert.Equal(250, node.X);
        Assert.Equal(180, node.Y);
        Assert.Same(node, vm.SelectedNode);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingFromTheOutputHandle_ConnectsTwoSteps()
    {
        var (window, view, vm) = Show();
        var a = vm.AddNode("planner", 40, 100);
        var b = vm.AddNode("implementer", 400, 100);
        Pump();

        var handle = ToWindow(window, view, a.OutPortX, a.OutPortY);
        var target = ToWindow(window, view, b.X + 80, b.Y + 30);
        window.MouseDown(handle, MouseButton.Left);
        window.MouseMove(target);
        window.MouseUp(target, MouseButton.Left);
        Pump();

        var edge = Assert.Single(vm.Edges);
        Assert.Same(a, edge.From);
        Assert.Same(b, edge.To);
        Assert.Same(edge, vm.SelectedEdge);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingEmptyCanvas_ClearsSelection_AndDeleteKeyRemovesSelection()
    {
        var (window, view, vm) = Show();
        var node = vm.AddNode("planner", 40, 40);
        Pump();
        Assert.Same(node, vm.SelectedNode);

        var empty = ToWindow(window, view, 500, 400); // inside the visible canvas, away from the node
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        Assert.Null(vm.SelectedNode);

        vm.SelectedNode = node;
        view.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Pump();
        Assert.Empty(vm.Nodes);
        window.Close();
    }
}
