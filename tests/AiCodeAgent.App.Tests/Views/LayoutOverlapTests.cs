using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>
/// E4-S3: at window widths of 1024, 1366 and 1920 no named control overlaps another or sticks out of its host.
/// The bounds come from a real (headless) layout pass with the Fluent theme, like the running app.
/// </summary>
public class LayoutOverlapTests : IDisposable
{
    public static IEnumerable<object[]> Widths => new[] { new object[] { 1024.0 }, new object[] { 1366.0 }, new object[] { 1920.0 } };

    private const double Tolerance = 0.75;
    private const double ActivityBarWidth = 48;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "layout-overlap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // ----- helpers -----

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Rect BoundsIn(Control control, Visual root)
    {
        var origin = control.TranslatePoint(new Point(0, 0), root);
        Assert.True(origin.HasValue, $"{control.Name ?? control.GetType().Name} is not in the visual tree");
        return new Rect(origin.Value, control.Bounds.Size);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}");

    /// <summary>No two rectangles intersect, and every rectangle lies inside the host.</summary>
    private static void AssertNoOverlapOrClipping(IReadOnlyDictionary<string, Rect> rects, Rect host, string where)
    {
        foreach (var (name, r) in rects)
        {
            Assert.True(r.Width > 0 && r.Height > 0, $"{where}: {name} has no size ({r})");
            Assert.True(r.Left >= host.Left - Tolerance && r.Right <= host.Right + Tolerance &&
                        r.Top >= host.Top - Tolerance && r.Bottom <= host.Bottom + Tolerance,
                $"{where}: {name} {r} sticks out of its host {host}");
        }

        var list = rects.ToList();
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i].Value.Deflate(Tolerance);
                var b = list[j].Value.Deflate(Tolerance);
                Assert.False(a.Intersects(b), $"{where}: {list[i].Key} {list[i].Value} overlaps {list[j].Key} {list[j].Value}");
            }
    }

    private static ChatViewModel CreateChat()
    {
        var bus = new AgentEventBus();
        var service = new AgentService(
            Substitute.For<IAgentOrchestrator>(),
            Substitute.For<IToolRegistry>(),
            Substitute.For<IContextManager>(),
            NullLogger<AgentService>.Instance,
            bus,
            new SessionRecorder(bus, NullLogger<SessionRecorder>.Instance));
        return new ChatViewModel(service, new EditorPaneViewModel(), new SharedChangeset());
    }

    // ----- 1. chat bottom bar -----

    /// <summary>
    /// The row under the chat input, laid out like MainWindow lays it out: the action bar in a star column, Send and
    /// Clear in the auto columns next to it. The chat column is the narrow one that is left when the editor is open
    /// (a third of the window, after the activity bar).
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void ChatBottomBar_NeverOverlapsSendOrItself_WhenTheEditorIsOpen(double windowWidth)
    {
        var chat = CreateChat();
        var bar = new ChatActionBarView { DataContext = chat, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var send = new Button { Name = "SendButton", Content = "➤ Send", Margin = new Thickness(8, 8, 0, 0), Padding = new Thickness(16, 8), MinHeight = 36 };
        var clear = new Button { Name = "ClearChatButton", Content = "🗑️", Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(4, 8, 0, 0) };
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(send, 2);
        Grid.SetColumn(clear, 3);
        var row = new Grid { Margin = new Thickness(16, 8, 16, 16), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        row.Children.Add(bar);
        row.Children.Add(send);
        row.Children.Add(clear);

        var chatColumnWidth = Math.Round((windowWidth - ActivityBarWidth) / 3);
        var host = new Border { Width = chatColumnWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = row };
        var window = new Window { Width = windowWidth, Height = 900, Content = host };
        window.Show();
        Settle(window);

        var rects = new Dictionary<string, Rect>
        {
            ["PipelineCombo"] = BoundsIn(Named<ComboBox>(bar, "PipelineCombo"), host),
            ["RunPipelineButton"] = BoundsIn(Named<Button>(bar, "RunPipelineButton"), host),
            ["CharacterCombo"] = BoundsIn(Named<ComboBox>(bar, "CharacterCombo"), host),
            ["EffortCombo"] = BoundsIn(Named<ComboBox>(bar, "EffortCombo"), host),
            ["RewindButton"] = BoundsIn(Named<Button>(bar, "RewindButton"), host),
            ["SendButton"] = BoundsIn(send, host),
            ["ClearChatButton"] = BoundsIn(clear, host),
        };
        AssertNoOverlapOrClipping(rects, new Rect(host.Bounds.Size), $"chat bar @{windowWidth}");

        // The bar itself ends before Send starts, so nothing of it can draw under the button.
        Assert.True(BoundsIn(bar, host).Right <= rects["SendButton"].Left + Tolerance,
            $"the chat action bar reaches under Send @{windowWidth}");
        window.Close();
    }

    // ----- 2. Flows header and run bar -----

    private (Window Window, FlowsView View, FlowEditorViewModel Vm) ShowFlows(double windowWidth)
    {
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters);
        vm.OpenAsync(_root).GetAwaiter().GetResult();
        vm.NewFlow();
        var view = new FlowsView { DataContext = vm };
        var window = new Window { Width = windowWidth, Height = 900, Content = new Border { Width = windowWidth - ActivityBarWidth, HorizontalAlignment = HorizontalAlignment.Left, Child = view } };
        window.Show();
        Settle(window);
        return (window, view, vm);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void FlowsRunBar_DoesNotClipTheParallelSpinner_OrOverlap(double windowWidth)
    {
        var (window, view, _) = ShowFlows(windowWidth);

        var spinner = Named<NumericUpDown>(view, "MaxParallelBox");
        Assert.True(spinner.Bounds.Width >= 90 - Tolerance, $"the parallel spinner is only {spinner.Bounds.Width}px wide @{windowWidth}");
        var text = spinner.GetVisualDescendants().OfType<TextBox>().First();
        Assert.True(text.Bounds.Width >= 28, $"the number box inside the spinner is {text.Bounds.Width}px: the digit is clipped @{windowWidth}");

        var rects = new Dictionary<string, Rect>
        {
            ["FlowNameBox"] = BoundsIn(Named<TextBox>(view, "FlowNameBox"), view),
            ["FlowDescriptionBox"] = BoundsIn(Named<TextBox>(view, "FlowDescriptionBox"), view),
            ["ParallelLabel"] = BoundsIn(Named<TextBlock>(view, "ParallelLabel"), view),
            ["MaxParallelBox"] = BoundsIn(spinner, view),
            ["RunTaskBox"] = BoundsIn(Named<TextBox>(view, "RunTaskBox"), view),
            ["RunButton"] = BoundsIn(Named<Button>(view, "RunButton"), view),
        };
        AssertNoOverlapOrClipping(rects, new Rect(view.Bounds.Size), $"flows run bar @{windowWidth}");
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void FlowsHeader_ToolsWrapInsteadOfRunningUnderTheCloseButton(double windowWidth)
    {
        var (window, view, _) = ShowFlows(windowWidth);

        var tools = Named<WrapPanel>(view, "HeaderTools");
        var close = Named<Button>(view, "CloseButton");
        var rects = new Dictionary<string, Rect>
        {
            ["HeaderTools"] = BoundsIn(tools, view),
            ["CloseButton"] = BoundsIn(close, view),
        };
        AssertNoOverlapOrClipping(rects, new Rect(view.Bounds.Size), $"flows header @{windowWidth}");

        foreach (var child in tools.Children.OfType<Control>().Where(c => c.IsVisible))
        {
            var r = BoundsIn(child, view);
            Assert.True(r.Right <= rects["CloseButton"].Left + Tolerance || r.Bottom <= rects["CloseButton"].Top + Tolerance || r.Top >= rects["CloseButton"].Bottom - Tolerance,
                $"{child.GetType().Name} {r} runs under the close button @{windowWidth}");
        }
        window.Close();
    }

    // ----- 3. Settings: one left edge -----

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void Settings_FieldsShareOneLeftEdge(double windowWidth)
    {
        Directory.CreateDirectory(_root);
        var vm = new SettingsViewModel(new ConfigurationService(_root));
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = windowWidth, Height = 1400, Content = view };
        window.Show();
        Settle(window);

        var lefts = new Dictionary<string, double>
        {
            ["ProviderComboBox"] = BoundsIn(Named<ComboBox>(view, "ProviderComboBox"), view).Left,
            ["BaseUrlBox"] = BoundsIn(Named<TextBox>(view, "BaseUrlBox"), view).Left,
            ["ModelComboBox"] = BoundsIn(Named<ComboBox>(view, "ModelComboBox"), view).Left,
            ["ApiKeyBox"] = BoundsIn(Named<TextBox>(view, "ApiKeyBox"), view).Left,
            ["ThemeComboBox"] = BoundsIn(Named<ComboBox>(view, "ThemeComboBox"), view).Left,
        };
        var first = lefts["ProviderComboBox"];
        foreach (var (name, left) in lefts)
            Assert.True(Math.Abs(left - first) <= Tolerance, $"{name} starts at x={left}, the AI Provider combo at x={first} @{windowWidth}");

        // The provider combo and the text boxes also line up with the field labels above them.
        var label = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "AI Provider");
        Assert.True(Math.Abs(BoundsIn(label, view).Left - first) <= Tolerance, $"the AI Provider label and its combo are not left-aligned @{windowWidth}");
        window.Close();
    }

    // ----- 4. one avatar per character -----

    [AvaloniaFact]
    public void FlowsCharacterList_ShowsOneAvatarPerRow()
    {
        var (window, _, vm) = ShowFlows(1366);

        Assert.NotEmpty(vm.Palette);
        foreach (var item in vm.Palette)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Avatar));
            Assert.DoesNotContain(item.Avatar, item.Label); // the row draws the avatar in its own column
        }

        vm.AddFromPalette(vm.Palette.First());
        var node = vm.Nodes.Last();
        Assert.False(string.IsNullOrWhiteSpace(node.Avatar));
        Assert.DoesNotContain(node.Avatar, node.CharacterLabel);
        Assert.DoesNotContain(node.Avatar, node.Subtitle);
        window.Close();
    }
}
