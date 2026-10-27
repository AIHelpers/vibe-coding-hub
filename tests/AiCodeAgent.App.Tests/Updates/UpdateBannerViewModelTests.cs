using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.App.Tests.Updates;

/// <summary>E3-S5: the update banner and the running build's version.</summary>
public class UpdateBannerViewModelTests
{
    private static ReleaseInfo Release(string notes = "## Fixes\n- A crash", ReleaseAsset? download = null) => new(
        SemanticVersion.Parse("1.3.0"), "v1.3.0", "Release 1.3.0", notes,
        "https://github.com/AIHelpers/vibe-coding-hub/releases/tag/v1.3.0", null,
        download == null ? Array.Empty<ReleaseAsset>() : new[] { download }, download);

    private static readonly ReleaseAsset Zip = new(
        "vibe-coding-hub-1.3.0-win-x64.zip", "https://github.com/AIHelpers/vibe-coding-hub/releases/download/v1.3.0/vibe-coding-hub-1.3.0-win-x64.zip", 52_428_800);

    private static UpdateBannerViewModel Banner(ReleaseInfo release, Func<string, bool>? open = null) =>
        new(release, SemanticVersion.Parse("1.2.0"), open ?? (_ => true));

    [Fact]
    public void ShowsTheVersions_AndThePlainTextNotes()
    {
        var banner = Banner(Release());

        Assert.Equal("Version 1.3.0 is available", banner.Title);
        Assert.Equal("You have 1.2.0.", banner.Subtitle);
        Assert.True(banner.HasNotes);
        Assert.Equal("Fixes\n• A crash", banner.Notes);
        Assert.False(banner.IsNotesOpen);
        Assert.Equal("What's new", banner.NotesButtonText);
    }

    [Fact]
    public void TheNotes_OpenAndCloseOnDemand()
    {
        var banner = Banner(Release());

        banner.ToggleNotesCommand.Execute(null);
        Assert.True(banner.IsNotesOpen);
        Assert.Equal("Hide notes", banner.NotesButtonText);

        banner.ToggleNotesCommand.Execute(null);
        Assert.False(banner.IsNotesOpen);
    }

    [Fact]
    public void AReleaseWithoutNotes_HasNoNotesButton()
    {
        Assert.False(Banner(Release(notes: "")).HasNotes);
    }

    [Fact]
    public void Download_OpensTheFileForThisComputer_AndClosesTheBanner()
    {
        var opened = new List<string>();
        var banner = Banner(Release(download: Zip), url => { opened.Add(url); return true; });
        var closed = 0;
        var dismissed = 0;
        banner.Closed += (_, _) => closed++;
        banner.DismissRequested += (_, _) => dismissed++;

        Assert.Equal("Download (50 MB)", banner.DownloadText);
        banner.DownloadCommand.Execute(null);

        Assert.Equal(new[] { Zip.DownloadUrl }, opened);
        Assert.Equal(1, closed);
        Assert.Equal(0, dismissed); // downloading is not dismissing: the version is announced again if it is not installed
    }

    [Fact]
    public void WithoutAFileForThisComputer_TheButtonOpensTheReleasePage()
    {
        var opened = new List<string>();
        var banner = Banner(Release(), url => { opened.Add(url); return true; });

        Assert.Equal("Open release page", banner.DownloadText);
        banner.DownloadCommand.Execute(null);

        Assert.Equal(new[] { "https://github.com/AIHelpers/vibe-coding-hub/releases/tag/v1.3.0" }, opened);
    }

    [Fact]
    public void IfTheBrowserCannotBeOpened_TheBannerStaysAndSaysSo()
    {
        var banner = Banner(Release(download: Zip), _ => false);
        var closed = false;
        banner.Closed += (_, _) => closed = true;

        banner.DownloadCommand.Execute(null);

        Assert.False(closed);
        Assert.Contains("release page", banner.Message);
    }

    [Fact]
    public void Dismiss_AsksToRememberThisVersion()
    {
        var banner = Banner(Release());
        var dismissed = 0;
        banner.DismissRequested += (_, _) => dismissed++;

        banner.DismissCommand.Execute(null);

        Assert.Equal(1, dismissed);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(52_428_800L, "50 MB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    public void FileSizes_AreReadable(long bytes, string expected)
    {
        Assert.Equal(expected, UpdateBannerViewModel.FormatSize(bytes));
    }

    [Fact]
    public void TheRunningVersion_ComesFromTheInformationalVersion_ThenTheAssemblyVersion()
    {
        Assert.Equal("1.2.3+abc123", AppInfo.From("1.2.3+abc123", new Version(9, 9, 9, 9)).ToString());
        Assert.Equal("0.0.0-dev.57", AppInfo.From("0.0.0-dev.57", null).ToString());
        Assert.Equal("1.0.0.5", AppInfo.From("garbage", new Version(1, 0, 0, 5)).ToString());
        Assert.Equal("0.0.0", AppInfo.From(null, null).ToString());
        Assert.True(AppInfo.CurrentVersion >= SemanticVersion.Parse("0.0.0"));
    }
}
