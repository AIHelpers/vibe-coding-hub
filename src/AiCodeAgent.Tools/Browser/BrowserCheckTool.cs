using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Browser;

/// <summary>A console line captured from the page.</summary>
public sealed record BrowserConsoleMessage(string Level, string Text);

/// <summary>
/// Loads a page in headless Chrome/Edge and reports what a user would see: title, visible text, console
/// errors/warnings and failed requests, plus a screenshot saved to disk. Works with any installed Chromium browser
/// (no extra packages): set AIAGENT_BROWSER to its path if it is not found automatically. It drives the browser over the
/// DevTools protocol, so console levels are exact and it can run a short script of click/type/press/wait steps before
/// capturing. Use it to check that a dev server or generated HTML page renders and behaves without errors.
/// </summary>
public class BrowserCheckTool : BaseTool
{
    private const int ProcessTimeoutSeconds = 60;

    private static readonly Regex ConsoleLine = new(
        @"(?<level>INFO|WARNING|ERROR|VERBOSE\d*):CONSOLE[^\]]*\]\s*""(?<text>.*?)"",\s*source:",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public BrowserCheckTool(ILogger<BrowserCheckTool> logger) : base(logger) { }

    public override string Name => "browser_check";
    public override string Description =>
        "Open a URL (http://localhost:3000, https://..., or a local .html file path) in a headless browser and report the page title, " +
        "visible text, JavaScript console errors/warnings, and save a screenshot. Use it to verify a web UI renders correctly " +
        "after changes. Optional 'actions' let it click, type, press keys and wait before the report is taken, so you can " +
        "exercise a form or button and see the resulting console errors and page text.";
    public override RiskLevel Risk => RiskLevel.Execute;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["url"] = new() { Type = "string", Description = "http(s) URL, or path of a local HTML file" },
                ["wait_ms"] = new() { Type = "integer", Description = "Time to let scripts run before capture (default 3000, max 15000)" },
                ["width"] = new() { Type = "integer", Description = "Viewport width (default 1280)" },
                ["height"] = new() { Type = "integer", Description = "Viewport height (default 800)" },
                ["screenshot"] = new() { Type = "boolean", Description = "Save a PNG screenshot (default true)" },
                ["actions"] = new()
                {
                    Type = "array",
                    Description = "Optional steps run in order after the page loads (max 30). type=click|type|press|wait|wait_for|eval",
                    Items = new PropertySchema
                    {
                        Type = "object",
                        Properties = new()
                        {
                            ["type"] = new() { Type = "string", Description = "Step kind", Enum = ["click", "type", "press", "wait", "wait_for", "eval"] },
                            ["selector"] = new() { Type = "string", Description = "CSS selector (click, type, wait_for)" },
                            ["text"] = new() { Type = "string", Description = "Text to type, or text to wait for (wait_for)" },
                            ["key"] = new() { Type = "string", Description = "Key for press: Enter, Tab, Escape, ArrowDown, a, ..." },
                            ["ms"] = new() { Type = "integer", Description = "Milliseconds (wait) or timeout (wait_for)" },
                            ["script"] = new() { Type = "string", Description = "JavaScript expression for eval; its value is reported" }
                        },
                        Required = ["type"]
                    }
                }
            },
            Required = ["url"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var target = GetArg<string>(call, "url");
        var waitMs = Math.Clamp(GetArg<int>(call, "wait_ms", 3000), 0, 15_000);
        var width = Math.Clamp(GetArg<int>(call, "width", 1280), 200, 3840);
        var height = Math.Clamp(GetArg<int>(call, "height", 800), 200, 2160);
        var wantShot = GetArg<bool>(call, "screenshot", true);

        if (string.IsNullOrWhiteSpace(target)) return Error("'url' is required.");

        string url;
        try { url = NormalizeUrl(target, context); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            return Error(ex.Message);
        }

        var browser = FindBrowser();
        if (browser == null)
            return Error("No Chromium-based browser found. Install Chrome or Edge, or set AIAGENT_BROWSER to its executable path.");

        var actions = GetArg<List<BrowserAction>>(call, "actions", new List<BrowserAction>()) ?? new List<BrowserAction>();

        try
        {
            var page = await CdpBrowserSession.RunAsync(browser, url, width, height, waitMs, actions, wantShot, context.CancellationToken);

            string? shotPath = null;
            if (page.Screenshot != null)
            {
                var shotDir = Path.Combine(string.IsNullOrEmpty(context.WorkingDirectory) ? Directory.GetCurrentDirectory() : context.WorkingDirectory,
                    ".aiagent", "screenshots");
                Directory.CreateDirectory(shotDir);
                shotPath = Path.Combine(shotDir, $"page-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
                await File.WriteAllBytesAsync(shotPath, page.Screenshot, context.CancellationToken);
            }
            return Success(BuildReport(page.FinalUrl.Length > 0 ? page.FinalUrl : url, page.Html, page.Console, shotPath, page.ScreenshotNote, page.ActionLog));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Error("Browser check cancelled.");
        }
        catch (Exception ex) when (actions.Count == 0)
        {
            // The DevTools session could not start (locked-down browser, blocked loopback port...). A plain load still works.
            Logger.LogInformation(ex, "DevTools session failed for {Url}; falling back to one-shot capture", url);
            return await ExecuteLegacyAsync(browser, url, waitMs, width, height, wantShot, context);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "browser_check failed for {Url}", url);
            return Error($"Browser check failed: {ex.Message}");
        }
    }

