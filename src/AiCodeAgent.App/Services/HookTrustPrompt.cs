using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.App.Services;

/// <summary>
/// A project's <c>.aiagent/settings.json</c> can declare hooks — arbitrary shell commands that run on
/// session start and around every tool call. Opening a cloned repository must never run those silently,
/// so the app lists them and asks once; the decision is remembered per project file content.
/// </summary>
public static class HookTrustPrompt
{
    public static async Task ConfirmIfNeededAsync(Window owner, IHookRegistry registry, string? workingDirectory)
    {
        if (registry.UntrustedProjectHooks.Count == 0 || string.IsNullOrWhiteSpace(workingDirectory))
            return;

        var list = string.Join(Environment.NewLine + Environment.NewLine, registry.UntrustedProjectHooks.Select(h =>
            $"{h.Name}  [{h.Event}{(h.Blocking ? ", blocking" : "")}]{Environment.NewLine}{h.Command}"));

        var trust = false;
        var dialog = new Window
        {
            Title = "Run this project's hooks?",
            Width = 640,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var trustButton = new Button { Content = "Trust and run hooks", Margin = new Thickness(0, 0, 8, 0) };
        var ignoreButton = new Button { Content = "Ignore (don't run)" };
        trustButton.Click += (_, _) => { trust = true; dialog.Close(); };
        ignoreButton.Click += (_, _) => dialog.Close();

        dialog.Content = new DockPanel
        {
            Margin = new Thickness(16),
            LastChildFill = true,
            Children =
            {
                WithDock(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { trustButton, ignoreButton }
                }, Dock.Bottom),
                WithDock(new TextBlock
                {
                    Text = "This project defines hooks — shell commands that run automatically on your machine. " +
                           "Only trust them if you know where this project came from.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                }, Dock.Top),
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = list,
                        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        await dialog.ShowDialog(owner);

        if (trust)
            await registry.TrustProjectAsync(workingDirectory);
    }

    private static T WithDock<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
