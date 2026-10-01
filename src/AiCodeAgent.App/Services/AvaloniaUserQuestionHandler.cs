using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using AiCodeAgent.Core.Agent;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Shows the agent's <c>ask_user</c> question as a modal dialog over the main window: one button per suggested
/// option, a free-text box, and Skip. Questions from parallel agents are queued so only one dialog is open at a time.
/// </summary>
public sealed class AvaloniaUserQuestionHandler : IUserQuestionHandler
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<string?> AskAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(question, options, ct));
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task<string?> ShowAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null) return null; // no window to ask in: the tool tells the model to assume

        string? answer = null;
        var dialog = new Window
        {
            Title = "The agent has a question",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var input = new TextBox
        {
            Watermark = options.Count > 0 ? "…or type your own answer" : "Type your answer",
            AcceptsReturn = false,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

        foreach (var option in options)
        {
            var choice = option;
            var button = new Button
            {
                Content = choice,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => { answer = choice; dialog.Close(); };
            panel.Children.Add(button);
        }

        var send = new Button { Content = "Send", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var skip = new Button { Content = "Skip" };
        send.Click += (_, _) => { answer = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text!.Trim(); dialog.Close(); };
        skip.Click += (_, _) => dialog.Close();

        panel.Children.Add(input);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { send, skip }
        });
        dialog.Content = panel;

        // If the run is cancelled while the dialog is open, close it and treat the question as unanswered.
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));

        await dialog.ShowDialog(owner);
        return answer;
    }
}
