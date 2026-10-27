using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E4-S1: the labelled activity bar replaces the emoji toolbar.</summary>
public class ActivityBarTests
{
    private static readonly string[] AllIds =
    {
        ActivityBarIds.Chat, ActivityBarIds.Explorer, ActivityBarIds.Changes, ActivityBarIds.Flows, ActivityBarIds.Tasks,
        ActivityBarIds.Characters, ActivityBarIds.Skills, ActivityBarIds.AgentsMd,
        ActivityBarIds.Checkpoints, ActivityBarIds.Sessions, ActivityBarIds.Dashboard,
        ActivityBarIds.Settings, ActivityBarIds.Theme
    };

    /// <summary>The same groups and entries the app builds, with counting commands instead of real ones.</summary>
    private static (ActivityBarViewModel Bar, Dictionary<string, int> Clicks) CreateBar(bool expanded = false)
    {
        var clicks = AllIds.ToDictionary(id => id, _ => 0);
        ActivityBarItem Item(string id, string label, string icon, string shortcut = "", string description = "") =>
            new(id, label, icon, new RelayCommand(() => clicks[id]++), shortcut, description);

        var bar = new ActivityBarViewModel(
            new[]
            {
                new ActivityBarGroup("WORK", new[]
                {
                    Item(ActivityBarIds.Chat, "Chat", ActivityBarIcons.Chat),
                    Item(ActivityBarIds.Explorer, "Explorer", ActivityBarIcons.Explorer, "Ctrl+B"),
                    Item(ActivityBarIds.Changes, "Changes", ActivityBarIcons.Changes, "Ctrl+Shift+G", "stage, diff and commit"),
                    Item(ActivityBarIds.Flows, "Flows", ActivityBarIcons.Flows),
                    Item(ActivityBarIds.Tasks, "Tasks", ActivityBarIcons.Tasks),
                }),
                new ActivityBarGroup("TEAM", new[]
                {
                    Item(ActivityBarIds.Characters, "Characters", ActivityBarIcons.Characters),
                    Item(ActivityBarIds.Skills, "Skills", ActivityBarIcons.Skills),
                    Item(ActivityBarIds.AgentsMd, "AGENTS.md", ActivityBarIcons.AgentsMd),
                }),
                new ActivityBarGroup("HISTORY", new[]
                {
                    Item(ActivityBarIds.Checkpoints, "Checkpoints", ActivityBarIcons.Checkpoints),
                    Item(ActivityBarIds.Sessions, "Sessions", ActivityBarIcons.Sessions),
                    Item(ActivityBarIds.Dashboard, "Dashboard", ActivityBarIcons.Dashboard),
                }),
            },
            new[]
            {
                Item(ActivityBarIds.Settings, "Settings", ActivityBarIcons.Settings, "Ctrl+,"),
                Item(ActivityBarIds.Theme, "Theme", ActivityBarIcons.Theme),
            },
            expanded);
        return (bar, clicks);
    }

