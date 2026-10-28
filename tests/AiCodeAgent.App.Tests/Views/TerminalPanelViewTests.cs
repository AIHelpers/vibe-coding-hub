using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiCodeAgent.App.Controls;
using AiCodeAgent.App.Tests.ViewModels;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E4-S4: the terminal panel in a window — tabs, the terminal control's size and keyboard input.</summary>
public class TerminalPanelViewTests
{
    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, TerminalPanelView Panel, TerminalViewModel Terminal, List<FakePty> Ptys) Create(double width = 900, double height = 500)
    {
        var ptys = new List<FakePty>();
        var terminal = new TerminalViewModel(options =>
        {
            var pty = new FakePty(options);
            ptys.Add(pty);
            return pty;
        }, action => action());
        terminal.Configure(
            () => OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") : "/bin/sh",
            () => Path.GetTempPath());
        var panel = new TerminalPanelView { DataContext = terminal };
        var window = new Window { Width = width, Height = height, Content = panel };
        window.Show();
        Layout(window);
        return (window, panel, terminal, ptys);
    }

    private static TerminalControl Host(TerminalPanelView panel) => panel.FindControl<TerminalControl>("ShellHost")!;

    [AvaloniaFact]
    public void Initially_ShowsTheAgentLog_AndNoTerminal()
    {
        var (_, panel, terminal, ptys) = Create();

        Assert.False(Host(panel).IsVisible);
        Assert.True(panel.FindControl<ScrollViewer>("AgentLog")!.IsVisible);
        Assert.Empty(ptys);
        Assert.Single(terminal.Tabs);
    }

    [AvaloniaFact]
    public void NewShell_ShowsTheTerminal_SizedToTheControl()
    {
        var (window, panel, terminal, ptys) = Create();

        terminal.NewShellCommand.Execute(null);
        Layout(window);

        var host = Host(panel);
        Assert.True(host.IsVisible);
        Assert.False(panel.FindControl<ScrollViewer>("AgentLog")!.IsVisible);
        Assert.Same(terminal.SelectedShell, host.Session);
        var (cols, rows) = host.GridSize;
        Assert.True(cols > 40, $"cols {cols}");
        Assert.True(rows > 5, $"rows {rows}");
        Assert.Equal(cols, terminal.SelectedShell!.Emulator.Cols);
        Assert.Equal(rows, terminal.SelectedShell.Emulator.Rows);
        Assert.Equal((cols, rows), ptys[0].LastResize);
    }

    [AvaloniaFact]
    public void ResizingTheWindow_ReflowsTheTerminal()
    {
        var (window, panel, terminal, ptys) = Create(900, 500);
        terminal.NewShellCommand.Execute(null);
        Layout(window);
        var before = Host(panel).GridSize;

        window.Width = 500;
        window.Height = 300;
        Layout(window);

        var after = Host(panel).GridSize;
        Assert.True(after.Cols < before.Cols);
        Assert.True(after.Rows < before.Rows);
        Assert.Equal(after, (terminal.SelectedShell!.Emulator.Cols, terminal.SelectedShell.Emulator.Rows));
        Assert.Equal(after, ptys[0].LastResize);
    }

    [AvaloniaFact]
    public void Tabs_ListEveryShell_AndTheAgentOutput()
    {
        var (window, panel, terminal, _) = Create();
        terminal.NewShellCommand.Execute(null);
        terminal.NewShellCommand.Execute(null);
        Layout(window);

        var strip = panel.FindControl<ListBox>("TabStrip")!;
        Assert.Equal(3, strip.ItemCount);
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Agent output", texts);
        Assert.Contains(terminal.Tabs[0].Title, texts);
        Assert.Contains(terminal.Tabs[1].Title, texts);
        // only shells have a close button
        var closeButtons = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "CloseTabButton" && b.IsEffectivelyVisible).ToList();
        Assert.Equal(2, closeButtons.Count);
    }

    [AvaloniaFact]
    public void ChatActions_AreOnlyOfferedForShellTabs()
    {
        var (window, panel, terminal, _) = Create();
        Assert.False(panel.FindControl<Button>("SendSelectionButton")!.IsEffectivelyVisible);
        Assert.False(panel.FindControl<Button>("AskAgentButton")!.IsEffectivelyVisible);

        terminal.NewShellCommand.Execute(null);
        Layout(window);

        Assert.True(panel.FindControl<Button>("SendSelectionButton")!.IsEffectivelyVisible);
        Assert.True(panel.FindControl<Button>("AskAgentButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AskTheAgent_WithoutSelection_SendsTheLastOutput()
    {
        var (window, panel, terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        Layout(window);
        ptys[0].Emit("error CS0246: type not found\r\n");
        var asked = new List<string>();
        terminal.AskAgentRequested += asked.Add;

        panel.FindControl<Button>("AskAgentButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Single(asked);
        Assert.Contains("CS0246", asked[0]);
    }

    [AvaloniaFact]
    public void Typing_ReachesTheShell()
    {
        var (window, panel, terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        Layout(window);
        Host(panel).Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("dotnet test");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("dotnet test\r\u001b[A", ptys[0].AllWritten);
    }

    [AvaloniaFact]
    public void CtrlC_WithoutSelection_InterruptsTheProgram()
    {
        var (window, panel, terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        Layout(window);
        Host(panel).Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("\u0003", ptys[0].AllWritten);
    }

    [AvaloniaFact]
    public void Wheel_ScrollsTheHistory_ButMovesTheCursorInFullScreenPrograms()
    {
        var (window, panel, terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        Layout(window);
        Host(panel).Focus();
        var point = new Point(300, 300);

        for (var i = 0; i < 200; i++) ptys[0].Emit("line " + i + "\r\n");
        window.MouseWheel(point, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(string.Empty, ptys[0].AllWritten);

        ptys[0].Emit("\u001b[?1049h");
        window.MouseWheel(point, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("\u001b[A", ptys[0].AllWritten);
    }

    [AvaloniaFact]
    public void TheTerminalIsNotRebuilt_WhenSwitchingTabs_AndKeepsItsOutput()
    {
        var (window, panel, terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        terminal.NewShellCommand.Execute(null);
        ptys[0].Emit("from the first shell");
        Layout(window);

        terminal.SelectedTab = terminal.Tabs[0];
        Layout(window);

        Assert.Same(terminal.Tabs[0], Host(panel).Session);
        Assert.Contains("from the first shell", ((ShellTabViewModel)terminal.Tabs[0]).Emulator.GetScreenText());
        Assert.Equal(2, ptys.Count);
    }
}
