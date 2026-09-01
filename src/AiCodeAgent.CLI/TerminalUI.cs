using System.Diagnostics;
using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.CLI;

public class TerminalUI
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<TerminalUI> _logger;
    private readonly SessionRecorder _recorder;
    private readonly SessionExportService _exportService;
    private readonly SessionImporter _importer;
    private readonly TaskHistoryStore _taskHistory;
    private readonly SessionPersistenceManager? _persistence;
    private readonly IAgentEventBus? _eventBus;
    private readonly ICheckpointManager? _checkpointManager;
    private readonly IProjectMemoryLoader? _memoryLoader;
    private readonly IAutoMemory? _autoMemory;
    private readonly LearningExtractor? _learningExtractor;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly IMcpRegistry? _mcpRegistry;
    private readonly IHookRunner? _hookRunner;
    private readonly IPermissionManager? _permissionManager;
    private readonly ConfigurationService? _configurationService;
    private readonly ModelRegistry? _modelRegistry;
    private string _sessionId = Guid.NewGuid().ToString();
    private string _taskTitle = "Untitled Task";
    private AgentOptions _options = null!;

    /// <summary>Sentinel returned by the line reader when the user presses Esc twice.</summary>
    private const string EscTwiceSentinel = "\u241B\u241B";

    private static class Colors
    {
        public static readonly ConsoleColor User = ConsoleColor.Cyan;
        public static readonly ConsoleColor Assistant = ConsoleColor.White;
        public static readonly ConsoleColor Tool = ConsoleColor.Yellow;
        public static readonly ConsoleColor ToolResult = ConsoleColor.DarkGray;
        public static readonly ConsoleColor Error = ConsoleColor.Red;
        public static readonly ConsoleColor Success = ConsoleColor.Green;
        public static readonly ConsoleColor Info = ConsoleColor.DarkCyan;
        public static readonly ConsoleColor Prompt = ConsoleColor.Magenta;
    }

    public TerminalUI(
        IAgentOrchestrator orchestrator,
        IToolRegistry toolRegistry,
        ILogger<TerminalUI> logger,
        SessionRecorder recorder,
        SessionExportService exportService,
        SessionImporter importer,
        TaskHistoryStore taskHistory,
        SessionPersistenceManager? persistence = null,
        IAgentEventBus? eventBus = null,
        ICheckpointManager? checkpointManager = null,
        IProjectMemoryLoader? memoryLoader = null,
        IAutoMemory? autoMemory = null,
        LearningExtractor? learningExtractor = null,
        ISkillRegistry? skillRegistry = null,
        IMcpRegistry? mcpRegistry = null,
        IHookRunner? hookRunner = null,
        IPermissionManager? permissionManager = null,
        ConfigurationService? configurationService = null,
        ModelRegistry? modelRegistry = null)
    {
        _orchestrator = orchestrator;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _recorder = recorder;
        _exportService = exportService;
        _importer = importer;
        _taskHistory = taskHistory;
        _persistence = persistence;
        _eventBus = eventBus;
        _checkpointManager = checkpointManager;
        _memoryLoader = memoryLoader;
        _autoMemory = autoMemory;
        _learningExtractor = learningExtractor;
        _skillRegistry = skillRegistry;
        _mcpRegistry = mcpRegistry;
        _hookRunner = hookRunner;
        _permissionManager = permissionManager;
        _configurationService = configurationService;
        _modelRegistry = modelRegistry;
    }

    public Task RunAsync(AgentOptions options, string? sessionId = null)
    {
        if (sessionId != null)
            _sessionId = sessionId;
        _options = options;
        return RunAsyncCore();
    }

    private async Task RunAsyncCore()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.Clear(); }
        catch (System.IO.IOException) { /* no console handle (redirected I/O) - skip clearing */ }

        PrintBanner();
        PrintHelp();

        // Load project memory (if any) so it is injected into every prompt.
        await RefreshProjectMemoryAsync();

        // Load auto-memory (learned preferences) and inject into options.
        await RefreshAutoMemoryAsync();

        _recorder.Start(_sessionId);

        // Wire session persistence: subscribe to the event bus and append
        // user/assistant/tool entries to the JSONL session log.
        CancellationTokenSource? persistenceCts = null;
        if (_persistence != null && _eventBus != null)
        {
            persistenceCts = new CancellationTokenSource();
            _ = Task.Run(() => PersistEventsAsync(_eventBus, persistenceCts.Token), persistenceCts.Token);
        }

        var history = new List<string>();
        var historyIndex = 0;

        while (true)
        {
            PrintPrompt(_options.WorkingDirectory);

            var input = ReadLineWithHistory(history, ref historyIndex);

            // Stdin reached EOF (piped input exhausted) → exit gracefully
            // instead of spinning forever on empty input.
            if (input == null)
                break;

            // Esc twice → rewind to the previous checkpoint.
            if (input == EscTwiceSentinel)
            {
                await UndoAsync();
                continue;
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            if (history.Count == 0 || history[^1] != input)
                history.Add(input);
            historyIndex = history.Count;

            if (await HandleCommandAsync(input)) continue;

            // Save user message to task history
            if (_taskTitle == "Untitled Task")
                _taskTitle = input.Length > 60 ? input[..60] : input;
            await _taskHistory.AddMessageAsync(_sessionId, _taskTitle, new TaskMessageRecord
            {
                Role = "User",
                Content = input,
                Timestamp = DateTime.UtcNow
            });

            // Persist user message to JSONL session log
            if (_persistence != null)
            {
                await _persistence.AppendAsync(new SessionEntry
                {
                    Type = SessionEntryTypes.User,
                    Timestamp = DateTime.UtcNow,
                    Payload = new() { ["content"] = input }
                });
            }

            Console.WriteLine();
            await StreamResponseAsync(input, captureLearnings: true);
            Console.WriteLine();
        }

        // Graceful shutdown (EOF on stdin): stop persistence and recorder.
        persistenceCts?.Cancel();
        _recorder.Stop();
        WriteColored("Goodbye!\n", Colors.Info);
    }

    private async Task StreamResponseAsync(string userMessage, bool captureLearnings = false)
    {
        using var cts = new CancellationTokenSource();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var isFirstToken = true;
        var toolDepth = 0;
        var assistantText = new StringBuilder();

        try
        {
            await foreach (var evt in _orchestrator.StreamRunAsync(
                userMessage, _sessionId, _options, cts.Token))
            {
                switch (evt)
                {
                    case TextDeltaEvent delta:
                        if (isFirstToken)
                        {
                            WriteColored("\nAssistant: ", Colors.Assistant);
                            isFirstToken = false;
                        }
                        WriteColored(delta.Delta, Colors.Assistant);
                        assistantText.Append(delta.Delta);
                        break;

                    case ToolCallStartEvent toolStart:
                        toolDepth++;
                        Console.WriteLine();
                        WriteToolCallStart(toolStart.Call, toolDepth);
                        break;

                    case ToolCallEndEvent toolEnd:
                        WriteToolCallEnd(toolEnd.Call, toolEnd.Result, toolEnd.Duration);
                        toolDepth--;
                        isFirstToken = true;
                        // Save tool call to task history
                        await _taskHistory.AddToolCallAsync(_sessionId, new TaskToolCallRecord
                        {
                            ToolName = toolEnd.Call.Name,
                            Arguments = toolEnd.Call.Arguments?.ToString(),
                            Output = toolEnd.Result.Content,
                            IsError = toolEnd.Result.IsError,
                            Timestamp = DateTime.UtcNow
                        });
                        break;

                    case AgentFinishedEvent finished:
                        Console.WriteLine();
                        WriteStats(finished.Response);
                        // Save assistant response to task history
                        if (assistantText.Length > 0)
                        {
                            await _taskHistory.AddMessageAsync(_sessionId, _taskTitle, new TaskMessageRecord
                            {
                                Role = "Assistant",
                                Content = assistantText.ToString(),
                                Timestamp = DateTime.UtcNow
                            });
                        }

                        // Auto-capture learnings from the user's message (Feature 05).
                        if (captureLearnings && _autoMemory != null && _learningExtractor != null)
                        {
                            try
                            {
                                var learnings = _learningExtractor.Extract(_sessionId, userMessage, assistantText.ToString());
                                foreach (var learning in learnings)
                                {
                                    await _autoMemory.CaptureAsync(_sessionId, learning);
                                }
                                if (learnings.Count > 0)
                                {
                                    await RefreshAutoMemoryAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Auto-memory capture failed");
                            }
                        }
                        break;

                    case AgentErrorEvent error:
                        Console.WriteLine();
                        WriteColored($"\nError: {error.Error.Message}\n", Colors.Error);
                        break;

                    case ApprovalRequestEvent approval:
                        Console.WriteLine();
                        var approved = PromptApproval(approval.Call);
                        approval.Approval.TrySetResult(approved);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            WriteColored("\nCancelled\n", Colors.Info);
        }
    }

    private bool PromptApproval(ToolCall call)
    {
        WriteColored($"Approve {call.Name}? [y/N] ", Colors.Prompt);

        // Non-interactive (piped stdin): ReadKey would throw without a console
        // handle, so read an answer line instead; EOF or anything but "y" denies.
        if (Console.IsInputRedirected)
        {
            var answer = Console.ReadLine();
            WriteColored($"{answer}\n", Colors.User);
            return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        }

        var key = Console.ReadKey(intercept: false);
        Console.WriteLine();
        return key.Key == ConsoleKey.Y;
    }

    private void WriteToolCallStart(ToolCall call, int depth)
    {
        var indent = new string(' ', (depth - 1) * 2);
        var args = FormatArguments(call.Arguments);
        WriteColored($"{indent}{call.Name}({args})\n", Colors.Tool);
    }

    private void WriteToolCallEnd(ToolCall call, ToolResult result, TimeSpan duration)
    {
        var preview = result.Content.Length > 200
            ? result.Content[..200] + "..."
            : result.Content;

        var color = result.IsError ? Colors.Error : Colors.ToolResult;
        var icon = result.IsError ? "X" : "OK";
        WriteColored($"   [{icon}] {call.Name} ({duration.TotalMilliseconds:F0}ms): {preview}\n", color);
    }

    private void WriteStats(AgentResponse response)
    {
        WriteColored($"\n--- {response.ToolExecutions.Count} tool calls | {response.Duration.TotalSeconds:F1}s ---\n",
            Colors.Info);
    }

    private static string FormatArguments(Dictionary<string, object?> args)
    {
        if (args.Count == 0) return "";
        var parts = args.Take(3).Select(kvp =>
        {
            var val = kvp.Value?.ToString() ?? "null";
            if (val.Length > 30) val = val[..30] + "...";
            return $"{kvp.Key}: \"{val}\"";
        });
        return string.Join(", ", parts) + (args.Count > 3 ? ", ..." : "");
    }

    private async Task<bool> HandleCommandAsync(string input)
    {
        var trimmed = input.Trim();

        switch (trimmed.ToLower())
        {
            case "/help" or "/h":
                PrintHelp();
                return true;

            case "/clear" or "/c":
                try { Console.Clear(); }
                catch (System.IO.IOException) { /* no console handle (redirected I/O) */ }
                PrintBanner();
                return true;

            case "/reset":
                _recorder.Stop();
                _sessionId = _persistence?.CreateNew() ?? Guid.NewGuid().ToString();
                _taskTitle = "Untitled Task";
                _recorder.Start(_sessionId);
                WriteColored("Session reset\n", Colors.Success);
                return true;

            case var s when s.StartsWith("/branch"):
                await BranchSessionAsync(s["/branch".Length..].Trim());
                return true;

            case "/tasks":
                await ListTasksAsync();
                return true;

            case var s when s.StartsWith("/task "):
                await ShowTaskAsync(s[6..].Trim());
                return true;

            case var s when s.StartsWith("/export "):
                await ExportSessionAsync(s[8..].Trim());
                return true;

            case var s when s.StartsWith("/import "):
                await ImportSessionAsync(s[8..].Trim());
                return true;

            case "/undo":
                await UndoAsync();
                return true;

            case "/checkpoints":
                await ListCheckpointsAsync();
                return true;

            case "/init":
                await InitMemoryAsync();
                return true;

            case "/doctor":
                await DoctorAsync();
                return true;

            case "/memory":
                await ShowMemoryAsync();
                return true;

            case "/automemory":
                await ShowAutoMemoryAsync();
                return true;

            case var s when s.StartsWith("/automemory edit"):
                await EditAutoMemoryAsync();
                return true;

            case "/mcp":
                await ShowMcpStatusAsync();
                return true;

            case "/tools":
                PrintTools();
                return true;

            case "/hooks":
                PrintHooks();
                return true;

            case "/skills":
                PrintSkills();
                return true;

            case var s when s.StartsWith("/skill "):
                await ActivateSkillAsync(s[7..].Trim());
                return true;

            case "/exit" or "/quit" or "exit" or "quit":
                WriteColored("Goodbye!\n", Colors.Info);
                Environment.Exit(0);
                return true;

            case "/model":
                await ShowCurrentModelAsync();
                return true;

            case var s when s.StartsWith("/model "):
                await SetModelAsync(s[7..].Trim());
                return true;

            case "/models":
                await ListModelsAsync();
                return true;

            case "/view":
                WriteColored("Usage: /view path  (e.g. /view src/Program.cs)\n", Colors.Error);
                return true;

            case var s when s.StartsWith("/view "):
                ViewFile(s[6..].Trim());
                return true;

            case "/edit":
                WriteColored("Usage: /edit path  (e.g. /edit src/Program.cs)\n", Colors.Error);
                return true;

            case var s when s.StartsWith("/edit "):
                EditFile(s[6..].Trim());
                return true;

            case var s when s.StartsWith("/cd "):
                var newDir = s[4..].Trim();
                if (Directory.Exists(newDir))
                {
                    Directory.SetCurrentDirectory(newDir);
                    _options = _options with { WorkingDirectory = newDir };
                    await RefreshProjectMemoryAsync();
                    WriteColored($"Changed to: {newDir}\n", Colors.Success);
                }
                else
                {
                    WriteColored($"Directory not found: {newDir}\n", Colors.Error);
                }
                return true;

            default:
                return false;
        }
    }

    private async Task PersistEventsAsync(IAgentEventBus eventBus, CancellationToken ct)
    {
        await foreach (var evt in eventBus.GetEventsAsync(ct).ConfigureAwait(false))
        {
            if (_persistence == null) continue;

            SessionEntry? entry = evt switch
            {
                TextDeltaEvent delta => new SessionEntry
                {
                    Type = SessionEntryTypes.Assistant,
                    Timestamp = DateTime.UtcNow,
                    Payload = new() { ["content"] = delta.Delta }
                },
                ToolCallEndEvent toolEnd => new SessionEntry
                {
                    Type = SessionEntryTypes.ToolResult,
                    Timestamp = DateTime.UtcNow,
                    Payload = new()
                    {
                        ["tool"] = toolEnd.Call.Name,
                        ["result"] = toolEnd.Result.Content,
                        ["isError"] = toolEnd.Result.IsError
                    }
                },
                AgentFinishedEvent finished => new SessionEntry
                {
                    Type = SessionEntryTypes.Finished,
                    Timestamp = DateTime.UtcNow,
                    Payload = new()
                    {
                        ["toolCalls"] = finished.Response.ToolExecutions.Count,
                        ["durationMs"] = finished.Response.Duration.TotalMilliseconds
                    }
                },
                _ => null
            };

            if (entry != null)
                await _persistence.AppendAsync(entry, ct).ConfigureAwait(false);
        }
    }

    private async Task BranchSessionAsync(string sessionId)
    {
        if (_persistence == null)
        {
            WriteColored("Session persistence is not available.\n", Colors.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = _sessionId;
        }

        try
        {
            _recorder.Stop();
            var newId = await _persistence.ForkAsync(sessionId);
            _sessionId = newId;
            _recorder.Start(_sessionId);
            WriteColored($"Forked session '{sessionId}' -> '{newId}'\n", Colors.Success);
        }
        catch (Exception ex)
        {
            WriteColored($"Fork failed: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task ExportSessionAsync(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            WriteColored("Usage: /export <path>  (e.g. /export session.agentsession)\n", Colors.Error);
            return;
        }

        try
        {
            await _exportService.ExportAsync(_sessionId, outputPath);
            WriteColored($"Session exported to {outputPath}\n", Colors.Success);
        }
        catch (Exception ex)
        {
            WriteColored($"Export failed: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task ImportSessionAsync(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            WriteColored("Usage: /import <path>  (e.g. /import session.agentsession)\n", Colors.Error);
            return;
        }

        try
        {
            var result = await _importer.ImportAsync(bundlePath);
            WriteColored($"Session imported: {result.MessageCount} messages, " +
                         $"{result.ToolCallCount} tool calls, {result.HunkCount} hunks, " +
                         $"{result.CheckpointCount} checkpoints\n", Colors.Success);

            foreach (var conflict in result.Conflicts)
            {
                WriteColored($"  ⚠ {conflict.Message}\n", Colors.Error);
            }
        }
        catch (Exception ex)
        {
            WriteColored($"Import failed: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task UndoAsync()
    {
        if (_checkpointManager == null)
        {
            WriteColored("Checkpoint manager is not available.\n", Colors.Error);
            return;
        }

        try
        {
            var checkpoints = await _checkpointManager.ListAsync(_sessionId);
            if (checkpoints.Count == 0)
            {
                WriteColored("No checkpoints available to undo.\n", Colors.Info);
                return;
            }

            var latest = checkpoints[0];
            var restored = await _checkpointManager.RestoreAsync(_sessionId, latest.CheckpointId, _eventBus);
            if (restored)
                WriteColored($"Restored checkpoint: {latest.CheckpointId} ({latest.FilePath})\n", Colors.Success);
            else
                WriteColored($"Failed to restore checkpoint {latest.CheckpointId} (may be a symlink/hard-link).\n", Colors.Error);
        }
        catch (Exception ex)
        {
            WriteColored($"Undo failed: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task ListCheckpointsAsync()
    {
        if (_checkpointManager == null)
        {
            WriteColored("Checkpoint manager is not available.\n", Colors.Error);
            return;
        }

        try
        {
            var checkpoints = await _checkpointManager.ListAsync(_sessionId);
            if (checkpoints.Count == 0)
            {
                WriteColored("No checkpoints for this session.\n", Colors.Info);
                return;
            }

            WriteColored($"\nCheckpoints ({checkpoints.Count}):\n", Colors.Info);
            WriteColored($"  {"ID",-24} {"File",-40} {"Turn",-12} {"Time"}\n", ConsoleColor.DarkGray);
            WriteColored(new string('-', 90) + "\n", ConsoleColor.DarkGray);

            for (var i = 0; i < checkpoints.Count; i++)
            {
                var c = checkpoints[i];
                var file = c.FilePath.Length > 38 ? c.FilePath[..35] + "..." : c.FilePath;
                var turn = c.TurnId.Length > 10 ? c.TurnId[..10] : c.TurnId;
                WriteColored($"  {i + 1}. ", Colors.Tool);
                WriteColored($"{c.CheckpointId,-24} ", Colors.Assistant);
                WriteColored($"{file,-40} ", ConsoleColor.Gray);
                WriteColored($"{turn,-12} ", ConsoleColor.Gray);
                WriteColored($"{c.Timestamp:HH:mm:ss}\n", ConsoleColor.DarkGray);
            }

            WriteColored("\nEnter number to restore (or Enter to cancel): ", Colors.Prompt);
            var input = Console.ReadLine();
            if (int.TryParse(input, out var sel) && sel >= 1 && sel <= checkpoints.Count)
            {
                var chosen = checkpoints[sel - 1];
                var restored = await _checkpointManager.RestoreAsync(_sessionId, chosen.CheckpointId, _eventBus);
                if (restored)
                    WriteColored($"Restored checkpoint: {chosen.CheckpointId} ({chosen.FilePath})\n", Colors.Success);
                else
                    WriteColored($"Failed to restore checkpoint {chosen.CheckpointId} (may be a symlink/hard-link).\n", Colors.Error);
            }
        }
        catch (Exception ex)
        {
            WriteColored($"Error listing checkpoints: {ex.Message}\n", Colors.Error);
        }
    }

    /// <summary>Loads (or reloads) the project memory for the current working directory.</summary>
    private async Task RefreshProjectMemoryAsync()
    {
        if (_memoryLoader == null) return;

        try
        {
            var memory = await _memoryLoader.LoadAsync(_options.WorkingDirectory);
            _options = _options with { ProjectMemory = memory };
            if (memory is { HasContent: true })
                WriteColored($"Loaded project memory from {memory.FilePath}\n", Colors.Info);
        }
        catch (Exception ex)
        {
            WriteColored($"Failed to load project memory: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task InitMemoryAsync()
    {
        if (_memoryLoader == null)
        {
            WriteColored("Project memory loader is not available.\n", Colors.Error);
            return;
        }

        try
        {
            var path = await _memoryLoader.InitAsync(_options.WorkingDirectory);
            WriteColored($"Created project memory file: {path}\n", Colors.Success);
            WriteColored("Edit it to add project-specific instructions and conventions.\n", Colors.Info);
            await RefreshProjectMemoryAsync();
        }
        catch (Exception ex)
        {
            WriteColored($"Init failed: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task DoctorAsync()
    {
        if (_memoryLoader == null)
        {
            WriteColored("Project memory loader is not available.\n", Colors.Error);
            return;
        }

        try
        {
            var report = await _memoryLoader.DiagnoseAsync(_options.WorkingDirectory);
            WriteColored($"\nProject Doctor Report ({(report.AllOk ? "ALL OK" : "ISSUES FOUND")}):\n",
                report.AllOk ? Colors.Success : Colors.Error);
            WriteColored(new string('-', 60) + "\n", ConsoleColor.DarkGray);
            foreach (var check in report.Checks)
            {
                var (icon, color) = check.Status switch
                {
                    DoctorStatus.Ok => ("OK", Colors.Success),
                    DoctorStatus.Warning => ("WARN", ConsoleColor.Yellow),
                    DoctorStatus.Error => ("FAIL", Colors.Error),
                    _ => ("?", ConsoleColor.Gray)
                };
                WriteColored($"  {icon,-5} ", color);
                WriteColored($"{check.Name,-25} ", ConsoleColor.Gray);
                WriteColored($"{check.Detail}\n", ConsoleColor.DarkGray);
                if (!string.IsNullOrEmpty(check.FixHint))
                    WriteColored($"        Fix: {check.FixHint}\n", ConsoleColor.DarkGray);
            }
            WriteColored("\n", ConsoleColor.Gray);
        }
        catch (Exception ex)
        {
            WriteColored($"Doctor failed: {ex.Message}\n", Colors.Error);
        }
    }

    /// <summary>Load (or reload) the bounded auto-memory into options.</summary>
    private async Task RefreshAutoMemoryAsync()
    {
        if (_autoMemory == null) return;
        try
        {
            var content = await _autoMemory.LoadAsync();
            _options = _options with { AutoMemory = content };
            if (!string.IsNullOrWhiteSpace(content))
                WriteColored($"Loaded auto-memory from {_autoMemory.GetFilePath()}\n", Colors.Info);
        }
        catch (Exception ex)
        {
            WriteColored($"Failed to load auto-memory: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task ShowAutoMemoryAsync()
    {
        if (_autoMemory == null)
        {
            WriteColored("Auto-memory is not available.\n", Colors.Error);
            return;
        }

        await RefreshAutoMemoryAsync();
        var content = _options.AutoMemory;
        if (string.IsNullOrWhiteSpace(content))
        {
            WriteColored("No auto-memory found. Learnings are captured automatically as you work.\n", Colors.Info);
            return;
        }

        WriteColored($"\nAuto-Memory ({_autoMemory.GetFilePath()}):\n", Colors.Info);
        WriteColored(new string('-', 60) + "\n", ConsoleColor.DarkGray);
        WriteColored(content + "\n", ConsoleColor.Gray);
    }

    private async Task EditAutoMemoryAsync()
    {
        if (_autoMemory == null)
        {
            WriteColored("Auto-memory is not available.\n", Colors.Error);
            return;
        }

        var path = _autoMemory.GetFilePath();
        try
        {
            if (!File.Exists(path))
            {
                await _autoMemory.SaveAsync();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDITOR"))
                    ? "notepad"
                    : Environment.GetEnvironmentVariable("EDITOR"),
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            WriteColored($"Opening {path} in editor...\n", Colors.Info);
        }
        catch (Exception ex)
        {
            WriteColored($"Failed to open editor: {ex.Message}\n", Colors.Error);
            WriteColored($"File path: {path}\n", Colors.Info);
        }
    }

    /// <summary>Prints a file's contents with line numbers so project source files can be inspected inline.</summary>
    private void ViewFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            WriteColored($"File not found: {path}\n", Colors.Error);
            return;
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            if (content.Contains('\0'))
            {
                WriteColored($"{fullPath} appears to be a binary file; use /edit to open it in an editor.\n", Colors.Error);
                return;
            }

            var lines = content.Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0)
            {
                lines = lines[..^1]; // drop the empty entry produced by a trailing newline
            }

            const int maxLines = 500;
            var digitCount = Math.Max(lines.Length, 1).ToString().Length;
            WriteColored($"\n{fullPath} ({lines.Length} lines)\n", Colors.Info);
            WriteColored(new string('-', 60) + "\n", ConsoleColor.DarkGray);

            for (var i = 0; i < Math.Min(lines.Length, maxLines); i++)
            {
                WriteColored($"{(i + 1).ToString().PadLeft(digitCount)} | {lines[i].TrimEnd('\r')}\n", ConsoleColor.Gray);
            }

            if (lines.Length > maxLines)
            {
                WriteColored($"... {lines.Length - maxLines} more lines (use /edit to open the file in an editor)\n", Colors.Info);
            }
        }
        catch (Exception ex)
        {
            WriteColored($"Failed to read file: {ex.Message}\n", Colors.Error);
        }
    }

    /// <summary>Opens a file in the user's preferred editor (EDITOR env var, falling back to notepad) so project source files can be modified.</summary>
    private void EditFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            WriteColored($"File not found: {path}\n", Colors.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDITOR"))
                    ? "notepad"
                    : Environment.GetEnvironmentVariable("EDITOR"),
                Arguments = $"\"{fullPath}\"",
                UseShellExecute = true
            });
            WriteColored($"Opening {fullPath} in editor...\n", Colors.Info);
        }
        catch (Exception ex)
        {
            WriteColored($"Failed to open editor: {ex.Message}\n", Colors.Error);
            WriteColored($"File path: {fullPath}\n", Colors.Info);
        }
    }

    private async Task ShowMemoryAsync()
    {
        if (_memoryLoader == null)
        {
            WriteColored("Project memory loader is not available.\n", Colors.Error);
            return;
        }

        await RefreshProjectMemoryAsync();

        var memory = _options.ProjectMemory;
        if (memory == null || !memory.HasContent)
        {
            WriteColored("No project memory found. Use /init to create one.\n", Colors.Info);
            return;
        }

        WriteColored($"\nProject Memory ({memory.FilePath}):\n", Colors.Info);
        WriteColored(new string('-', 60) + "\n", ConsoleColor.DarkGray);
        if (!string.IsNullOrWhiteSpace(memory.CompactInstructions))
            WriteColored($"Compact Instructions:\n{memory.CompactInstructions}\n\n", ConsoleColor.Gray);
        if (!string.IsNullOrWhiteSpace(memory.BuildCommands))
            WriteColored($"Build Commands:\n{memory.BuildCommands}\n\n", ConsoleColor.Gray);
        if (!string.IsNullOrWhiteSpace(memory.TestCommands))
            WriteColored($"Test Commands:\n{memory.TestCommands}\n\n", ConsoleColor.Gray);
        if (!string.IsNullOrWhiteSpace(memory.LintCommands))
            WriteColored($"Lint Commands:\n{memory.LintCommands}\n\n", ConsoleColor.Gray);
        if (!string.IsNullOrWhiteSpace(memory.Conventions))
            WriteColored($"Conventions:\n{memory.Conventions}\n\n", ConsoleColor.Gray);
        if (memory.CustomSections.Count > 0)
        {
            foreach (var kvp in memory.CustomSections)
                WriteColored($"{kvp.Key}:\n{kvp.Value}\n\n", ConsoleColor.Gray);
        }
    }

    private void PrintTools()
    {
        WriteColored("\nAvailable tools:\n", Colors.Info);
        foreach (var tool in _toolRegistry.GetAllTools())
        {
            WriteColored($"  {tool.Name,-25}", Colors.Tool);
            WriteColored($"{tool.Description}\n", ConsoleColor.Gray);
        }
        Console.WriteLine();
    }

    private async Task ShowMcpStatusAsync()
    {
        if (_mcpRegistry == null)
        {
            WriteColored("MCP registry is not available.\n", Colors.Error);
            return;
        }

        var servers = _mcpRegistry.ListServers();
        if (servers.Count == 0)
        {
            WriteColored("No MCP servers configured. Add servers to .aiagent/mcp.json.\n", Colors.Info);
            return;
        }

        WriteColored($"\nMCP Servers ({servers.Count}):\n", Colors.Info);
        WriteColored($"  {"Name",-20} {"Transport",-10} {"Status",-10} {"Tools"}\n", ConsoleColor.DarkGray);
        WriteColored(new string('-', 60) + "\n", ConsoleColor.DarkGray);

        foreach (var server in servers)
        {
            var status = _mcpRegistry.GetStatus(server.Name);
            var toolCount = _mcpRegistry.GetToolCount(server.Name);
            var transport = server.IsRemote ? "HTTP/SSE" : "stdio";
            WriteColored($"  {server.Name,-20} ", Colors.Tool);
            WriteColored($"{transport,-10} ", ConsoleColor.Gray);
            WriteColored($"{status,-10} ", status == "Connected" ? Colors.Success : ConsoleColor.DarkGray);
            WriteColored($"{toolCount}\n", ConsoleColor.Gray);
        }

        WriteColored("\nMCP tools are automatically merged with built-in tools.\n", Colors.Info);
        await Task.CompletedTask;
    }

    private void PrintSkills()
    {
        if (_skillRegistry == null)
        {
            WriteColored("Skill registry is not available.\n", Colors.Error);
            return;
        }

        var skills = _skillRegistry.ListAsync().GetAwaiter().GetResult();
        if (skills.Count == 0)
        {
            WriteColored("No skills available. Add SKILL.md files to .aiagent/skills/<name>/ to define skills.\n", Colors.Info);
            return;
        }

        WriteColored($"\nAvailable skills ({skills.Count}):\n", Colors.Info);
        WriteColored($"  {"Name",-20} {"Manual",-8} {"Description"}\n", ConsoleColor.DarkGray);
        WriteColored(new string('-', 80) + "\n", ConsoleColor.DarkGray);
        foreach (var skill in skills)
        {
            var desc = skill.Description.Length > 45
                ? skill.Description[..42] + "..."
                : skill.Description;
            WriteColored($"  {skill.Name,-20} ", Colors.Tool);
            WriteColored($"{(skill.DisableModelInvocation ? "yes" : "no"),-8} ", ConsoleColor.Gray);
            WriteColored($"{desc}\n", ConsoleColor.Gray);
        }
        WriteColored("\nUse /skill <name> to load and invoke a skill.\n", Colors.Info);
    }

    private async Task ActivateSkillAsync(string skillName)
    {
        if (_skillRegistry == null)
        {
            WriteColored("Skill registry is not available.\n", Colors.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(skillName))
        {
            WriteColored("Usage: /skill <name>\n", Colors.Error);
            return;
        }

        var invocation = await _skillRegistry.InvokeAsync(skillName);
        if (invocation == null)
        {
            WriteColored($"Skill '{skillName}' not found. Use /skills to list available skills.\n", Colors.Error);
            return;
        }

        WriteColored($"Skill '{invocation.Name}' loaded.\n", Colors.Success);
        var path = _skillRegistry.GetSkillPath(skillName);
        if (!string.IsNullOrEmpty(path))
            WriteColored($"  File: {path}\n", ConsoleColor.DarkGray);
        WriteColored($"  Content length: {invocation.Content.Length} chars\n", ConsoleColor.Gray);
        WriteColored("The skill content is now available for injection into the agent context.\n", Colors.Info);
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  AI Code Agent v1.0
  Your intelligent coding assistant");
        Console.ResetColor();
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Commands: /help /clear /reset /branch /tools /skills /skill <name> /hooks /mcp /model <name> /models /cd <dir> /tasks /task <id> /init /doctor /memory /automemory /automemory edit /exit");
        Console.WriteLine("  Files:   /view path (print file with line numbers)  /edit path (open in external editor)");
        Console.WriteLine("  Ctrl+C to cancel current operation");
        Console.ResetColor();
        Console.WriteLine();
    }

    private void PrintHooks()
    {
        if (_hookRunner == null)
        {
            WriteColored("Hook runner is not available.\n", Colors.Error);
            return;
        }

        var hooks = _hookRunner.ListHooks();
        if (hooks.Count == 0)
        {
            WriteColored("No hooks configured. Add hooks to .aiagent/settings.json under \"hooks\".\n", Colors.Info);
            return;
        }

        WriteColored($"\nConfigured Hooks ({hooks.Count}):\n", Colors.Info);
        WriteColored($"  {"Name",-20} {"Event",-15} {"Blocking",-10} {"Timeout",-8} Filter\n", ConsoleColor.DarkGray);
        WriteColored(new string('-', 90) + "\n", ConsoleColor.DarkGray);
        foreach (var hook in hooks)
        {
            WriteColored($"  {hook.Name,-20} ", Colors.Tool);
            WriteColored($"{hook.Event,-15} ", ConsoleColor.Gray);
            WriteColored($"{(hook.Blocking ? "yes" : "no"),-10} ", ConsoleColor.Gray);
            WriteColored($"{hook.TimeoutSeconds}s".PadRight(9), ConsoleColor.Gray);
            WriteColored($"{hook.ToolFilter ?? "-"}\n", ConsoleColor.DarkGray);
        }
        WriteColored("\nHooks are loaded from ~/.aiagent/settings.json and .aiagent/settings.json.\n", Colors.Info);
    }

    /// <summary>Displays the currently selected model (or alias) for the active provider.</summary>
    private async Task ShowCurrentModelAsync()
    {
        var current = _options.Model ?? _configurationService?.Config.Ui?.SelectedModel ?? "(default)";
        WriteColored($"\nCurrent model: {current}\n", Colors.Info);

        if (_modelRegistry != null && _configurationService != null)
        {
            var provider = _configurationService.Config.DefaultProvider;
            var resolved = _modelRegistry.Resolve(current, provider);
            if (resolved != null && !string.Equals(resolved.Id, current, StringComparison.OrdinalIgnoreCase))
            {
                WriteColored($"  Resolved alias '{current}' -> '{resolved.Id}' (provider: {resolved.Provider})\n", ConsoleColor.DarkGray);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Sets the active model (alias or concrete id) and persists the selection.</summary>
    private async Task SetModelAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            WriteColored("Usage: /model <name|alias>  (e.g. /model Auto, /model gpt-4o)\n", Colors.Error);
            return;
        }

        string resolvedId = model;
        if (_modelRegistry != null && _configurationService != null)
        {
            var provider = _configurationService.Config.DefaultProvider;
            var resolved = _modelRegistry.Resolve(model, provider);
            if (resolved != null)
            {
                resolvedId = resolved.Id;
                WriteColored($"Resolved '{model}' -> '{resolvedId}' (provider: {resolved.Provider}, " +
                             $"context: {resolved.ContextWindow:N0}, max output: {resolved.MaxOutputTokens:N0})\n",
                             ConsoleColor.DarkGray);
            }
            else
            {
                WriteColored($"Note: '{model}' is not in the model registry; passing through to provider as-is.\n",
                             ConsoleColor.DarkGray);
            }
        }

        _options = _options with { Model = resolvedId };

        if (_configurationService != null)
        {
            try
            {
                _configurationService.Config.Ui ??= new UiConfiguration();
                _configurationService.Config.Ui.SelectedModel = model;
                await _configurationService.SaveAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to persist SelectedModel");
            }
        }

        WriteColored($"Model set to: {resolvedId}\n", Colors.Success);
    }

    /// <summary>Lists all models known to the registry, grouped by alias and provider.</summary>
    private async Task ListModelsAsync()
    {
        if (_modelRegistry == null)
        {
            WriteColored("Model registry is not available.\n", Colors.Error);
            return;
        }

        var provider = _configurationService?.Config.DefaultProvider;
        var current = _options.Model ?? _configurationService?.Config.Ui?.SelectedModel;

        WriteColored($"\nAvailable models{(provider != null ? $" (provider: {provider})" : "")}:\n", Colors.Info);
        WriteColored($"  {"Alias",-8} {"Id",-36} {"Provider",-12} {"Ctx",-10} {"Out",-8}\n", ConsoleColor.DarkGray);
        WriteColored(new string('-', 78) + "\n", ConsoleColor.DarkGray);

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in _modelRegistry.ListAliases())
        {
            foreach (var m in _modelRegistry.ListForAlias(alias))
            {
                if (shown.Add(m.Id))
                {
                    var marker = string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                    WriteColored($"{marker} ", Colors.Success);
                    WriteColored($"{m.Alias,-7} ", Colors.Tool);
                    WriteColored($"{m.Id,-36} ", ConsoleColor.Gray);
                    WriteColored($"{m.Provider,-12} ", ConsoleColor.Gray);
                    WriteColored($"{m.ContextWindow,8:N0} ", ConsoleColor.DarkGray);
                    WriteColored($"{m.MaxOutputTokens,6:N0}\n", ConsoleColor.DarkGray);
                }
            }
        }

        if (provider != null)
        {
            foreach (var m in _modelRegistry.ListForProvider(provider))
            {
                if (shown.Add(m.Id))
                {
                    var marker = string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                    WriteColored($"{marker} ", Colors.Success);
                    WriteColored($"{(m.Alias ?? "-"),-7} ", Colors.Tool);
                    WriteColored($"{m.Id,-36} ", ConsoleColor.Gray);
                    WriteColored($"{m.Provider,-12} ", ConsoleColor.Gray);
                    WriteColored($"{m.ContextWindow,8:N0} ", ConsoleColor.DarkGray);
                    WriteColored($"{m.MaxOutputTokens,6:N0}\n", ConsoleColor.DarkGray);
                }
            }
        }

        WriteColored("\nUse /model <alias|id> to switch. Aliases: Auto, Fast, Smart.\n", Colors.Info);
        await Task.CompletedTask;
    }

    private static void PrintPrompt(string workDir)
    {
        var dir = Path.GetFileName(workDir);
        if (string.IsNullOrEmpty(dir)) dir = workDir;

        Console.ForegroundColor = Colors.Prompt;
        Console.Write($"\n[{dir}] ");
        Console.ForegroundColor = Colors.User;
        Console.Write("You: ");
        Console.ResetColor();
    }

    private string? ReadLineWithHistory(List<string> history, ref int historyIndex)
    {
        // When stdin is redirected (piped/scripted, no real console), fall back
        // to a plain ReadLine so the app doesn't crash on a missing console handle.
        // ReadLine returns null at EOF; the caller treats that as "exit".
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        var pos = 0;
        var escPressed = false;
        var escTimer = Stopwatch.StartNew();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Escape:
                    // Esc twice within 500ms → rewind sentinel. Esc once clears the buffer.
                    if (escPressed && escTimer.ElapsedMilliseconds < 500 && buffer.Length == 0)
                    {
                        Console.WriteLine();
                        return EscTwiceSentinel;
                    }

                    escPressed = true;
                    escTimer.Restart();
                    buffer.Clear();
                    pos = 0;
                    RedrawLine(buffer.ToString(), pos);
                    break;

                case ConsoleKey.Backspace when pos > 0:
                    buffer.Remove(--pos, 1);
                    RedrawLine(buffer.ToString(), pos);
                    break;

                case ConsoleKey.LeftArrow when pos > 0:
                    if (Console.CursorLeft > 0)
                        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                    pos--;
                    break;

                case ConsoleKey.RightArrow when pos < buffer.Length:
                    Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                    pos++;
                    break;

                case ConsoleKey.Tab:
                    // Shift+Tab cycles permission modes (Feature 10).
                    if ((key.Modifiers & ConsoleModifiers.Shift) != 0 && _permissionManager != null)
                    {
                        var newMode = _permissionManager.CycleModeAsync().GetAwaiter().GetResult();
                        _options = _options with { PermissionMode = newMode };
                        Console.Write("\r\u001b[2K");
                        WriteColored($"Mode: {newMode}\n", Colors.Success);
                        PrintPrompt(_options.WorkingDirectory);
                        RedrawLine(buffer.ToString(), pos);
                    }
                    break;

                case ConsoleKey.UpArrow when history.Count > 0:
                    historyIndex = Math.Max(0, historyIndex - 1);
                    buffer.Clear();
                    buffer.Append(history[historyIndex]);
                    pos = buffer.Length;
                    RedrawLine(buffer.ToString(), pos);
                    break;

                case ConsoleKey.DownArrow:
                    historyIndex = Math.Min(history.Count, historyIndex + 1);
                    var histEntry = historyIndex < history.Count ? history[historyIndex] : "";
                    buffer.Clear();
                    buffer.Append(histEntry);
                    pos = buffer.Length;
                    RedrawLine(buffer.ToString(), pos);
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(pos++, key.KeyChar);
                        if (key.KeyChar != '\0')
                            Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
    }

    private static void RedrawLine(string text, int cursorPos)
    {
        int left = Console.CursorLeft;
        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
        Console.Write(text);
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private async Task ListTasksAsync()
    {
        try
        {
            var tasks = await _taskHistory.ListAsync();
            if (tasks.Count == 0)
            {
                WriteColored("No saved tasks found.\n", Colors.Info);
                return;
            }

            WriteColored($"\nSaved tasks ({tasks.Count}):\n", Colors.Info);
            WriteColored($"  {"ID",-12} {"Title",-40} {"Msgs",-5} {"Updated"}\n", ConsoleColor.DarkGray);
            WriteColored(new string('-', 75) + "\n", ConsoleColor.DarkGray);
            foreach (var t in tasks.Take(20))
            {
                var title = t.Title.Length > 38 ? t.Title[..35] + "..." : t.Title;
                WriteColored($"  {t.Id,-12} ", Colors.Tool);
                WriteColored($"{title,-40} ", Colors.Assistant);
                WriteColored($"{t.Messages.Count,-5} ", ConsoleColor.Gray);
                WriteColored($"{t.UpdatedAt:yyyy-MM-dd HH:mm}\n", ConsoleColor.DarkGray);
            }
            WriteColored("\nUse /task <id> to view a task's dialog\n", Colors.Info);
        }
        catch (Exception ex)
        {
            WriteColored($"Error listing tasks: {ex.Message}\n", Colors.Error);
        }
    }

    private async Task ShowTaskAsync(string taskId)
    {
        try
        {
            var entry = await _taskHistory.LoadAsync(taskId);
            if (entry == null)
            {
                WriteColored($"Task '{taskId}' not found.\n", Colors.Error);
                return;
            }

            WriteColored($"\nTask: {entry.Title}\n", Colors.Info);
            WriteColored($"ID:      {entry.Id}\n", ConsoleColor.DarkGray);
            WriteColored($"Created: {entry.CreatedAt:yyyy-MM-dd HH:mm}\n", ConsoleColor.DarkGray);
            WriteColored($"Updated: {entry.UpdatedAt:yyyy-MM-dd HH:mm}\n", ConsoleColor.DarkGray);
            WriteColored($"Messages: {entry.Messages.Count}, Tool calls: {entry.ToolCalls.Count}\n\n", ConsoleColor.DarkGray);

            foreach (var msg in entry.Messages)
            {
                var (label, color) = msg.Role switch
                {
                    "User" => ("[USER]", Colors.User),
                    "Assistant" => ("[ASSISTANT]", Colors.Assistant),
                    "System" => ("[SYSTEM]", ConsoleColor.DarkGray),
                    _ => ($"[{msg.Role}]", ConsoleColor.Gray)
                };
                WriteColored($"{label} ({msg.Timestamp:HH:mm:ss}): ", color);
                WriteColored($"{msg.Content}\n\n", ConsoleColor.Gray);
            }

            if (entry.ToolCalls.Count > 0)
            {
                WriteColored($"Tool Calls ({entry.ToolCalls.Count}):\n", Colors.Tool);
                foreach (var tc in entry.ToolCalls)
                {
                    var icon = tc.IsError ? "X" : "OK";
                    WriteColored($"  [{icon}] {tc.ToolName} ({tc.Timestamp:HH:mm:ss})\n", tc.IsError ? Colors.Error : Colors.ToolResult);
                }
            }
        }
        catch (Exception ex)
        {
            WriteColored($"Error showing task: {ex.Message}\n", Colors.Error);
        }
    }
}