    private static Window Show(ActivityBarViewModel bar)
    {
        var window = new Window { Width = 400, Height = 900, Content = new ActivityBarView { DataContext = bar } };
        window.Show();
        Layout(window);
        return window;
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    // ----- view-model -----

    [Fact]
    public void Bar_HasTheGroupsOfTheStory_InOrder()
    {
        var (bar, _) = CreateBar();
        Assert.Equal(new[] { "WORK", "TEAM", "HISTORY" }, bar.Groups.Select(g => g.Title));
        Assert.Equal(new[] { "Chat", "Explorer", "Changes", "Flows", "Tasks" }, bar.Groups[0].Items.Select(i => i.Label));
        Assert.Equal(new[] { "Characters", "Skills", "AGENTS.md" }, bar.Groups[1].Items.Select(i => i.Label));
        Assert.Equal(new[] { "Checkpoints", "Sessions", "Dashboard" }, bar.Groups[2].Items.Select(i => i.Label));
        Assert.Equal(new[] { "Settings", "Theme" }, bar.BottomItems.Select(i => i.Label));
    }

    [Fact]
    public void SetActive_MarksExactlyTheGivenEntries()
    {
        var (bar, _) = CreateBar();
        bar.SetActive(new[] { ActivityBarIds.Changes, ActivityBarIds.Explorer });
        Assert.Equal(new[] { ActivityBarIds.Explorer, ActivityBarIds.Changes },
            bar.AllItems.Where(i => i.IsActive).Select(i => i.Id));

        bar.SetActive(new[] { ActivityBarIds.Tasks });
        Assert.Equal(new[] { ActivityBarIds.Tasks }, bar.AllItems.Where(i => i.IsActive).Select(i => i.Id));

        bar.SetActive(Array.Empty<string>());
        Assert.DoesNotContain(bar.AllItems, i => i.IsActive);
    }

    [Fact]
    public void Badge_ShowsOnlyWhenPositive_AndCapsAt99()
    {
        var (bar, _) = CreateBar();
        var changes = bar.Find(ActivityBarIds.Changes)!;
        Assert.False(changes.HasBadge);

        bar.SetBadge(ActivityBarIds.Changes, 3);
        Assert.True(changes.HasBadge);
        Assert.Equal("3", changes.BadgeText);

        bar.SetBadge(ActivityBarIds.Changes, 250);
        Assert.Equal("99+", changes.BadgeText);

        bar.SetBadge(ActivityBarIds.Changes, -4);
        Assert.False(changes.HasBadge);

        bar.SetBadge("no-such-entry", 5); // ignored
    }

    [Fact]
    public void Expanding_ShowsLabelsAndGroupTitles_AndRaisesTheEventOnce()
    {
        var (bar, _) = CreateBar();
        var seen = new List<bool>();
        bar.ExpandedChanged += seen.Add;

        Assert.All(bar.AllItems, i => Assert.False(i.ShowLabel));
        Assert.All(bar.Groups, g => Assert.True(g.ShowSeparator));
        Assert.Equal(ActivityBarViewModel.CollapsedWidth, bar.BarWidth);

        bar.ToggleExpandedCommand.Execute(null);

        Assert.All(bar.AllItems, i => Assert.True(i.ShowLabel));
        Assert.All(bar.Groups, g => Assert.True(g.ShowTitle));
        Assert.Equal(ActivityBarViewModel.ExpandedWidth, bar.BarWidth);
        Assert.Equal(new[] { true }, seen);
    }

    [Fact]
    public void StartingExpanded_ShowsLabelsFromTheStart()
    {
        var (bar, _) = CreateBar(expanded: true);
        Assert.All(bar.AllItems, i => Assert.True(i.ShowLabel));
        Assert.Equal(ActivityBarViewModel.ExpandedWidth, bar.BarWidth);
    }

    [Fact]
    public void Tooltips_ShowTheShortcut()
    {
        var (bar, _) = CreateBar();
        Assert.Equal("Changes — stage, diff and commit (Ctrl+Shift+G)", bar.Find(ActivityBarIds.Changes)!.ToolTip);
        Assert.Equal("Explorer (Ctrl+B)", bar.Find(ActivityBarIds.Explorer)!.ToolTip);
        Assert.Equal("Settings (Ctrl+,)", bar.Find(ActivityBarIds.Settings)!.ToolTip);
        Assert.Equal("Flows", bar.Find(ActivityBarIds.Flows)!.ToolTip);
    }

    [Fact]
    public void EveryIcon_IsAVectorPath_NotAnEmoji()
    {
        var (bar, _) = CreateBar();
        foreach (var item in bar.AllItems)
        {
            Assert.NotNull(item.Icon);
            Assert.StartsWith("M", item.IconData);
            Assert.All(item.IconData, c => Assert.True(c < 0x80, $"{item.Id} icon has a non-ASCII character"));
            // Labels and tooltips use plain text too (the em dash is the only symbol): no glyph that needs an emoji font.
            Assert.All(item.Label + item.ToolTip, c => Assert.True(c < 0x2100, $"{item.Id} text has an emoji-range character"));
        }
        // Each entry has its own icon.
        Assert.Equal(bar.AllItems.Count, bar.AllItems.Select(i => i.IconData).Distinct().Count());
    }

    // ----- view -----

    private static Border BarRoot(Window window) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ActivityBarRoot");

    [AvaloniaFact]
    public void View_RendersOneButtonPerEntry_WithIconAndTooltip()
    {
        var (bar, clicks) = CreateBar();
        var window = Show(bar);

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(bar.AllItems.Count + 1, buttons.Count); // + the collapse/expand button

        foreach (var item in bar.AllItems)
        {
            var button = Assert.Single(buttons, b => b.DataContext == item);
            Assert.NotNull(button.Command);
            Assert.Equal(item.ToolTip, ToolTip.GetTip(button) as string);
            Assert.NotNull(Assert.Single(button.GetVisualDescendants().OfType<PathIcon>()).Data);

            button.Command!.Execute(button.CommandParameter);
            Assert.Equal(1, clicks[item.Id]);
        }
    }

    [AvaloniaFact]
    public void View_ActiveEntry_GetsTheActiveClass()
    {
        var (bar, _) = CreateBar();
        var window = Show(bar);

        bar.SetActive(new[] { ActivityBarIds.Dashboard });
        Layout(window);

        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is ActivityBarItem).ToList();
        var active = Assert.Single(buttons, b => b.Classes.Contains("active"));
        Assert.Equal(ActivityBarIds.Dashboard, ((ActivityBarItem)active.DataContext!).Id);
    }

