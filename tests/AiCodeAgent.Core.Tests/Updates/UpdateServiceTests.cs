using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Tests.Updates;

/// <summary>E3-S5: once a day, off switch, and "dismiss for that version".</summary>
public class UpdateServiceTests
{
    private static readonly UpdatePlatform Win = new(UpdateOs.Windows, UpdateArch.X64);
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeChecker : IUpdateChecker
    {
        public int Calls { get; private set; }
        public Func<UpdateCheckResult> Respond { get; set; } = () => UpdateCheckResult.UpToDate();

        public Task<UpdateCheckResult> CheckAsync(SemanticVersion current, UpdatePlatform platform, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Respond());
        }
    }

    private static ReleaseInfo Release(string version) => new(
        SemanticVersion.Parse(version), "v" + version, "Release " + version, "notes", "https://github.com/AIHelpers/vibe-coding-hub/releases/tag/v" + version,
        Now, Array.Empty<ReleaseAsset>(), null);

    private static (UpdateService Service, FakeChecker Checker, UiConfiguration Ui) Make(string newest = "1.3.0")
    {
        var checker = new FakeChecker { Respond = () => UpdateCheckResult.Available(Release(newest)) };
        return (new UpdateService(checker, () => Now), checker, new UiConfiguration());
    }

    private static Task<UpdateOutcome> Check(UpdateService service, UiConfiguration ui, bool manual = false) =>
        service.CheckAsync(ui, SemanticVersion.Parse("1.2.0"), Win, manual);

    [Fact]
    public void ChecksAreOnByDefault()
    {
        Assert.True(new UiConfiguration().CheckForUpdates);
        Assert.Null(new UiConfiguration().LastUpdateCheck);
        Assert.Null(new UiConfiguration().DismissedUpdateVersion);
    }

    [Fact]
    public async Task WithCheckForUpdatesOff_NoRequestIsMade()
    {
        var (service, checker, ui) = Make();
        ui.CheckForUpdates = false;

        var outcome = await Check(service, ui);

        Assert.Equal(0, checker.Calls);
        Assert.False(outcome.Requested);
        Assert.Null(outcome.Offer);
        Assert.Null(ui.LastUpdateCheck);
    }

    [Fact]
    public async Task ANewerRelease_IsOffered_AndTheCheckTimeIsRecorded()
    {
        var (service, checker, ui) = Make();

        var outcome = await Check(service, ui);

        Assert.Equal(1, checker.Calls);
        Assert.True(outcome.Requested);
        Assert.Equal("1.3.0", outcome.Offer!.Version.ToString());
        Assert.Equal(Now, ui.LastUpdateCheck);
    }

    [Fact]
    public async Task AtMostOneRequestADay()
    {
        var (service, checker, ui) = Make();
        ui.LastUpdateCheck = Now - TimeSpan.FromHours(23);

        var tooSoon = await Check(service, ui);

        Assert.Equal(0, checker.Calls);
        Assert.False(tooSoon.Requested);

        ui.LastUpdateCheck = Now - TimeSpan.FromHours(24);
        var due = await Check(service, ui);

        Assert.Equal(1, checker.Calls);
        Assert.NotNull(due.Offer);
    }

    [Fact]
    public void IsDue_AlsoWhenTheClockWentBackwards()
    {
        Assert.True(UpdateService.IsDue(new UiConfiguration { LastUpdateCheck = Now + TimeSpan.FromDays(3) }, Now));
        Assert.False(UpdateService.IsDue(new UiConfiguration { LastUpdateCheck = Now + TimeSpan.FromMinutes(5) }, Now));
        Assert.True(UpdateService.IsDue(new UiConfiguration(), Now));
    }

    [Fact]
    public async Task ADismissedVersion_IsNotOfferedAgain_ButANewerOneIs()
    {
        var (service, checker, ui) = Make("1.3.0");
        var first = await Check(service, ui);
        UpdateService.Dismiss(ui, first.Offer!);
        Assert.Equal("1.3.0", ui.DismissedUpdateVersion);

        ui.LastUpdateCheck = Now - TimeSpan.FromDays(2);
        var again = await Check(service, ui);

        Assert.True(again.Requested);
        Assert.Null(again.Offer);

        checker.Respond = () => UpdateCheckResult.Available(Release("1.4.0"));
        ui.LastUpdateCheck = Now - TimeSpan.FromDays(2);
        var newer = await Check(service, ui);

        Assert.Equal("1.4.0", newer.Offer!.Version.ToString());
    }

    [Fact]
    public async Task UpToDate_ShowsNothing_ButStillCountsAsChecked()
    {
        var (service, checker, ui) = Make();
        checker.Respond = () => UpdateCheckResult.UpToDate(Release("1.2.0"));

        var outcome = await Check(service, ui);

        Assert.Null(outcome.Offer);
        Assert.Equal(Now, ui.LastUpdateCheck);
    }

    [Fact]
    public async Task AFailedCheck_ShowsNothing_AndIsRetriedAtTheNextStart()
    {
        var (service, checker, ui) = Make();
        checker.Respond = () => UpdateCheckResult.Failed("offline");

        var outcome = await Check(service, ui);

        Assert.True(outcome.Requested);
        Assert.Null(outcome.Offer);
        Assert.Null(ui.LastUpdateCheck);

        checker.Respond = () => UpdateCheckResult.Available(Release("1.3.0"));
        var retry = await Check(service, ui);
        Assert.NotNull(retry.Offer);
    }

    [Fact]
    public async Task ACheckTheUserAsksFor_IgnoresTheOffSwitch_TheDailyLimit_AndTheDismissal()
    {
        var (service, checker, ui) = Make();
        ui.CheckForUpdates = false;
        ui.LastUpdateCheck = Now - TimeSpan.FromMinutes(1);
        ui.DismissedUpdateVersion = "1.3.0";

        var outcome = await Check(service, ui, manual: true);

        Assert.Equal(1, checker.Calls);
        Assert.Equal("1.3.0", outcome.Offer!.Version.ToString());
    }

    [Fact]
    public async Task TheSettings_SurviveARoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "update-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ConfigurationService(dir);
            await first.LoadAsync();
            first.Config.Ui.CheckForUpdates = false;
            first.Config.Ui.LastUpdateCheck = Now;
            first.Config.Ui.DismissedUpdateVersion = "1.3.0";
            await first.SaveAsync();

            var second = new ConfigurationService(dir);
            await second.LoadAsync();

            Assert.False(second.Config.Ui.CheckForUpdates);
            Assert.Equal(Now, second.Config.Ui.LastUpdateCheck);
            Assert.Equal("1.3.0", second.Config.Ui.DismissedUpdateVersion);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}
