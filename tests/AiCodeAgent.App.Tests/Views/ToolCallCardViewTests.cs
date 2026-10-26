using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>
/// E2-S2: tool cards and the terminal header must never print a raw bool ("True"/"False"),
/// and one tool call must render as one card.
/// </summary>
public class ToolCallCardViewTests
{
    private static void LayoutWindow(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, ObservableCollection<ToolCallCardViewModel> Cards, TerminalViewModel Terminal) CreateWindow()
    {
        var cards = new ObservableCollection<ToolCallCardViewModel>();
        var terminal = new TerminalViewModel();
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = new StackPanel
            {
                Children =
                {
                    new TerminalHeaderView { DataContext = terminal },
                    new ItemsControl
                    {
                        ItemsSource = cards,
                        ItemTemplate = new FuncDataTemplate<ToolCallCardViewModel>((_, _) => new ToolCallCardView())
                    }
                }
            }
        };
        window.Show();
        LayoutWindow(window);
        return (window, cards, terminal);
    }

    private static IReadOnlyList<string> AllTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static IReadOnlyList<string> VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty)
            .ToList();

    private static void AssertNoBoolText(Window window)
    {
        foreach (var text in AllTexts(window))
        {
            Assert.False(string.Equals(text.Trim(), "True", StringComparison.OrdinalIgnoreCase), "A TextBlock shows \"True\"");
            Assert.False(string.Equals(text.Trim(), "False", StringComparison.OrdinalIgnoreCase), "A TextBlock shows \"False\"");
        }
    }

    [AvaloniaFact]
    public void ScriptedToolRun_ShowsNoBools_OneCardPerCall_AndReadableHeader()
    {
        var (window, cards, terminal) = CreateWindow();

        var read = new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "src/Todo.Core/TodoService.cs" } };
        var build = new ToolCall { Name = "execute_command", Arguments = new() { ["command"] = "dotnet build" } };

        // Calls start: cards show "running…", terminal runs.
        ToolCallCardList.OnStarted(cards, read);
        ToolCallCardList.OnStarted(cards, build);
        terminal.IsRunning = true;
        LayoutWindow(window);

        AssertNoBoolText(window);
        Assert.Equal(2, window.GetVisualDescendants().OfType<ToolCallCardView>().Count());
        Assert.Equal(2, VisibleTexts(window).Count(t => t == "running…"));
        Assert.Contains("running", VisibleTexts(window));

        // Results arrive: same cards updated, no extra card.
        ToolCallCardList.OnEnded(cards, read, new ToolResult { Content = "class TodoService {}" }, TimeSpan.FromTicks(94395));
        ToolCallCardList.OnEnded(cards, build, new ToolResult { Content = "error CS1002: ; expected", IsError = true }, TimeSpan.FromSeconds(11.8));
        terminal.IsRunning = false;
        LayoutWindow(window);

        AssertNoBoolText(window);
        var views = window.GetVisualDescendants().OfType<ToolCallCardView>().ToList();
        Assert.Equal(2, views.Count);

        var visible = VisibleTexts(window);
        Assert.DoesNotContain("running…", visible);
        Assert.DoesNotContain("running", visible);

        // Successful read_file: "🔧 Read file · src/Todo.Core/TodoService.cs · 0.0 s", no badge.
        var readView = views[0];
        Assert.Equal("🔧 Read file", readView.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal("· src/Todo.Core/TodoService.cs", readView.FindControl<TextBlock>("SubtitleText")!.Text);
        Assert.Equal("· 0.0 s", readView.FindControl<TextBlock>("DurationText")!.Text);
        Assert.False(readView.FindControl<Border>("ErrorBadge")!.IsVisible);
        Assert.False(readView.FindControl<Border>("RunningBadge")!.IsVisible);

        // Failed call: red "error" badge and the error text.
        var buildView = views[1];
        Assert.True(buildView.FindControl<Border>("ErrorBadge")!.IsVisible);
        var errorText = buildView.FindControl<TextBlock>("ErrorText")!;
        Assert.True(errorText.IsVisible);
        Assert.Equal("error CS1002: ; expected", errorText.Text);
        Assert.Contains("error", visible);
    }

    [AvaloniaFact]
    public void TerminalHeader_NeverShowsBool()
    {
        var (window, _, terminal) = CreateWindow();
        var indicator = window.GetVisualDescendants().OfType<TerminalHeaderView>().Single()
            .FindControl<StackPanel>("RunningIndicator")!;

        Assert.False(indicator.IsVisible);
        AssertNoBoolText(window);

        terminal.IsRunning = true;
        LayoutWindow(window);
        Assert.True(indicator.IsVisible);
        AssertNoBoolText(window);

        terminal.IsRunning = false;
        LayoutWindow(window);
        Assert.False(indicator.IsVisible);
        AssertNoBoolText(window);
    }

    [AvaloniaFact]
    public void ExpandButton_TogglesDetails()
    {
        var (window, cards, _) = CreateWindow();
        var call = new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "a.cs" } };
        var card = ToolCallCardList.OnEnded(cards, call, new ToolResult { Content = "body" }, TimeSpan.FromSeconds(0.3));
        LayoutWindow(window);

        var button = window.GetVisualDescendants().OfType<ToolCallCardView>().Single()
            .GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "▶");
        Assert.NotNull(button.Command);
        button.Command!.Execute(button.CommandParameter);
        LayoutWindow(window);

        Assert.True(card.IsExpanded);
        Assert.Equal("▼", (string?)button.Content);
        Assert.Contains("body", VisibleTexts(window).Concat(
            window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")));
    }
}
