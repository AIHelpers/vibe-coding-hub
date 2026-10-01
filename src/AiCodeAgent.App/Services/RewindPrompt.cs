using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AiCodeAgent.Core.Sessions;

namespace AiCodeAgent.App.Services;

/// <summary>What the user chose to do at a rewind point.</summary>
public enum RewindAction
{
    /// <summary>Drop the conversation from that message on; files stay.</summary>
    Chat,
    /// <summary>Restore files to how they were before that message; the conversation stays.</summary>
    Code,
    /// <summary>Both chat and files.</summary>
    Both,
    /// <summary>Copy the conversation before that message into a new session; nothing is changed.</summary>
    Fork
}

public sealed record RewindChoice(int Number, RewindAction Action);

/// <summary>Asks the user which message to rewind or fork from, and what to undo.</summary>
public interface IRewindPrompt
{
    /// <summary>Returns null when the user cancels.</summary>
    Task<RewindChoice?> ChooseAsync(IReadOnlyList<RewindPoint> points);
}

/// <summary>Modal dialog over the main window: a list of the user's messages plus Chat / Code / Both / Fork buttons.</summary>
public sealed class AvaloniaRewindPrompt : IRewindPrompt
{
    public Task<RewindChoice?> ChooseAsync(IReadOnlyList<RewindPoint> points) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowAsync(points));

    private static async Task<RewindChoice?> ShowAsync(IReadOnlyList<RewindPoint> points)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null || points.Count == 0) return null;

        RewindChoice? result = null;
        var dialog = new Window
        {
            Title = "Rewind or fork the conversation",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var list = new ListBox
        {
            Height = 260,
            ItemsSource = points.Select(Describe).ToList(),
            SelectedIndex = points.Count - 1
        };

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Text = "Pick the message to go back to. The chosen message and everything after it is undone " +
                   "(you can edit and resend it). \"Code\" restores files the agent changed since then using its edit " +
                   "checkpoints. \"Fork\" copies the conversation before that message into a new session and changes nothing else."
        };

        Button Make(string text, RewindAction action, string tip)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                if (list.SelectedIndex < 0) return;
                result = new RewindChoice(points[list.SelectedIndex].Number, action);
                dialog.Close();
            };
            return b;
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                Make("Rewind chat", RewindAction.Chat, "Remove this message and everything after it; files stay as they are"),
                Make("Restore code", RewindAction.Code, "Put files back the way they were before this message; the chat stays"),
                Make("Rewind both", RewindAction.Both, "Go back to exactly the state before this message"),
                Make("Fork", RewindAction.Fork, "Continue from here in a new session; the current one is kept"),
                cancel
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = { hint, list, buttons }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    private static string Describe(RewindPoint p)
    {
        var files = p.FilesChanged switch { 0 => "no file changes", 1 => "1 file changed", var n => $"{n} files changed" };
        return $"#{p.Number}  {p.Preview}   ·   {p.Timestamp:t}   ·   {files}";
    }
}
