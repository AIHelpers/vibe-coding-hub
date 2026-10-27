using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Ids of the activity bar entries (E4-S1). Stable: tests and persistence refer to them.</summary>
public static class ActivityBarIds
{
    public const string Chat = "chat";
    public const string Explorer = "explorer";
    public const string Changes = "changes";
    public const string Flows = "flows";
    public const string Tasks = "tasks";
    public const string Characters = "characters";
    public const string Skills = "skills";
    public const string AgentsMd = "agents-md";
    public const string Checkpoints = "checkpoints";
    public const string Sessions = "sessions";
    public const string Dashboard = "dashboard";
    public const string Settings = "settings";
    public const string Theme = "theme";
}

/// <summary>
/// Vector icons (Material Design icon paths on a 24x24 grid) for the activity bar. They are drawn by the app itself,
/// so the bar looks the same on Windows, macOS and Linux; no emoji font is involved.
/// </summary>
public static class ActivityBarIcons
{
    public const string Menu = "M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z";
    public const string Chat = "M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H6l-2 2V4h16v12z";
    public const string Explorer = "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z";
    public const string Changes = "M17 20.41L18.41 19 15 15.59 13.59 17 17 20.41zM7.5 8H11v5.59L5.59 19 7 20.41l6-6V8h3.5L12 3.5 7.5 8z";
    public const string Flows = "M22 11V3h-7v3H9V3H2v8h7V8h2v10h4v3h7v-8h-7v3h-2V8h2v3z";
    public const string Tasks = "M14 10H2v2h12v-2zm0-4H2v2h12V6zM2 16h8v-2H2v2zm19.5-4.5L23 13l-6.99 7-4.51-4.5L13 14l3.01 3 5.49-5.5z";
    public const string Characters = "M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1.66 0-3 1.34-3 3s1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5C6.34 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z";
    public const string Skills = "M20.5 11H19V7c0-1.1-.9-2-2-2h-4V3.5C13 2.12 11.88 1 10.5 1S8 2.12 8 3.5V5H4c-1.1 0-1.99.9-1.99 2v3.8H3.5c1.49 0 2.7 1.21 2.7 2.7s-1.21 2.7-2.7 2.7H2V20c0 1.1.9 2 2 2h3.8v-1.5c0-1.49 1.21-2.7 2.7-2.7 1.49 0 2.7 1.21 2.7 2.7V22H17c1.1 0 2-.9 2-2v-4h1.5c1.38 0 2.5-1.12 2.5-2.5S21.88 11 20.5 11z";
    public const string AgentsMd = "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z";
    public const string Checkpoints = "M13 3c-4.97 0-9 4.03-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42C8.27 19.99 10.51 21 13 21c4.97 0 9-4.03 9-9s-4.03-9-9-9zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z";
    public const string Sessions = "M21 6h-2v9H6v2c0 .55.45 1 1 1h11l4 4V7c0-.55-.45-1-1-1zm-4 6V3c0-.55-.45-1-1-1H3c-.55 0-1 .45-1 1v14l4-4h10c.55 0 1-.45 1-1z";
    public const string Dashboard = "M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z";
    public const string Settings = "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z";
    public const string Theme = "M20 8.69V4h-4.69L12 .69 8.69 4H4v4.69L.69 12 4 15.31V20h4.69L12 23.31 15.31 20H20v-4.69L23.31 12 20 8.69zM12 18c-3.31 0-6-2.69-6-6s2.69-6 6-6 6 2.69 6 6-2.69 6-6 6zm0-10v8c2.21 0 4-1.79 4-4s-1.79-4-4-4z";
    public const string Search = "M15.5 14h-.79l-.28-.27A6.471 6.471 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";
    public const string Commands = "M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z";
}

