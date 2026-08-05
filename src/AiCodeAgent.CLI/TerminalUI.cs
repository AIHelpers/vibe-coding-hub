using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
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
    private string _sessionId = Guid.NewGuid().ToString();

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
        SessionImporter importer)
    {
        _orchestrator = orchestrator;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _recorder = recorder;
        _exportService = exportService;
        _importer = importer;
    }

    public async Task RunAsync(AgentOptions options)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Clear();

        PrintBanner();
        PrintHelp();

        _recorder.Start(_sessionId);

        var history = new List<string>();
        var historyIndex = 0;

        while (true)
        {
            PrintPrompt(options.WorkingDirectory);

            var input = ReadLineWithHistory(history, ref historyIndex);
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (history.Count == 0 || history[^1] != input)
                history.Add(input);
            historyIndex = history.Count;

            if (await HandleCommandAsync(input, options)) continue;

            Console.WriteLine();
            await StreamResponseAsync(input, options);
            Console.WriteLine();
        }
    }

    private async Task StreamResponseAsync(string userMessage, AgentOptions options)
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

        try
        {
            await foreach (var evt in _orchestrator.StreamRunAsync(
                userMessage, _sessionId, options, cts.Token))
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
                        break;

                    case AgentFinishedEvent finished:
                        Console.WriteLine();
                        WriteStats(finished.Response);
                        break;

                    case AgentErrorEvent error:
                        Console.WriteLine();
                        WriteColored($"\nError: {error.Error.Message}\n", Colors.Error);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            WriteColored("\nCancelled\n", Colors.Info);
        }
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

    private async Task<bool> HandleCommandAsync(string input, AgentOptions options)
    {
        var trimmed = input.Trim();

        switch (trimmed.ToLower())
        {
            case "/help" or "/h":
                PrintHelp();
                return true;

            case "/clear" or "/c":
                Console.Clear();
                PrintBanner();
                return true;

            case "/reset":
                _recorder.Stop();
                _sessionId = Guid.NewGuid().ToString();
                _recorder.Start(_sessionId);
                WriteColored("Session reset\n", Colors.Success);
                return true;

            case var s when s.StartsWith("/export "):
                await ExportSessionAsync(s[8..].Trim());
                return true;

            case var s when s.StartsWith("/import "):
                await ImportSessionAsync(s[8..].Trim());
                return true;

            case "/tools":
                PrintTools();
                return true;

            case "/exit" or "/quit" or "exit" or "quit":
                WriteColored("Goodbye!\n", Colors.Info);
                Environment.Exit(0);
                return true;

            case var s when s.StartsWith("/model "):
                var model = s[7..].Trim();
                WriteColored($"Model set to: {model}\n", Colors.Success);
                return true;

            case var s when s.StartsWith("/cd "):
                var newDir = s[4..].Trim();
                if (Directory.Exists(newDir))
                {
                    Directory.SetCurrentDirectory(newDir);
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
        Console.WriteLine("  Commands: /help /clear /reset /tools /model <name> /cd <dir> /exit");
        Console.WriteLine("  Ctrl+C to cancel current operation");
        Console.ResetColor();
        Console.WriteLine();
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

    private static string ReadLineWithHistory(List<string> history, ref int historyIndex)
    {
        var buffer = new StringBuilder();
        var pos = 0;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

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
}