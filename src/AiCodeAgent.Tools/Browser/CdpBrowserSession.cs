using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCodeAgent.Tools.Browser;

/// <summary>Minimal Chrome DevTools Protocol client over a WebSocket (flat sessions, request/response + events).</summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private int _nextId;

    /// <summary>Raised for every protocol event: (method, params, sessionId).</summary>
    public event Action<string, JsonNode?, string?>? EventReceived;

    public async Task ConnectAsync(Uri url, CancellationToken ct)
    {
        await _ws.ConnectAsync(url, ct);
        _reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject? args = null, string? sessionId = null, CancellationToken ct = default, int timeoutMs = 30_000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args ?? new JsonObject() };
        if (sessionId != null) msg["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            return await tcs.Task;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                JsonNode? node;
                try { node = JsonNode.Parse(ms.ToArray()); } catch { continue; }
                if (node == null) continue;

                if (node["id"] is JsonNode idNode)
                {
                    if (_pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                    {
                        if (node["error"] is JsonNode err)
                            tcs.TrySetException(new InvalidOperationException($"CDP error: {err["message"]?.GetValue<string>() ?? err.ToJsonString()}"));
                        else
                            tcs.TrySetResult(node["result"] ?? new JsonObject());
                    }
                }
                else if (node["method"] is JsonNode m)
                {
                    try { EventReceived?.Invoke(m.GetValue<string>(), node["params"], node["sessionId"]?.GetValue<string>()); }
                    catch { /* a bad handler must not kill the loop */ }
                }
            }
        }
        catch { /* socket closed */ }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException("Browser connection closed."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _ws.Dispose();
        try { if (_reader != null) await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}

/// <summary>One scripted step a browser_check run performs after the page has loaded.</summary>
public sealed class BrowserAction
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("selector")] public string? Selector { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("text")] public string? Text { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("key")] public string? Key { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ms")] public int? Ms { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("script")] public string? Script { get; set; }
}

/// <summary>Result of a browser_check session.</summary>
internal sealed record CdpPageResult(
    string Html,
    string Title,
    string FinalUrl,
    IReadOnlyList<BrowserConsoleMessage> Console,
    IReadOnlyList<string> ActionLog,
    byte[]? Screenshot,
    string? ScreenshotNote);

