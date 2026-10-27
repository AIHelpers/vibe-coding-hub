using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Samples;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>"Try the sample project" and the checklist overlay of its guided first task (E3-S3).</summary>
public partial class MainViewModel
{
    private readonly SampleProjectInstaller _sampleInstaller = new(new EmbeddedSampleFileSource());

    /// <summary>A sample the wizard copied; its guided task starts when the wizard finishes with that folder.</summary>
    private SampleInstallResult? _wizardSample;

    // Progress is saved after every tick; two saves must not write config.json at the same time.
    private readonly SemaphoreSlim _sampleSaveLock = new(1, 1);

    /// <summary>The checklist overlay, or null when there is no tour (not started, finished or hidden).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSampleGuideOpen))]
    private SampleGuideViewModel? _sampleGuide;

    public bool IsSampleGuideOpen => SampleGuide != null;

    /// <summary>The wizard's "Try the sample project" button: copies the sample and returns its folder.</summary>
    private async Task<string?> CreateSampleForWizardAsync()
    {
        var result = await Task.Run(() => _sampleInstaller.InstallAsync()).ConfigureAwait(true);
        _wizardSample = result;
        return result.Path;
    }

    /// <summary>Command Palette → "Try the sample project": copy it, open it, and start the guided first task.</summary>
    [RelayCommand]
    private async Task TrySampleProjectAsync()
    {
        try
        {
            StatusText = "Creating the sample project…";
            var result = await Task.Run(() => _sampleInstaller.InstallAsync()).ConfigureAwait(true);
            await ApplyWorkingDirectoryAsync(result.Path);
            BeginSampleGuide(result);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't create the sample project: {ex.Message}";
        }
    }

    /// <summary>Puts the first task into the chat box and, unless the tour was finished before, shows the checklist.</summary>
    private void BeginSampleGuide(SampleInstallResult sample)
    {
        GetOrCreateChatViewModel().InputText = SampleProjectInstaller.FirstTaskPrompt;
        _ = SourceControl.RefreshAsync();

        // Finished or hidden once means hidden for good, also for a second copy of the sample.
        if (_configurationService?.Config.Ui.SampleGuide is not { Finished: true })
        {
            OpenSampleGuide(sample.Path, 0);
            _ = SaveSampleGuideAsync();
        }

        StatusText = sample.Warning ?? "Sample project ready: press Enter to send the task.";
    }

    /// <summary>After a restart: shows the checklist again where the user left it, if the tour is not finished.</summary>
    private void RestoreSampleGuide()
    {
        var state = _configurationService?.Config.Ui.SampleGuide;
        if (state == null || state.Finished || SampleGuide != null) return;
        if (string.IsNullOrWhiteSpace(state.ProjectPath) || !Directory.Exists(state.ProjectPath)) return;
        OpenSampleGuide(state.ProjectPath, state.Completed);
    }

    private void OpenSampleGuide(string projectPath, int completed)
    {
        CloseSampleGuide();
        var guide = new SampleGuideViewModel(projectPath, completed);
        guide.Changed += OnSampleGuideChanged;
        guide.Dismissed += OnSampleGuideDismissed;
        SampleGuide = guide;
    }

    private void CloseSampleGuide()
    {
        var guide = SampleGuide;
        if (guide == null) return;
        guide.Changed -= OnSampleGuideChanged;
        guide.Dismissed -= OnSampleGuideDismissed;
        SampleGuide = null;
    }

    private void OnSampleGuideChanged(object? sender, EventArgs e) => _ = SaveSampleGuideAsync();

    private void OnSampleGuideDismissed(object? sender, EventArgs e)
    {
        var path = SampleGuide?.ProjectPath ?? string.Empty;
        var completed = SampleGuide?.Checklist.Completed ?? 0;
        CloseSampleGuide();
        _ = SaveSampleGuideAsync(new SampleGuideState { ProjectPath = path, Completed = completed, Finished = true });
    }

    private async Task SaveSampleGuideAsync(SampleGuideState? state = null)
    {
        try
        {
            if (_configurationService == null) return;
            var guide = SampleGuide;
            state ??= guide == null
                ? null
                : new SampleGuideState
                {
                    ProjectPath = guide.ProjectPath,
                    Completed = guide.Checklist.Completed,
                    Finished = guide.IsComplete
                };
            if (state == null) return;
            await _sampleSaveLock.WaitAsync();
            try
            {
                _configurationService.Config.Ui.SampleGuide = state;
                await _configurationService.SaveAsync();
            }
            finally
            {
                _sampleSaveLock.Release();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Saving the sample project progress failed: {ex.Message}");
        }
    }

    /// <summary>Ticks the checklist, but only while the agent works inside the sample folder.</summary>
    private void ReportToSampleGuide(Func<SampleTaskChecklist, bool> report)
    {
        var guide = SampleGuide;
        if (guide == null) return;
        if (!SamePath(GetOrCreateChatViewModel().WorkingDirectory, guide.ProjectPath)) return;
        report(guide.Checklist);
    }

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);
    }
}
