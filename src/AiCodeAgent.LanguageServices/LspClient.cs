using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using Range = AiCodeAgent.LanguageServices.Models.Range;

namespace AiCodeAgent.LanguageServices;

/// <summary>
/// Manages a single language server process over stdio using JSON-RPC.
/// Handles the initialize/initialized handshake, request/response correlation,
/// and notification dispatch (e.g. textDocument/publishDiagnostics).
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly LspServerDescriptor _descriptor;
    private readonly string _langId;
    private readonly ILogger _logger;
    private readonly string _workspaceRoot;
    private readonly CancellationTokenSource _cts = new();

    private Process? _process;
    private JsonRpc? _rpc;
    private bool _initialized;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<Diagnostic>> _latestDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the server publishes diagnostics for a document.</summary>
    public event EventHandler<LspDiagnosticsEvent>? DiagnosticsPublished;

    /// <summary>Raised when the server is ready (after initialized).</summary>
    public event EventHandler? ServerReady;

    /// <summary>Raised when the server process exits unexpectedly.</summary>
    public event EventHandler<string>? ServerExited;

    public string LangId => _langId;
    public bool IsInitialized => _initialized;

    public LspClient(
        string langId,
        LspServerDescriptor descriptor,
        string workspaceRoot,
        ILogger logger)
    {
        _langId = langId;
        _descriptor = descriptor;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>Start the language server process and perform the LSP handshake.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_process != null)
                throw new InvalidOperationException($"LSP client for '{_langId}' is already started.");
        }

        _logger.LogInformation("Starting LSP server for {LangId}: {Command} {Args}",
            _langId, _descriptor.Command, string.Join(" ", _descriptor.Args));

        var psi = new ProcessStartInfo
        {
            FileName = _descriptor.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspaceRoot
        };

        foreach (var arg in _descriptor.Args)
            psi.ArgumentList.Add(arg);

        _process = new Process { StartInfo = psi };
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start language server: {_descriptor.Command}");

        // Capture stderr for diagnostics
        _ = Task.Run(() => ReadStderrAsync(_process.StandardError, _cts.Token), _cts.Token);

        // Set up JSON-RPC over stdio
        _rpc = JsonRpc.Attach(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            this);

        _rpc.Disconnected += OnRpcDisconnected;

        // Perform the LSP handshake
        var initParams = _descriptor.BuildInitParams();
        var initResult = await _rpc.InvokeWithCancellationAsync<JsonObject>(
            "initialize",
            new object?[] { initParams },
            cancellationToken);

        _logger.LogInformation("LSP server {LangId} initialized: {Result}",
            _langId, initResult?.ToString() ?? "(no result)");

        // Send the initialized notification
        await _rpc.NotifyAsync("initialized", new object?[] { new { } });

        _initialized = true;
        ServerReady?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("LSP server {LangId} is ready", _langId);
    }

    /// <summary>Notify the server that a document was opened.</summary>
    public async Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var item = new TextDocumentItem(uri, languageId, version, text);
        await _rpc!.NotifyAsync("textDocument/didOpen", new object?[] { new { textDocument = item } });
    }

    /// <summary>Notify the server that a document's content changed.</summary>
    public async Task DidChangeAsync(string uri, int version, string text, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var change = new TextDocumentContentChangeEvent(null, null, text);
        var identifier = new VersionedTextDocumentIdentifier(uri, version);
        await _rpc!.NotifyAsync("textDocument/didChange", new object?[] { new { textDocument = identifier, contentChanges = new[] { change } } });
    }

    /// <summary>Notify the server that a document was closed.</summary>
    public async Task DidCloseAsync(string uri, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var identifier = new TextDocumentIdentifier(uri);
        await _rpc!.NotifyAsync("textDocument/didClose", new object?[] { new { textDocument = identifier } });
    }

    /// <summary>Request hover information at a position.</summary>
    public async Task<Hover?> HoverAsync(string uri, Position position, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await _rpc!.InvokeWithCancellationAsync<JsonObject?>(
            "textDocument/hover",
            new object?[] { new { textDocument = new TextDocumentIdentifier(uri), position } },
            cancellationToken);

        return ParseHover(result);
    }

    /// <summary>Request go-to-definition at a position.</summary>
    public async Task<List<Location>> DefinitionAsync(string uri, Position position, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await _rpc!.InvokeWithCancellationAsync<JsonNode?>(
            "textDocument/definition",
            new object?[] { new { textDocument = new TextDocumentIdentifier(uri), position } },
            cancellationToken);

        return ParseLocations(result);
    }

    /// <summary>Request code completions at a position.</summary>
    public async Task<List<CompletionItem>> CompletionAsync(string uri, Position position, CompletionContext? context = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await _rpc!.InvokeWithCancellationAsync<JsonNode?>(
            "textDocument/completion",
            new object?[] { new { textDocument = new TextDocumentIdentifier(uri), position, context } },
            cancellationToken);

        return ParseCompletions(result);
    }

    /// <summary>Request all references to a symbol at a position.</summary>
    public async Task<List<Location>> ReferencesAsync(string uri, Position position, bool includeDeclaration = true, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await _rpc!.InvokeWithCancellationAsync<JsonNode?>(
            "textDocument/references",
            new object?[] { new { textDocument = new TextDocumentIdentifier(uri), position, context = new { includeDeclaration } } },
            cancellationToken);

        return ParseLocations(result);
    }

    /// <summary>Get the latest diagnostics published by the server for a URI.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics(string uri) =>
        _latestDiagnostics.TryGetValue(NormalizeUri(uri), out var diags) ? diags : Array.Empty<Diagnostic>();

    /// <summary>Get diagnostics for all known documents.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> GetAllDiagnostics() =>
        new Dictionary<string, IReadOnlyList<Diagnostic>>(_latestDiagnostics);

    /// <summary>Request document symbols for a file.</summary>
    public async Task<List<DocumentSymbol>> DocumentSymbolsAsync(string uri, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await _rpc!.InvokeWithCancellationAsync<JsonNode?>(
            "textDocument/documentSymbol",
            new object?[] { new { textDocument = new TextDocumentIdentifier(uri) } },
            cancellationToken);

        return ParseDocumentSymbols(result);
    }

    /// <summary>Shutdown the server gracefully.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized || _rpc == null)
            return;

        try
        {
            await _rpc.InvokeWithCancellationAsync<object?>("shutdown", null, cancellationToken);
            await _rpc.NotifyAsync("exit", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during LSP shutdown for {LangId}", _langId);
        }
        finally
        {
            _initialized = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ShutdownAsync();
        }
        catch { /* best-effort */ }

        await _cts.CancelAsync();

        if (_rpc != null)
        {
            _rpc.Dispose();
            _rpc = null;
        }

        if (_process != null)
        {
            _process.Exited -= OnProcessExited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    exitTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _process.WaitForExitAsync(exitTimeout.Token);
                    }
                    catch (OperationCanceledException) { /* give up waiting */ }
                }
            }
            catch { /* best-effort */ }
            _process.Dispose();
            _process = null;
        }

        _cts.Dispose();
    }

    // ===== Notification handlers (called by StreamJsonRpc) =====

    /// <summary>Handles textDocument/publishDiagnostics notifications from the server.</summary>
    [JsonRpcMethod("textDocument/publishDiagnostics")]
    public void OnPublishDiagnostics(JsonObject? parameters)
    {
        if (parameters == null)
            return;

        try
        {
            var uri = parameters["uri"]?.GetValue<string>() ?? string.Empty;
            var diagnostics = new List<Diagnostic>();

            if (parameters["diagnostics"] is JsonArray diagArray)
            {
                foreach (var item in diagArray)
                {
                    if (item is not JsonObject diag)
                        continue;

                    var range = ParseRange(diag["range"] as JsonObject);
                    if (range == null)
                        continue;

                    var severity = (DiagnosticSeverity)(diag["severity"]?.GetValue<int>() ?? (int)DiagnosticSeverity.Error);
                    var message = diag["message"]?.GetValue<string>() ?? string.Empty;
                    var source = diag["source"]?.GetValue<string>();
                    var code = diag["code"]?.ToString();

                    diagnostics.Add(new Diagnostic(range, severity, message, source, code));
                }
            }

            // Cache the latest diagnostics for agent tools to query
            _latestDiagnostics[NormalizeUri(uri)] = diagnostics;

            var filePath = UriToPath(uri);
            DiagnosticsPublished?.Invoke(this, new LspDiagnosticsEvent(uri, filePath, diagnostics));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse publishDiagnostics notification for {LangId}", _langId);
        }
    }

    // ===== Private helpers =====

    private void EnsureInitialized()
    {
        if (!_initialized || _rpc == null)
            throw new InvalidOperationException($"LSP client for '{_langId}' is not initialized.");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("LSP server {LangId} exited with code {ExitCode}", _langId, exitCode);
        _initialized = false;
        ServerExited?.Invoke(this, $"Language server exited with code {exitCode}");
    }

    private void OnRpcDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        _logger.LogWarning("LSP server {LangId} disconnected: {Reason}", _langId, e.Reason);
        _initialized = false;
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[1024];
            while (!token.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                var text = new string(buffer, 0, read);
                _logger.LogDebug("LSP stderr [{LangId}]: {Text}", _langId, text.TrimEnd());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP stderr read ended for {LangId}", _langId);
        }
    }

    private static Range? ParseRange(JsonObject? rangeObj)
    {
        if (rangeObj == null)
            return null;

        var start = ParsePosition(rangeObj["start"] as JsonObject);
        var end = ParsePosition(rangeObj["end"] as JsonObject);
        if (start == null || end == null)
            return null;

        return new Range(start, end);
    }

    private static Position? ParsePosition(JsonObject? posObj)
    {
        if (posObj == null)
            return null;

        var line = posObj["line"]?.GetValue<int>() ?? 0;
        var character = posObj["character"]?.GetValue<int>() ?? 0;
        return new Position(line, character);
    }

    private static Hover? ParseHover(JsonObject? hoverObj)
    {
        if (hoverObj == null)
            return null;

        HoverContents? contents = null;
        if (hoverObj["contents"] is JsonObject contentsObj)
        {
            var kind = contentsObj["kind"]?.GetValue<string>();
            var value = contentsObj["value"]?.GetValue<string>();
            contents = new HoverContents(kind, value);
        }
        else if (hoverObj["contents"] is JsonArray contentsArray)
        {
            // Handle array of MarkupContent / MarkedString
            var parts = new List<string>();
            foreach (var item in contentsArray)
            {
                if (item is JsonObject obj)
                {
                    var value = obj["value"]?.GetValue<string>();
                    if (value != null)
                        parts.Add(value);
                }
                else if (item is JsonValue val)
                {
                    parts.Add(val.ToString());
                }
            }
            if (parts.Count > 0)
                contents = new HoverContents("markdown", string.Join("\n\n", parts));
        }
        else if (hoverObj["contents"] is JsonValue value)
        {
            contents = new HoverContents("markdown", value.ToString());
        }

        var range = ParseRange(hoverObj["range"] as JsonObject);
        return new Hover(contents, range);
    }

    private static List<Location> ParseLocations(JsonNode? result)
    {
        var locations = new List<Location>();

        if (result is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject locObj)
                {
                    var uri = locObj["uri"]?.GetValue<string>() ?? string.Empty;
                    var range = ParseRange(locObj["range"] as JsonObject);
                    if (range != null)
                        locations.Add(new Location(uri, range));
                }
            }
        }
        else if (result is JsonObject single)
        {
            var uri = single["uri"]?.GetValue<string>() ?? string.Empty;
            var range = ParseRange(single["range"] as JsonObject);
            if (range != null)
                locations.Add(new Location(uri, range));
        }

        return locations;
    }

    private static List<CompletionItem> ParseCompletions(JsonNode? result)
    {
        var items = new List<CompletionItem>();

        if (result is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                    items.Add(ParseCompletionItem(obj));
            }
        }
        else if (result is JsonObject obj)
        {
            // Could be a CompletionList
            if (obj["items"] is JsonArray itemsArray)
            {
                foreach (var item in itemsArray)
                {
                    if (item is JsonObject itemObj)
                        items.Add(ParseCompletionItem(itemObj));
                }
            }
            else
            {
                items.Add(ParseCompletionItem(obj));
            }
        }

        return items;
    }

    private static CompletionItem ParseCompletionItem(JsonObject obj)
    {
        var label = obj["label"]?.GetValue<string>() ?? string.Empty;
        var kind = (CompletionItemKind)(obj["kind"]?.GetValue<int>() ?? (int)CompletionItemKind.Text);
        var detail = obj["detail"]?.GetValue<string>();
        var insertText = obj["insertText"]?.GetValue<string>();
        var sortText = obj["sortText"]?.GetValue<string>();
        var filterText = obj["filterText"]?.GetValue<string>();
        var insertTextFormat = obj["insertTextFormat"]?.GetValue<string>();

        TextEdit? textEdit = null;
        if (obj["textEdit"] is JsonObject teObj)
        {
            var range = ParseRange(teObj["range"] as JsonObject);
            var newText = teObj["newText"]?.GetValue<string>() ?? string.Empty;
            if (range != null)
                textEdit = new TextEdit(range, newText);
        }

        string? documentation = null;
        if (obj["documentation"] is JsonObject docObj)
        {
            documentation = docObj["value"]?.GetValue<string>();
        }
        else if (obj["documentation"] is JsonValue docVal)
        {
            documentation = docVal.ToString();
        }

        return new CompletionItem(
            label,
            kind,
            detail,
            documentation,
            insertText,
            sortText,
            filterText,
            textEdit,
            insertTextFormat);
    }

    private static List<DocumentSymbol> ParseDocumentSymbols(JsonNode? result)
    {
        var symbols = new List<DocumentSymbol>();

        if (result is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                    symbols.Add(ParseDocumentSymbol(obj));
            }
        }

        return symbols;
    }

    private static DocumentSymbol ParseDocumentSymbol(JsonObject obj)
    {
        var name = obj["name"]?.GetValue<string>() ?? string.Empty;
        var detail = obj["detail"]?.GetValue<string>();
        var kind = obj["kind"]?.GetValue<int>() ?? 0;
        var range = ParseRange(obj["range"] as JsonObject);
        var selectionRange = ParseRange(obj["selectionRange"] as JsonObject);

        var children = new List<DocumentSymbol>();
        if (obj["children"] is JsonArray childrenArray)
        {
            foreach (var child in childrenArray)
            {
                if (child is JsonObject childObj)
                    children.Add(ParseDocumentSymbol(childObj));
            }
        }

        return new DocumentSymbol(name, detail, kind, range, selectionRange, children);
    }

    private static string NormalizeUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? parsed.AbsoluteUri
            : uri;

    private static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri[7..]);
            // Handle Windows drive letters: file:///C:/path -> C:\path
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
                path = path[1..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        return uri;
    }
}

/// <summary>Represents a symbol in a document.</summary>
public record DocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    Range? Range,
    Range? SelectionRange,
    List<DocumentSymbol> Children);