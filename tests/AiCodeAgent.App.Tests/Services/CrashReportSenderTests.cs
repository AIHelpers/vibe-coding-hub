using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Diagnostics;
using Xunit;

namespace AiCodeAgent.App.Tests.Services;

public class CrashReportSenderTests
{
    private sealed class FakeLauncher(bool succeeds = true) : IUrlLauncher
    {
        public List<string> Opened { get; } = new();

        public bool Open(string url)
        {
            Opened.Add(url);
            return succeeds;
        }
    }

    private static readonly CrashReportContext Context = new("1.2.0", "Test OS", ".NET 10", "X64");
    private static readonly SecretRedactor NoEnv = new(new Dictionary<string, string>());

    [Fact]
    public void OpensAPrefilledIssueWithTheExceptionInIt()
    {
        var launcher = new FakeLauncher();

        var opened = CrashReportSender.Send(
            new CrashNotice("Something went wrong.", "System.InvalidOperationException: binding failed"), launcher, Context, NoEnv);

        Assert.True(opened);
        var url = Assert.Single(launcher.Opened);
        Assert.StartsWith("https://github.com/AIHelpers/vibe-coding-hub/issues/new?", url);
        Assert.Contains(Uri.EscapeDataString("binding failed"), url);
    }

    [Fact]
    public void TheLauncherGetsALinkItAccepts()
    {
        // The real launcher refuses everything but plain https links to GitHub.
        var launcher = new SystemUrlLauncherProbe();

        CrashReportSender.Send(new CrashNotice("x", "System.Exception: boom"), launcher, Context, NoEnv);

        Assert.True(launcher.WouldAccept);
    }

    [Fact]
    public void ReportsFailureWhenTheBrowserCannotBeOpened()
    {
        Assert.False(CrashReportSender.Send(new CrashNotice("x", "System.Exception: boom"), new FakeLauncher(succeeds: false), Context, NoEnv));
    }

    [Fact]
    public void SecretsInTheDetailsNeverReachTheLauncher()
    {
        var launcher = new FakeLauncher();

        CrashReportSender.Send(
            new CrashNotice("x", "System.Exception: rejected key sk-ant-abcdefghijklmnop1234"), launcher, Context, NoEnv);

        Assert.DoesNotContain("abcdefghijklmnop1234", Assert.Single(launcher.Opened));
    }

    private sealed class SystemUrlLauncherProbe : IUrlLauncher
    {
        public bool WouldAccept { get; private set; }

        public bool Open(string url)
        {
            WouldAccept = AiCodeAgent.Core.Updates.UpdateUrlGuard.IsSafe(url);
            return WouldAccept;
        }
    }
}
