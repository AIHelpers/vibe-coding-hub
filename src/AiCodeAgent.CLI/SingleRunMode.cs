using System.Text;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.CLI;

public class SingleRunMode
{
    private readonly IAgentOrchestrator _orchestrator;

    public SingleRunMode(IAgentOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task RunAsync(string prompt, AgentOptions options)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();

        await foreach (var evt in _orchestrator.StreamRunAsync(prompt, sessionId, options))
        {
            switch (evt)
            {
                case TextDeltaEvent delta:
                    Console.Write(delta.Delta);
                    break;
                case ToolCallStartEvent toolStart:
                    Console.Error.WriteLine($"\n[TOOL] {toolStart.Call.Name}");
                    break;
                case ToolCallEndEvent toolEnd:
                    var icon = toolEnd.Result.IsError ? "ERROR" : "OK";
                    Console.Error.WriteLine($"[{icon}] {toolEnd.Call.Name} ({toolEnd.Duration.TotalMilliseconds:F0}ms)");
                    break;
                case AgentFinishedEvent:
                    Console.WriteLine();
                    break;
                case AgentErrorEvent error:
                    Console.Error.WriteLine($"Error: {error.Error.Message}");
                    Environment.Exit(1);
                    break;
            }
        }
    }
}