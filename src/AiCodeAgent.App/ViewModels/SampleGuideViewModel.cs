using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AiCodeAgent.Core.Samples;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One line of the checklist overlay.</summary>
public sealed partial class SampleGuideItem : ObservableObject
{
    public SampleGuideItem(int number, string label)
    {
        Number = number;
        Label = label;
    }

    public int Number { get; }

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private bool _isDone;

    /// <summary>True for the step the user should do now.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>A tick for a finished step, else its number.</summary>
    public string Mark => IsDone ? "✓" : Number.ToString();
}

/// <summary>
/// The overlay of the sample project's guided first task: four steps that tick off by themselves
/// (<see cref="SampleTaskChecklist"/>), a hint for the next one, and a way to hide it for good.
/// </summary>
public sealed partial class SampleGuideViewModel : ObservableObject
{
    private readonly TimeSpan _autoHideAfter;
    private bool _dismissed;

    public SampleGuideViewModel(string projectPath, int completed = 0, TimeSpan? autoHideAfter = null)
    {
        ProjectPath = projectPath;
        _autoHideAfter = autoHideAfter ?? TimeSpan.FromSeconds(8);
        Checklist = new SampleTaskChecklist(completed);
        for (var i = 0; i < SampleTaskChecklist.StepCount; i++)
            Items.Add(new SampleGuideItem(i + 1, SampleTaskChecklist.Labels[i]));
        Refresh();
        Checklist.Changed += OnChecklistChanged;
    }

    /// <summary>The sample folder this tour belongs to.</summary>
    public string ProjectPath { get; }

    public SampleTaskChecklist Checklist { get; }

    public ObservableCollection<SampleGuideItem> Items { get; } = new();

    /// <summary>"2 of 4".</summary>
    public string Progress => $"{Checklist.Completed} of {SampleTaskChecklist.StepCount}";

    public bool IsComplete => Checklist.IsComplete;

    /// <summary>What to do next, in one sentence.</summary>
    [ObservableProperty]
    private string _hint = string.Empty;

    /// <summary>Raised after a step was ticked: the caller saves the progress.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the checklist should disappear for good (completed, or hidden by the user).</summary>
    public event EventHandler? Dismissed;

    /// <summary>Hides the checklist for good.</summary>
    [RelayCommand]
    private void Hide() => Dismiss();

    private void OnChecklistChanged()
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        if (Checklist.IsComplete) _ = HideAfterDelayAsync();
    }

    // After the last tick the overlay shows its "all done" message for a few seconds, then goes away for good.
    private async Task HideAfterDelayAsync()
    {
        if (_autoHideAfter > TimeSpan.Zero)
            await Task.Delay(_autoHideAfter).ConfigureAwait(true);
        Dismiss();
    }

    private void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsDone = i < Checklist.Completed;
            Items[i].IsCurrent = i == Checklist.Completed;
        }
        Hint = Checklist.Completed switch
        {
            0 => "Press Enter to send the prepared task to the agent.",
            1 => "The agent proposes a change. Look at the diff, then approve it.",
            2 => "The agent runs the tests. If it doesn't, ask it to run dotnet test.",
            3 => "Open Source Control, write a commit message and press Commit.",
            _ => "All done! You have seen the whole loop: task, approval, tests, diff and commit."
        };
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsComplete));
    }
}