/// <summary>
/// Drives a headless Chromium through the DevTools protocol: loads the page, records real console levels
/// (Runtime.consoleAPICalled / exceptionThrown / Log.entryAdded / Network.loadingFailed), runs click/type/wait actions
/// and captures the DOM and a screenshot.
/// </summary>
internal static class CdpBrowserSession
{
    public static async Task<CdpPageResult> RunAsync(
        string browserExe, string url, int width, int height, int waitMs,
        IReadOnlyList<BrowserAction> actions, bool screenshot, CancellationToken ct)
    {
        var profile = Path.Combine(Path.GetTempPath(), "aiagent-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Process? proc = null;
        CdpConnection? cdp = null;
        try
        {
            var psi = new ProcessStartInfo(browserExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--mute-audio", "--remote-debugging-port=0", "--remote-allow-origins=*",
                $"--user-data-dir={profile}", $"--window-size={width},{height}", "about:blank"
            }) psi.ArgumentList.Add(a);
            if (Environment.GetEnvironmentVariable("AIAGENT_BROWSER_NO_SANDBOX") == "1") psi.ArgumentList.Add("--no-sandbox");

            proc = Process.Start(psi) ?? throw new InvalidOperationException("Browser did not start.");
            // Drain output so the child never blocks on a full pipe.
            _ = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var wsUrl = await WaitForBrowserEndpointAsync(profile, proc, ct);

            cdp = new CdpConnection();
            var consoleLog = new List<BrowserConsoleMessage>();
            var gate = new object();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? sessionId = null;

            void Add(string level, string text) { lock (gate) consoleLog.Add(new BrowserConsoleMessage(level, text)); }

            cdp.EventReceived += (method, p, sid) =>
            {
                if (sessionId != null && sid != sessionId) return;
                switch (method)
                {
                    case "Runtime.consoleAPICalled":
                        Add(MapConsoleType(p?["type"]?.GetValue<string>()), FormatArgs(p?["args"] as JsonArray));
                        break;
                    case "Runtime.exceptionThrown":
                        Add("ERROR", "Uncaught " + FormatException(p?["exceptionDetails"]));
                        break;
                    case "Log.entryAdded":
                    {
                        var e = p?["entry"];
                        var level = e?["level"]?.GetValue<string>() switch { "error" => "ERROR", "warning" => "WARNING", _ => "INFO" };
                        // "source: javascript" entries duplicate consoleAPICalled/exceptionThrown; keep browser-side ones.
                        if (e?["source"]?.GetValue<string>() is "javascript" or "console-api") break;
                        Add(level, e?["text"]?.GetValue<string>() + (e?["url"] != null ? $" ({e["url"]})" : ""));
                        break;
                    }
                    case "Network.loadingFailed":
                        // ERR_ABORTED is normal (navigations, cancelled prefetch); the rest are real failures.
                        if (p?["canceled"]?.GetValue<bool>() != true && p?["errorText"]?.GetValue<string>() != "net::ERR_ABORTED")
                            Add("ERROR", $"Request failed: {p?["errorText"]?.GetValue<string>()}");
                        break;
                    case "Page.loadEventFired":
                        loaded.TrySetResult();
                        break;
                }
            };

            await cdp.ConnectAsync(new Uri(wsUrl), ct);
            var target = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, ct);
            var targetId = target["targetId"]!.GetValue<string>();
            var attach = await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null, ct);
            sessionId = attach["sessionId"]!.GetValue<string>();

