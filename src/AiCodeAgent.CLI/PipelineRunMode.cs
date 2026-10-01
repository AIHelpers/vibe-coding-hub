using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.CLI;

/// <summary>
/// Runs an <see cref="SdlcPipelineDefinition"/> end-to-end from the CLI, printing a header whenever
/// the active stage changes and streaming each stage's text/tool activity, mirroring SingleRunMode
/// but for a multi-stage, multi-role session.
/// </summary>
public class PipelineRunMode
{
    private readonly SdlcPipelineRunner _runner;

    public PipelineRunMode(SdlcPipelineRunner runner) => _runner = runner;

    public async Task RunAsync(SdlcPipelineDefinition pipeline, string task, string workingDirectory, string? model)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();

        Console.WriteLine($"Running pipeline '{pipeline.Name}': {pipeline.Description}");
        Console.WriteLine($"Task: {task}");
        Console.WriteLine();

        string? currentRole = null;

        await foreach (var evt in _runner.RunAsync(pipeline, task, sessionId, workingDirectory, model))
        {
            if (evt is not AgentTaggedEvent tagged)
                continue;

            if (tagged.Role != currentRole)
            {
                currentRole = tagged.Role;
                Console.WriteLine();
                Console.WriteLine($"===== Stage: {tagged.Role} (agent: {tagged.AgentId}) =====");
            }

            switch (tagged.Inner)
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

                case ApprovalRequestEvent approval:
                    Console.Error.Write($"\nApprove {approval.Call.Name} for stage '{tagged.Role}'? [y/N] ");
                    var key = Console.ReadKey(intercept: false);
                    Console.Error.WriteLine();
                    approval.Approval.TrySetResult(key.Key == ConsoleKey.Y);
                    break;

                case AgentFinishedEvent finished:
                    Console.WriteLine();
                    if (finished.Response.WasCancelled)
                        Console.WriteLine($"[{tagged.Role}] cancelled");
                    break;

                case AgentErrorEvent error:
                    Console.Error.WriteLine($"[{tagged.Role}] Error: {error.Error.Message}");
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Pipeline complete.");
    }
}
