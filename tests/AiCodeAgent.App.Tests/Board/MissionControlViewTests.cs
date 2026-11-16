using System;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Board;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiCodeAgent.App.Tests.Board;

/// <summary>E11-S1: the board view loads, binds and shows a card in each column.</summary>
public sealed class MissionControlViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mission-view-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void TheView_ShowsOneCardInEachColumn_WithItsActions()
    {
        Directory.CreateDirectory(_root);
        var storePath = Path.Combine(_root, "board.json");
        var seed = new TaskBoard(new BoardStore(storePath));
        var backlog = seed.Add("Backlog task").Id;
        var running = seed.Add("Running task").Id;
        var review = seed.Add("Review task").Id;
        var done = seed.Add("Done task").Id;
        seed.BeginRun(running);
        seed.BeginRun(review);
        seed.EndRun(review, CardOutcome.Completed, c => c with { Branch = "agent/x/review-task", Summary = "All good.", CostUsd = 0.12m });
        seed.BeginRun(done);
        seed.EndRun(done, CardOutcome.Completed);
        seed.MarkDone(done);

        var vm = new MissionControlViewModel(storeFor: _ => new BoardStore(storePath));
        vm.SetProject(_root);
        // The seeded "running" card was cut off by the restart, so it shows up in Needs review as interrupted.
        var view = new MissionControlView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Backlog task", texts);
        Assert.Contains("Review task", texts);
        Assert.Contains("Done task", texts);
        Assert.Contains("Running task", texts);
        Assert.Contains("BACKLOG · 1", texts);
        Assert.Contains("NEEDS REVIEW · 2", texts);
        Assert.Contains("DONE · 1", texts);
        Assert.Contains("agent/x/review-task", texts);
        Assert.Contains("⚠ Interrupted", texts);

        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("✓ Mark done", buttons);
        Assert.Contains("Back to backlog", buttons); // the interrupted card (Retry needs an agent, which this view model has none of)
        Assert.DoesNotContain("↻ Retry", buttons);
        Assert.Contains("Copy branch", buttons);
        Assert.Contains("＋ Add to backlog", buttons);
        window.Close();
        Assert.NotEqual(backlog, running);
    }

    [AvaloniaFact]
    public void AnEmptyBoard_ExplainsWhatToDo()
    {
        var vm = new MissionControlViewModel(storeFor: _ => new BoardStore(Path.Combine(_root, "empty.json")));
        vm.SetProject(_root);
        var window = new Window { Width = 1280, Height = 800, Content = new MissionControlView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.StartsWith("No cards yet"));
        window.Close();
    }
}