            Task S(string m, JsonObject? a = null) => cdp!.SendAsync(m, a, sessionId, ct);
            await S("Page.enable");
            await S("Runtime.enable");
            await S("Log.enable");
            await S("Network.enable");
            await S("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false });

            await S("Page.navigate", new JsonObject { ["url"] = url });
            var done = await Task.WhenAny(loaded.Task, Task.Delay(30_000, ct));
            if (done != loaded.Task) Add("WARNING", "Page did not finish loading within 30s; continuing with what has rendered.");
            if (waitMs > 0) await Task.Delay(waitMs, ct);

            var log = new List<string>();
            int step = 0;
            foreach (var action in actions.Take(30))
            {
                step++;
                try { log.Add($"{step}. {await ExecuteActionAsync(cdp, sessionId, action, ct)}"); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { log.Add($"{step}. {action.Type}: timed out"); }
                catch (Exception ex) { log.Add($"{step}. {action.Type} FAILED: {ex.Message}"); }
            }
            if (actions.Count > 0) await Task.Delay(150, ct); // let late console output arrive

            var htmlRes = await EvalAsync(cdp, sessionId, "document.documentElement ? document.documentElement.outerHTML : ''", ct);
            var titleRes = await EvalAsync(cdp, sessionId, "document.title", ct);
            var urlRes = await EvalAsync(cdp, sessionId, "location.href", ct);

            byte[]? shot = null;
            string? shotNote = null;
            if (screenshot)
            {
                try
                {
                    var r = await cdp.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" }, sessionId, ct);
                    shot = Convert.FromBase64String(r["data"]!.GetValue<string>());
                }
                catch (Exception ex) { shotNote = "screenshot failed: " + ex.Message; }
            }

            List<BrowserConsoleMessage> snapshot;
            lock (gate) snapshot = consoleLog.ToList();
            return new CdpPageResult(htmlRes, titleRes, urlRes, snapshot, log, shot, shotNote);
        }
        finally
        {
            if (cdp != null) await cdp.DisposeAsync();
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
            for (var i = 0; i < 3; i++)
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
                catch { await Task.Delay(200, CancellationToken.None); }
            }
        }
    }

    private static async Task<string> WaitForBrowserEndpointAsync(string profile, Process proc, CancellationToken ct)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited) throw new InvalidOperationException($"Browser exited early (code {proc.ExitCode}). If running as root or in a container, set AIAGENT_BROWSER_NO_SANDBOX=1.");
            if (File.Exists(file))
            {
                try
                {
                    var lines = File.ReadAllLines(file);
                    if (lines.Length >= 2 && lines[1].StartsWith("/devtools/"))
                        return $"ws://127.0.0.1:{lines[0].Trim()}{lines[1].Trim()}";
                }
                catch (IOException) { /* still being written */ }
            }
            await Task.Delay(50, ct);
        }
        throw new TimeoutException("Browser did not open its DevTools port within 20s.");
    }

    internal static string MapConsoleType(string? type) => type switch
    {
        "error" or "assert" => "ERROR",
        "warning" => "WARNING",
        _ => "INFO"
    };

    internal static string FormatArgs(JsonArray? args)
    {
        if (args == null) return string.Empty;
        return string.Join(" ", args.Select(a =>
        {
            if (a == null) return "";
            var v = a["value"];
            if (v != null) return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString();
            return a["description"]?.GetValue<string>() ?? a["type"]?.GetValue<string>() ?? "";
        }));
    }

    internal static string FormatException(JsonNode? details)
    {
        if (details == null) return "exception";
        var desc = details["exception"]?["description"]?.GetValue<string>();
        var text = desc?.Split('\n')[0] ?? details["text"]?.GetValue<string>() ?? "exception";
        var line = details["lineNumber"]?.GetValue<int>();
        var src = details["url"]?.GetValue<string>();
        return line != null && !string.IsNullOrEmpty(src) ? $"{text} ({src}:{line + 1})" : text;
    }

    private static async Task<string> EvalAsync(CdpConnection cdp, string sessionId, string expr, CancellationToken ct)
    {
        var r = await cdp.SendAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expr, ["returnByValue"] = true, ["awaitPromise"] = true }, sessionId, ct);
        if (r["exceptionDetails"] != null) throw new InvalidOperationException(FormatException(r["exceptionDetails"]));
        var v = r["result"]?["value"];
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? string.Empty;
    }

    private const string FindCenterJs = @"(sel) => {
        const el = document.querySelector(sel);
        if (!el) return null;
        el.scrollIntoView({block: 'center', inline: 'center'});
        const r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return { hidden: true };
        return { x: r.left + r.width / 2, y: r.top + r.height / 2, tag: el.tagName.toLowerCase(), text: (el.innerText || el.value || '').slice(0, 40) };
    }";

    private static string Call(string fn, string arg) => $"({fn})({JsonSerializer.Serialize(arg)})";

    private static async Task<JsonNode?> LocateAsync(CdpConnection cdp, string sessionId, string selector, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var json = await EvalAsync(cdp, sessionId, $"JSON.stringify({Call(FindCenterJs, selector)})", ct);
            var node = JsonNode.Parse(json);
            if (node != null && node["x"] != null) return node;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(node == null ? $"no element matches '{selector}'" : $"element '{selector}' is not visible");
            await Task.Delay(100, ct);
        }
    }

    private static async Task<string> ExecuteActionAsync(CdpConnection cdp, string sessionId, BrowserAction a, CancellationToken ct)
    {
        switch (a.Type.ToLowerInvariant())
        {
            case "click":
            {
                if (string.IsNullOrEmpty(a.Selector)) throw new ArgumentException("'selector' is required");
                var pos = await LocateAsync(cdp, sessionId, a.Selector, ct);
                var x = pos!["x"]!.GetValue<double>();
                var y = pos["y"]!.GetValue<double>();
                await Mouse(cdp, sessionId, "mouseMoved", x, y, ct);
                await Mouse(cdp, sessionId, "mousePressed", x, y, ct);
                await Mouse(cdp, sessionId, "mouseReleased", x, y, ct);
                await Task.Delay(250, ct);
                return $"click {a.Selector} ok ({pos["tag"]} \"{pos["text"]}\")";
            }
            case "type":
            {
                if (string.IsNullOrEmpty(a.Selector)) throw new ArgumentException("'selector' is required");
                await LocateAsync(cdp, sessionId, a.Selector, ct);
                await EvalAsync(cdp, sessionId, $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(a.Selector)}); e.focus(); if ('select' in e) e.select(); }})()", ct);
                await cdp.SendAsync("Input.insertText", new JsonObject { ["text"] = a.Text ?? "" }, sessionId, ct);
                await Task.Delay(100, ct);
                return $"type {a.Selector} ok ({(a.Text ?? "").Length} chars)";
            }
            case "press":
            {
                var key = a.Key ?? throw new ArgumentException("'key' is required");
                var (vk, code, text) = KeyInfo(key);
                var down = new JsonObject { ["type"] = text != null ? "keyDown" : "rawKeyDown", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk };
                if (text != null) down["text"] = text;
                await cdp.SendAsync("Input.dispatchKeyEvent", down, sessionId, ct);
                await cdp.SendAsync("Input.dispatchKeyEvent",
                    new JsonObject { ["type"] = "keyUp", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk }, sessionId, ct);
                await Task.Delay(250, ct);
                return $"press {key} ok";
            }
            case "wait":
            {
                var ms = Math.Clamp(a.Ms ?? 500, 0, 15_000);
                await Task.Delay(ms, ct);
                return $"wait {ms}ms";
            }
            case "wait_for":
            {
                if (string.IsNullOrEmpty(a.Selector) && string.IsNullOrEmpty(a.Text)) throw new ArgumentException("'selector' or 'text' is required");
                var timeout = Math.Clamp(a.Ms ?? 5000, 100, 15_000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                var expr = !string.IsNullOrEmpty(a.Selector)
                    ? $"!!document.querySelector({JsonSerializer.Serialize(a.Selector)})"
                    : $"(document.body ? document.body.innerText : '').includes({JsonSerializer.Serialize(a.Text)})";
                while (true)
                {
                    if (await EvalAsync(cdp, sessionId, expr, ct) == "true") return $"wait_for {a.Selector ?? $"text \"{a.Text}\""} ok";
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException($"'{a.Selector ?? a.Text}' did not appear within {timeout}ms");
                    await Task.Delay(100, ct);
                }
            }
            case "eval":
            {
                if (string.IsNullOrEmpty(a.Script)) throw new ArgumentException("'script' is required");
                var result = await EvalAsync(cdp, sessionId, a.Script, ct);
                if (result.Length > 300) result = result[..300] + "...";
                return $"eval => {result}";
            }
            default:
                throw new ArgumentException($"unknown action type '{a.Type}' (use click, type, press, wait, wait_for, eval)");
        }
    }

    private static Task Mouse(CdpConnection cdp, string sessionId, string type, double x, double y, CancellationToken ct) =>
        cdp.SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = type == "mouseMoved" ? "none" : "left",
            ["buttons"] = type == "mousePressed" ? 1 : 0, ["clickCount"] = type == "mouseMoved" ? 0 : 1
        }, sessionId, ct);

    private static (int vk, string code, string? text) KeyInfo(string key) => key switch
    {
        "Enter" => (13, "Enter", "\r"),
        "Tab" => (9, "Tab", null),
        "Escape" => (27, "Escape", null),
        "Backspace" => (8, "Backspace", null),
        "ArrowDown" => (40, "ArrowDown", null),
        "ArrowUp" => (38, "ArrowUp", null),
        "ArrowLeft" => (37, "ArrowLeft", null),
        "ArrowRight" => (39, "ArrowRight", null),
        " " or "Space" => (32, "Space", " "),
        _ when key.Length == 1 => (char.ToUpperInvariant(key[0]), "Key" + char.ToUpperInvariant(key[0]), key),
        _ => throw new ArgumentException($"unsupported key '{key}'")
    };
}