    /// <summary>One-shot capture via --dump-dom (no clicking; console levels are inferred from text).</summary>
    private async Task<ToolResult> ExecuteLegacyAsync(string browser, string url, int waitMs, int width, int height, bool wantShot, AgentExecutionContext context)
    {
        var profile = Path.Combine(Path.GetTempPath(), "aiagent-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            var common = new List<string>
            {
                "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                "--disable-extensions", "--mute-audio",
                $"--user-data-dir={profile}",
                $"--window-size={width},{height}",
                $"--virtual-time-budget={waitMs}",
                "--enable-logging=stderr", "--v=0"
            };
            if (Environment.GetEnvironmentVariable("AIAGENT_BROWSER_NO_SANDBOX") == "1") common.Add("--no-sandbox");

            // Pass 1: DOM + console. (Headless chrome cannot reliably emit a screenshot and a DOM dump in one run.)
            var dom = await RunBrowserAsync(browser, common.Concat(new[] { "--dump-dom", url }), context.CancellationToken);
            if (dom.TimedOut) return Error($"Browser timed out after {ProcessTimeoutSeconds}s loading {url}.");
            if (dom.ExitCode != 0 && string.IsNullOrWhiteSpace(dom.Stdout))
                return Error($"Browser failed (exit {dom.ExitCode}): {Tail(dom.Stderr, 800)}");

            var html = dom.Stdout;
            var console = ParseConsole(dom.Stderr);

            string? shotPath = null;
            string? shotNote = null;
            if (wantShot)
            {
                var shotDir = Path.Combine(string.IsNullOrEmpty(context.WorkingDirectory) ? Directory.GetCurrentDirectory() : context.WorkingDirectory,
                    ".aiagent", "screenshots");
                Directory.CreateDirectory(shotDir);
                var file = Path.Combine(shotDir, $"page-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
                var shot = await RunBrowserAsync(browser, common.Concat(new[] { $"--screenshot={file}", url }), context.CancellationToken);
                if (File.Exists(file)) shotPath = file;
                else shotNote = shot.TimedOut ? "screenshot timed out" : $"screenshot failed: {Tail(shot.Stderr, 200)}";
            }

            return Success(BuildReport(url, html, console, shotPath, shotNote));
        }
        catch (OperationCanceledException)
        {
            return Error("Browser check cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "browser_check failed for {Url}", url);
            return Error($"Browser check failed: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    private string NormalizeUrl(string target, AgentExecutionContext context)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.AbsoluteUri;

        if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pass a plain file path instead of a file: URL.");
        if (Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*://"))
            throw new ArgumentException("Only http(s) URLs and local file paths are supported.");

        var resolved = ResolvePath(target, context); // enforces the workspace path policy
        if (!File.Exists(resolved)) throw new FileNotFoundException($"File not found: {target}");
        return new Uri(resolved).AbsoluteUri;
    }

    internal static string BuildReport(string url, string html, IReadOnlyList<BrowserConsoleMessage> console, string? screenshotPath, string? screenshotNote,
        IReadOnlyList<string>? actionLog = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {url}");
        sb.AppendLine($"Title: {ExtractTitle(html) ?? "(none)"}");

        if (actionLog is { Count: > 0 })
        {
            sb.AppendLine("Actions:");
            foreach (var l in actionLog) sb.AppendLine($"  {l}");
        }

        var errors = console.Where(c => c.Level == "ERROR").ToList();
        var warnings = console.Where(c => c.Level == "WARNING").ToList();
        sb.AppendLine($"Console: {errors.Count} error(s), {warnings.Count} warning(s)");
        foreach (var c in errors.Concat(warnings).Take(20))
            sb.AppendLine($"  [{c.Level}] {Trim(c.Text, 300)}");

        var text = ExtractVisibleText(html);
        sb.AppendLine(text.Length == 0
            ? "Visible text: (none: the page may be blank or failed to render)"
            : $"Visible text ({text.Length} chars):\n{Trim(text, 2500)}");
        sb.AppendLine($"Elements: {Regex.Matches(html, "<button", RegexOptions.IgnoreCase).Count} button(s), " +
                      $"{Regex.Matches(html, "<a[ >]", RegexOptions.IgnoreCase).Count} link(s), " +
                      $"{Regex.Matches(html, "<input", RegexOptions.IgnoreCase).Count} input(s), " +
                      $"{Regex.Matches(html, "<img", RegexOptions.IgnoreCase).Count} image(s)");

        if (screenshotPath != null) sb.AppendLine($"Screenshot saved: {screenshotPath}");
        else if (screenshotNote != null) sb.AppendLine($"Screenshot: {screenshotNote}");
        return sb.ToString().TrimEnd();
    }

    internal static List<BrowserConsoleMessage> ParseConsole(string stderr)
    {
        var list = new List<BrowserConsoleMessage>();
        foreach (Match m in ConsoleLine.Matches(stderr))
        {
            var level = m.Groups["level"].Value.ToUpperInvariant();
            if (level.StartsWith("VERBOSE")) level = "INFO";
            var text = m.Groups["text"].Value;
            // Chromium's stderr log reports every console call as INFO (console.error included), so the
            // log level alone is unreliable. Uncaught exceptions and failed loads are recognisable by text.
            if (level == "INFO" && LooksLikeError(text)) level = "ERROR";
            list.Add(new BrowserConsoleMessage(level, text));
        }
        return list;
    }

    private static readonly Regex ErrorText = new(
        @"^Uncaught\b|Failed to load resource|net::ERR_|\b(Reference|Type|Syntax|Range)Error\b",
        RegexOptions.Compiled);

    internal static bool LooksLikeError(string text) => ErrorText.IsMatch(text);

    internal static string? ExtractTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    internal static string ExtractVisibleText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|noscript|template)\b.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<head\b.*?</head>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    private static string Tail(string s, int max) => s.Length <= max ? s.Trim() : s[^max..].Trim();

    private readonly record struct BrowserRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    private static async Task<BrowserRun> RunBrowserAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Browser did not start.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ProcessTimeoutSeconds));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            ct.ThrowIfCancellationRequested();
            return new BrowserRun(-1, string.Empty, string.Empty, TimedOut: true);
        }
        return new BrowserRun(p.ExitCode, await stdout, await stderr, false);
    }

    /// <summary>Finds Chrome/Edge/Chromium: AIAGENT_BROWSER, then well-known install paths, then PATH.</summary>
    internal static string? FindBrowser()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AIAGENT_BROWSER");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LocalAppData")
            }.Where(r => !string.IsNullOrEmpty(r)))
            {
                candidates.Add(Path.Combine(root!, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root!, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }

        foreach (var c in candidates) if (File.Exists(c)) return c;

        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "chrome.exe", "msedge.exe" }
            : new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                try { var full = Path.Combine(dir, n); if (File.Exists(full)) return full; } catch { /* bad PATH entry */ }
            }
        return null;
    }
}