/// <summary>One button of the activity bar: a vector icon, a label (shown when the bar is expanded), a tooltip with the shortcut.</summary>
public partial class ActivityBarItem : ObservableObject
{
    public ActivityBarItem(string id, string label, string iconData, ICommand command,
        string shortcut = "", string description = "", string badgeColor = "#E5A100")
    {
        Id = id;
        Label = label;
        IconData = iconData;
        Icon = StreamGeometry.Parse(iconData);
        Command = command;
        Shortcut = shortcut;
        BadgeBrush = Brush.Parse(badgeColor);
        var text = string.IsNullOrWhiteSpace(description) ? label : $"{label} — {description}";
        ToolTip = string.IsNullOrEmpty(shortcut) ? text : $"{text} ({shortcut})";
    }

    public string Id { get; }
    public string Label { get; }
    public string IconData { get; }
    public Geometry Icon { get; }
    public ICommand Command { get; }
    public string Shortcut { get; }

    /// <summary>Tooltip, e.g. "Changes — stage, diff and commit (Ctrl+Shift+G)". It is the only label while the bar is collapsed.</summary>
    public string ToolTip { get; }

    public IBrush BadgeBrush { get; }

    /// <summary>True while the panel or page this entry opens is showing.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The label is visible (expanded bar).</summary>
    [ObservableProperty]
    private bool _showLabel;

    /// <summary>Number on the badge (changed files, running tasks); 0 hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText))]
    private int _badgeCount;

    public bool HasBadge => BadgeCount > 0;

    public string BadgeText => BadgeCount > 99 ? "99+" : BadgeCount.ToString();
}

/// <summary>A titled group of entries (Work, Team, History).</summary>
public sealed partial class ActivityBarGroup : ObservableObject
{
    public ActivityBarGroup(string title, IReadOnlyList<ActivityBarItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<ActivityBarItem> Items { get; }

    /// <summary>The title is written out (expanded bar); a collapsed bar shows a thin separator instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeparator))]
    private bool _showTitle;

    public bool ShowSeparator => !ShowTitle;
}

/// <summary>
/// The VS Code-style activity bar on the left: Work · Team · History groups plus Settings and Theme at the bottom.
/// It only knows its entries; <see cref="MainViewModel"/> supplies the commands and keeps <see cref="ActivityBarItem.IsActive"/>
/// and the badges up to date.
/// </summary>
public partial class ActivityBarViewModel : ObservableObject
{
    public const double CollapsedWidth = 48;
    public const double ExpandedWidth = 184;

    public ActivityBarViewModel(IReadOnlyList<ActivityBarGroup> groups, IReadOnlyList<ActivityBarItem> bottomItems, bool expanded = false)
    {
        Groups = groups;
        BottomItems = bottomItems;
        AllItems = groups.SelectMany(g => g.Items).Concat(bottomItems).ToList();
        IsExpanded = expanded;
        ApplyExpanded();
    }

    public IReadOnlyList<ActivityBarGroup> Groups { get; }
    public IReadOnlyList<ActivityBarItem> BottomItems { get; }
    public IReadOnlyList<ActivityBarItem> AllItems { get; }

    /// <summary>Labels are shown next to the icons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarWidth))]
    private bool _isExpanded;

    public double BarWidth => IsExpanded ? ExpandedWidth : CollapsedWidth;

    /// <summary>Raised after the user collapses or expands the bar, so the choice can be saved.</summary>
    public event Action<bool>? ExpandedChanged;

    partial void OnIsExpandedChanged(bool value)
    {
        ApplyExpanded();
        ExpandedChanged?.Invoke(value);
    }

    private void ApplyExpanded()
    {
        foreach (var item in AllItems) item.ShowLabel = IsExpanded;
        foreach (var group in Groups) group.ShowTitle = IsExpanded;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public ActivityBarItem? Find(string id) => AllItems.FirstOrDefault(i => i.Id == id);

    /// <summary>Marks exactly the given entries as active.</summary>
    public void SetActive(IEnumerable<string> activeIds)
    {
        var set = new HashSet<string>(activeIds);
        foreach (var item in AllItems) item.IsActive = set.Contains(item.Id);
    }

    /// <summary>Sets a badge number; unknown ids are ignored.</summary>
    public void SetBadge(string id, int count)
    {
        if (Find(id) is { } item) item.BadgeCount = Math.Max(0, count);
    }
}
