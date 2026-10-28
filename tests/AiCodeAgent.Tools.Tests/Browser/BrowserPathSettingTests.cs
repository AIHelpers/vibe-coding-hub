using AiCodeAgent.Tools.Browser;

namespace AiCodeAgent.Tools.Tests.Browser;

/// <summary>E4-S5: the browser path from Settings → Integrations is used by the browser check.</summary>
public class BrowserPathSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "browser-path-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FakeBrowser()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "my-browser");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void AConfiguredPathThatExists_IsUsed()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIAGENT_BROWSER")))
            return;   // the variable wins, so there is nothing to show on this machine

        var path = FakeBrowser();

        Assert.Equal(path, BrowserCheckTool.FindBrowser(path));
    }

    [Fact]
    public void AConfiguredPathThatDoesNotExist_IsIgnored()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIAGENT_BROWSER")))
            return;

        var missing = Path.Combine(_dir, "nope");

        Assert.NotEqual(missing, BrowserCheckTool.FindBrowser(missing));
    }
}
