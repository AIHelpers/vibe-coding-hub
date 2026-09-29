using AiCodeAgent.Tools.Browser;

namespace AiCodeAgent.Tools.Tests.Browser;

public class BrowserCheckToolTests
{
    [Fact]
    public void ParseConsole_ExtractsLevelsAndText()
    {
        var stderr =
            "[1234:5678:0929/120000.000:INFO:CONSOLE(1)] \"hello world\", source: http://localhost/ (1)\n" +
            "[1234:5678:0929/120000.001:ERROR:CONSOLE(7)] \"Uncaught TypeError: x is not a function\", source: http://localhost/app.js (7)\n" +
            "[1234:5678:0929/120000.002:WARNING:CONSOLE(9)] \"deprecated\", source: http://localhost/app.js (9)\n" +
            "[1234:5678:0929/120000.003:ERROR:gpu_process_host.cc(1)] not a console line\n";

        var messages = BrowserCheckTool.ParseConsole(stderr);

        Assert.Equal(3, messages.Count);
        Assert.Equal("ERROR", messages[1].Level);
        Assert.Contains("TypeError", messages[1].Text);
        Assert.Equal("WARNING", messages[2].Level);
    }

    [Fact]
    public void ParseConsole_TreatsUncaughtExceptionsLoggedAsInfoAsErrors()
    {
        // Real Chromium output: every console message arrives as INFO.
        var stderr =
            "[2302:2302:0929/193717.328097:INFO:CONSOLE:1] \"careful\", source: file:///a.html (1)\n" +
            "[2302:2302:0929/193717.332210:INFO:CONSOLE:1] \"Uncaught Error: sync fail\", source: file:///a.html (1)\n" +
            "[2302:2302:0929/193717.341248:INFO:CONSOLE:1] \"Uncaught ReferenceError: undefinedFn is not defined\", source: file:///a.html (1)\n";

        var messages = BrowserCheckTool.ParseConsole(stderr);

        Assert.Equal(3, messages.Count);
        Assert.Equal("INFO", messages[0].Level);
        Assert.Equal("ERROR", messages[1].Level);
        Assert.Equal("ERROR", messages[2].Level);
    }

    [Fact]
    public void ExtractVisibleText_DropsScriptsStylesAndTags()
    {
        var html = "<html><head><title>T</title><style>p{}</style></head><body><h1>Hi &amp; bye</h1><script>var a=1;</script><p>Text</p></body></html>";
        Assert.Equal("Hi & bye Text", BrowserCheckTool.ExtractVisibleText(html));
        Assert.Equal("T", BrowserCheckTool.ExtractTitle(html));
    }

    [Fact]
    public void BuildReport_FlagsBlankPage()
    {
        var report = BrowserCheckTool.BuildReport("http://x/", "<html><body></body></html>", [], null, null);
        Assert.Contains("none: the page may be blank", report);
        Assert.Contains("0 error(s)", report);
    }

    [Fact]
    public void BuildReport_ListsConsoleErrorsAndScreenshot()
    {
        var report = BrowserCheckTool.BuildReport("http://x/", "<title>App</title><button>go</button>",
            [new BrowserConsoleMessage("ERROR", "boom")], "/tmp/shot.png", null);
        Assert.Contains("Title: App", report);
        Assert.Contains("1 error(s)", report);
        Assert.Contains("[ERROR] boom", report);
        Assert.Contains("1 button(s)", report);
        Assert.Contains("Screenshot saved: /tmp/shot.png", report);
    }
}
