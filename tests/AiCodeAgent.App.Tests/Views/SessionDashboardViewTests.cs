using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiCodeAgent.App.Tests.Views;

public class SessionDashboardViewTests
{
    /// <summary>Measures and arranges the window so the item templates are realised.</summary>
    private static void LayoutWindow(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void CreateSession_ShowsItem_AndButtonsWork()
    {
        using var manager = new SessionManager();
        var vm = new SessionDashboardViewModel(manager);
        var window = new Window
        {
            Width = 400,
            Height = 800,
            Content = new SessionDashboardView { DataContext = vm }
        };
        window.Show();

        vm.NewTask = "Add logging";
        vm.CreateSessionCommand.Execute(null);
        LayoutWindow(window);
        Dispatcher.UIThread.RunJobs();

        var session = Assert.Single(manager.Sessions);
        Assert.Equal(session.Id, manager.ActiveSession?.Id);

        // The session's buttons only exist once the item template is realised. Before the fix their bindings
        // could not be resolved and the whole dashboard threw; here each command must be bound and runnable.
        Button FindButton(string label) =>
            Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => (string?)b.Content == label);

        foreach (var label in new[] { "Select", "Pause", "Resume", "Cancel", "Close" })
        {
            var button = FindButton(label);
            Assert.NotNull(button.Command);
            Assert.True(button.Command!.CanExecute(button.CommandParameter), $"{label} cannot execute");
        }

        session.MarkRunning();
        FindButton("Pause").Command!.Execute(FindButton("Pause").CommandParameter);
        Assert.Equal(SessionStatus.Paused, session.Status);

        FindButton("Resume").Command!.Execute(FindButton("Resume").CommandParameter);
        Assert.Equal(SessionStatus.Running, session.Status);

        FindButton("Cancel").Command!.Execute(FindButton("Cancel").CommandParameter);
        Assert.Equal(SessionStatus.Cancelled, session.Status);

        FindButton("Close").Command!.Execute(FindButton("Close").CommandParameter);
        Assert.Empty(manager.Sessions);
    }

    [AvaloniaFact]
    public void Select_ActivatesTheChosenSession()
    {
        using var manager = new SessionManager();
        var vm = new SessionDashboardViewModel(manager);
        var window = new Window
        {
            Width = 400,
            Height = 800,
            Content = new SessionDashboardView { DataContext = vm }
        };
        window.Show();

        var first = manager.CreateSession("first");
        var second = manager.CreateSession("second");
        LayoutWindow(window);
        Dispatcher.UIThread.RunJobs();

        var select = window.GetVisualDescendants().OfType<Button>()
            .Where(b => (string?)b.Content == "Select")
            .Single(b => ReferenceEquals(b.DataContext, first));
        select.Command!.Execute(select.CommandParameter);

        Assert.Equal(first.Id, manager.ActiveSession?.Id);
        Assert.NotEqual(second.Id, manager.ActiveSession?.Id);
    }
}
