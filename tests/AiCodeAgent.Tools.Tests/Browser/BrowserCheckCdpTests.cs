using System.Text.Json;
using AiCodeAgent.Tools.Browser;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.Browser;

/// <summary>Runs against a real installed Chromium/Chrome/Edge; silently passes when none is available.</summary>
public class BrowserCheckCdpTests : TempDirTestBase
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static bool HasBrowser => BrowserCheckTool.FindBrowser() != null;

    private static BrowserCheckTool Tool() => new(NullLogger<BrowserCheckTool>.Instance);

    [Fact]
    public void MapConsoleType_UsesRealLevels()
    {
        Assert.Equal("ERROR", CdpBrowserSession.MapConsoleType("error"));
        Assert.Equal("ERROR", CdpBrowserSession.MapConsoleType("assert"));
        Assert.Equal("WARNING", CdpBrowserSession.MapConsoleType("warning"));
        Assert.Equal("INFO", CdpBrowserSession.MapConsoleType("log"));
        Assert.Equal("INFO", CdpBrowserSession.MapConsoleType("debug"));
    }

    [Fact]
    public void BuildReport_IncludesActionLog()
    {
        var report = BrowserCheckTool.BuildReport("http://x/", "<title>T</title>", [], null, null, ["1. click #go ok"]);
        Assert.Contains("Actions:", report);
        Assert.Contains("1. click #go ok", report);
    }

    [Fact]
    public async Task ConsoleError_IsReportedAsErrorNotInfo()
    {
        if (!HasBrowser) return;
        var page = WriteFile("a.html", "<title>Levels</title><body>hi<script>console.log('fine');console.warn('careful');console.error('boom');</script></body>");

        var result = await Tool().ExecuteAsync(Call(new() { ["url"] = page, ["wait_ms"] = 200, ["screenshot"] = false }), Context());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("1 error(s), 1 warning(s)", result.Content);
        Assert.Contains("[ERROR] boom", result.Content);
        Assert.Contains("[WARNING] careful", result.Content);
        Assert.DoesNotContain("fine", result.Content.Split("Visible text")[0]);
    }

    [Fact]
    public async Task UncaughtException_IsReportedWithLocation()
    {
        if (!HasBrowser) return;
        var page = WriteFile("b.html", "<body><script>undefinedFn();</script></body>");

        var result = await Tool().ExecuteAsync(Call(new() { ["url"] = page, ["wait_ms"] = 200, ["screenshot"] = false }), Context());

        Assert.Contains("[ERROR] Uncaught ReferenceError", result.Content);
    }

    [Fact]
    public async Task Click_TypeAndWaitFor_DriveThePage()
    {
        if (!HasBrowser) return;
        var page = WriteFile("app.html", """
            <html><head><title>App</title></head><body>
            <input id="name"><button id="go">Go</button><div id="out"></div>
            <script>
              document.getElementById('go').addEventListener('click', () => {
                const v = document.getElementById('name').value;
                if (!v) { console.error('name required'); return; }
                setTimeout(() => { document.getElementById('out').textContent = 'Hello ' + v; }, 100);
              });
            </script></body></html>
            """);

        // 1) Click with empty input -> console.error
        var empty = await Tool().ExecuteAsync(Call(new()
        {
            ["url"] = page, ["wait_ms"] = 100, ["screenshot"] = false,
            ["actions"] = Json("""[{"type":"click","selector":"#go"}]""")
        }), Context());
        Assert.Contains("click #go ok", empty.Content);
        Assert.Contains("[ERROR] name required", empty.Content);

        // 2) Type then click -> DOM changes
        var filled = await Tool().ExecuteAsync(Call(new()
        {
            ["url"] = page, ["wait_ms"] = 100, ["screenshot"] = false,
            ["actions"] = Json("""[{"type":"type","selector":"#name","text":"Roman"},{"type":"click","selector":"#go"},{"type":"wait_for","text":"Hello Roman"}]""")
        }), Context());
        Assert.Contains("wait_for text \"Hello Roman\" ok", filled.Content);
        Assert.Contains("Hello Roman", filled.Content);
        Assert.Contains("0 error(s)", filled.Content);
    }

    [Fact]
    public async Task MissingElement_IsReportedInActionLogWithoutFailingTheRun()
    {
        if (!HasBrowser) return;
        var page = WriteFile("c.html", "<body><p>x</p></body>");

        var result = await Tool().ExecuteAsync(Call(new()
        {
            ["url"] = page, ["wait_ms"] = 0, ["screenshot"] = false,
            ["actions"] = Json("""[{"type":"click","selector":"#nope"},{"type":"eval","script":"1+1"}]""")
        }), Context());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("click FAILED: no element matches '#nope'", result.Content);
        Assert.Contains("eval => 2", result.Content);
    }

    [Fact]
    public async Task Screenshot_IsWrittenAsPng()
    {
        if (!HasBrowser) return;
        var page = WriteFile("d.html", "<body style='background:#369'><h1>Shot</h1></body>");

        var result = await Tool().ExecuteAsync(Call(new() { ["url"] = page, ["wait_ms"] = 100 }), Context());

        var line = result.Content.Split('\n').Single(l => l.StartsWith("Screenshot saved:"));
        var path = line["Screenshot saved:".Length..].Trim();
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }
}