    [AvaloniaFact]
    public void View_CollapsedHidesLabels_ExpandedShowsThem_AndTheBarGrows()
    {
        var (bar, _) = CreateBar();
        var window = Show(bar);

        bool LabelVisible(string text) =>
            window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

        Assert.False(LabelVisible("Changes"));
        Assert.False(LabelVisible("WORK"));
        Assert.Equal(ActivityBarViewModel.CollapsedWidth, BarRoot(window).Bounds.Width);

        bar.IsExpanded = true;
        Layout(window);

        Assert.True(LabelVisible("Changes"));
        Assert.True(LabelVisible("AGENTS.md"));
        Assert.True(LabelVisible("WORK"));
        Assert.True(LabelVisible("HISTORY"));
        Assert.Equal(ActivityBarViewModel.ExpandedWidth, BarRoot(window).Bounds.Width);
    }

    [AvaloniaFact]
    public void View_BadgeAppearsOnTheEntry()
    {
        var (bar, _) = CreateBar();
        var window = Show(bar);

        bool BadgeVisible(string text) =>
            window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.FontSize == 9 && t.IsEffectivelyVisible);

        Assert.False(BadgeVisible("5"));
        bar.SetBadge(ActivityBarIds.Changes, 5);
        Layout(window);
        Assert.True(BadgeVisible("5"));

        bar.SetBadge(ActivityBarIds.Changes, 0);
        Layout(window);
        Assert.False(BadgeVisible("5"));
    }

    // ----- navigation coverage (source scan: MainViewModel cannot be built without the whole DI container) -----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AiCodeAgent.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string ReadApp(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "AiCodeAgent.App" }.Concat(relative).ToArray()));

    [Fact]
    public void EveryFormerToolbarAction_IsReachableFromTheBarOrThePalette()
    {
        var palette = ReadApp("ViewModels", "MainViewModel.cs");
        var bar = ReadApp("ViewModels", "MainViewModel.ActivityBar.cs");

        // Activity bar: command used by each entry.
        foreach (var command in new[]
                 {
                     "ShowChatCommand", "ToggleFileExplorerCommand", "ToggleSourceControlCommand", "ToggleFlowsCommand",
                     "ToggleBackgroundTasksCommand", "ToggleCharactersCommand", "ToggleSkillsCommand", "ToggleAgentsMdCommand",
                     "ToggleCheckpointBrowserCommand", "ToggleSessionHistoryCommand", "ToggleSessionDashboardCommand",
                     "NavigateToSettingsCommand", "ToggleThemeCommand"
                 })
            Assert.Contains(command, bar);

        // Everything else from the old toolbar: command palette entries (palette and quick open also keep a top-bar button).
        foreach (var id in new[]
                 {
                     "nav.terminal", "nav.diffViewer", "view.togglePreview", "editor.openFile", "view.refreshExplorer",
                     "nav.slideMode", "index.goToFile", "view.toggleTheme"
                 })
            Assert.Contains($"Id = \"{id}\"", palette);
    }

    [Fact]
    public void MainWindow_UsesTheActivityBar_AndNoMoreEmojiToolbar()
    {
        var xaml = ReadApp("Views", "MainWindow.axaml");

        Assert.Contains("<views:ActivityBarView", xaml);
        Assert.Contains("Name=\"SidePanelHost\"", xaml);

        // The old icon-only emoji buttons are gone.
        foreach (var emoji in new[] { "🔎", "💻", "📄", "🌿", "🧠", "🧵", "🧭", "🔄", "🌓", "🎞", "⚙", "⌘" })
            Assert.DoesNotContain(emoji, xaml);
        Assert.DoesNotContain("Content=\"🔀\"", xaml);
    }
}
