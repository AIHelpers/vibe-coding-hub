using System.Collections.ObjectModel;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using ReactiveUI;

namespace AiCodeAgent.App.ViewModels;

/// <summary>View-model wrapper for a single <see cref="PlanStep"/>.</summary>
public class PlanStepViewModel : ReactiveObject
{
    private PlanStepStatus _status;
    private string? _failureReason;

    public int Index { get; }
    public string Description { get; }
    public string Files { get; }
    public string? VerifyCommand { get; }

    public PlanStepStatus Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string? FailureReason
    {
        get => _failureReason;
        set => this.RaiseAndSetIfChanged(ref _failureReason, value);
    }

    public string Icon => Status switch
    {
        PlanStepStatus.Completed => "✓",
        PlanStepStatus.Failed => "✗",
        PlanStepStatus.Running => "▶",
        PlanStepStatus.AwaitingApproval => "⏸",
        _ => "○"
    };

    public PlanStepViewModel(PlanStep step)
    {
        Index = step.Index;
        Description = step.Description;
        Files = step.FilesLikelyTouched.Count > 0
            ? string.Join(", ", step.FilesLikelyTouched)
            : "(unspecified)";
        VerifyCommand = step.VerifyCommand;
        _status = step.Status;
        _failureReason = step.FailureReason;
    }

    public void Sync(PlanStep step)
    {
        Status = step.Status;
        FailureReason = step.FailureReason;
        this.RaisePropertyChanged(nameof(Icon));
    }
}

/// <summary>View-model for the autonomous plan panel.</summary>
public class PlanViewModel : ReactiveObject
{
    private bool _isVisible;
    private bool _isRunning;
    private string _summary = string.Empty;

    public ObservableCollection<PlanStepViewModel> Steps { get; } = new();

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    public string Summary
    {
        get => _summary;
        set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    public void LoadPlan(List<PlanStep> steps)
    {
        Steps.Clear();
        foreach (var s in steps)
            Steps.Add(new PlanStepViewModel(s));
        IsVisible = true;
        IsRunning = true;
        Summary = $"Plan: {steps.Count} step(s)";
    }

    public void UpdateStep(int index, PlanStep step)
    {
        if (index >= 0 && index < Steps.Count)
            Steps[index].Sync(step);
    }

    public void Complete(string? failureSummary)
    {
        IsRunning = false;
        Summary = string.IsNullOrEmpty(failureSummary)
            ? "Plan completed"
            : $"Plan failed: {failureSummary}";
    }
}