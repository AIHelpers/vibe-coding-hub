using System.Text;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;

namespace AiCodeAgent.CLI;

public class SingleRunMode
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly TaskHistoryStore _taskHistory;

    public SingleRunMode(IAgentOrchestrator orchestrator, TaskHistoryStore taskHistory)
    {
        _orchestrator = orchestrator;
        _taskHistory = taskHistory;
    }

    public async Task RunAsync(string prompt, AgentOptions options)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();
        var title = prompt.Length > 60 ? prompt[..60] : prompt;

        // Save user message to task history
        await _taskHistory.AddMessageAsync(sessionId, title, new TaskMessageRecord
        {
            Role = "User",
            Content = prompt,
            Timestamp = DateTime.UtcNow
        });

        var assistantText = new StringBuilder();

        await foreach (var evt in _orchestrator.StreamRunAsync(prompt, sessionId, options))
        {
            switch (evt)
            {
                case TextDeltaEvent delta:
                    Console.Write(delta.Delta);
                    assistantText.Append(delta.Delta);
                    break;

                case ToolCallStartEvent toolStart:
                    Console.Error.WriteLine($"\n[TOOL] {toolStart.Call.Name}");
                    break;

                case ToolCallEndEvent toolEnd:
                    var icon = toolEnd.Result.IsError ? "ERROR" : "OK";
                    Console.Error.WriteLine($"[{icon}] {toolEnd.Call.Name} ({toolEnd.Duration.TotalMilliseconds:F0}ms)");

                    // Save tool call to task history
                    await _taskHistory.AddToolCallAsync(sessionId, new TaskToolCallRecord
                    {
                        ToolName = toolEnd.Call.Name,
                        Arguments = toolEnd.Call.Arguments?.ToString(),
                        Output = toolEnd.Result.Content,
                        IsError = toolEnd.Result.IsError,
                        Timestamp = DateTime.UtcNow
                    });
                    break;

                case AgentFinishedEvent:
                    Console.WriteLine();

                    // Save assistant response to task history
                    if (assistantText.Length > 0)
                    {
                        await _taskHistory.AddMessageAsync(sessionId, title, new TaskMessageRecord
                        {
                            Role = "Assistant",
                            Content = assistantText.ToString(),
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;

                case AgentErrorEvent error:
                    Console.Error.WriteLine($"Error: {error.Error.Message}");
                    Environment.Exit(1);
                    break;

                case ApprovalRequestEvent approval:
                    Console.Error.Write($"\nApprove {approval.Call.Name}? [y/N] ");
                    var key = Console.ReadKey(intercept: false);
                    Console.Error.WriteLine();
                    approval.Approval.TrySetResult(key.Key == ConsoleKey.Y);
                    break;
            }
        }
    }
}