using System;
using AiCodeAgent.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The non-blocking "a newer version is available" card: the version, the release notes (open on demand),
/// <b>Download</b> and <b>Dismiss</b>. Dismiss means "do not tell me about this version again".
/// </summary>
public sealed partial class UpdateBannerViewModel : ObservableObject
{
    private readonly Func<string, bool> _open;

    /// <param name="release">The newer release.</param>
    /// <param name="current">The version that is running.</param>
    /// <param name="open">Opens an address in the browser; returns false when it could not.</param>
    public UpdateBannerViewModel(ReleaseInfo release, SemanticVersion current, Func<string, bool> open)
    {
        Release = release;
        Current = current;
        _open = open;
        Notes = ReleaseNotesFormatter.Format(release.Notes);
    }

    public ReleaseInfo Release { get; }

    public SemanticVersion Current { get; }

    public string Title => $"Version {Release.Version} is available";

    public string Subtitle => $"You have {Current}.";

    /// <summary>The release notes as plain text; empty when the release has none.</summary>
    public string Notes { get; }

    public bool HasNotes => Notes.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesButtonText))]
    private bool _isNotesOpen;

    public string NotesButtonText => IsNotesOpen ? "Hide notes" : "What's new";

    /// <summary>"Download (48 MB)" when there is a file for this computer, else "Open release page".</summary>
    public string DownloadText => Release.Download is { } file && file.Size > 0
        ? $"Download ({FormatSize(file.Size)})"
        : Release.Download != null ? "Download" : "Open release page";

    /// <summary>The address <b>Download</b> opens: the file for this computer, else the release page.</summary>
    public string DownloadUrl => Release.Download?.DownloadUrl ?? Release.PageUrl;

    /// <summary>Raised when the card should go away because the user pressed <b>Dismiss</b> (remember the version).</summary>
    public event EventHandler? DismissRequested;

    /// <summary>Raised when the card should go away without remembering anything (the download was started).</summary>
    public event EventHandler? Closed;

    /// <summary>Set when the address could not be opened, so the card can say so.</summary>
    [ObservableProperty]
    private string _message = string.Empty;

    [RelayCommand]
    private void ToggleNotes() => IsNotesOpen = !IsNotesOpen;

    [RelayCommand]
    private void Download()
    {
        if (_open(DownloadUrl))
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }
        Message = "Couldn't open the browser. The download is on the release page of the project on GitHub.";
    }

    [RelayCommand]
    private void Dismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B"
    };
}
