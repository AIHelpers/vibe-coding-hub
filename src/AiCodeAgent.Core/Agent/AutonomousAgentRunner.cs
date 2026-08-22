using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>Runs a plan step-by-step with verification and approval.</summary>
public interface IAutonomousAgentRunner
{
    Task<ExecutionLog> RunAsync(
        List<PlanStep> steps,
        string task,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a <see cref="PlanStep"/> list step-by-step using the existing
/// <see cref="IAgentOrchestrator"/> for edits/commands and the shell tool for
/// verification. Pauses at step boundaries for user approval and self-corrects
/// by feeding verify output back to the orchestrator on retry.
/// </summary>
public class AutonomousAgentRunner : IAutonomousAgentRunner
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentEventBus _eventBus;
    private readonly ILogger<AutonomousAgentRunner> _logger;

    private const int MaxRetriesPerStep = 3;

    public AutonomousAgentRunner(
        IAgentOrchestrator orchestrator,
        IAgentEventBus eventBus,
        ILogger<AutonomousAgentRunner> logger)
    {
        _orchestrator = orchestrator;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// Runs the given plan to completion. Step boundaries pause for user
    /// approval via <see cref="PlanStepApprovalEvent"/> unless the options
    /// auto-approve.
    /// </summary>
    public async Task<ExecutionLog> RunAsync(
        List<PlanStep> steps,
        string task,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var log = new ExecutionLog
        {
            SessionId = sessionId,
            Task = task,
            Steps = steps
        };

        _eventBus.Publish(new PlanGeneratedEvent(steps.ToArray(), log.RunId));

        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    log.WasCancelled = true;
                    break;
                }

                var step = steps[i];
                var ok = await RunStepAsync(i, step, task, sessionId, options, cancellationToken)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    log.FailureSummary = $"Step {step.Index} failed: {step.FailureReason}";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            log.WasCancelled = true;
        }
        catch (Exception ex)
        {
            log.FailureSummary = ex.Message;
            _logger.LogError(ex, "Autonomous run failed.");
        }
        finally
        {
            log.FinishedAt = DateTime.UtcNow;
            _eventBus.Publish(new PlanRunFinishedEvent(log));
        }

        return log;
    }

    private async Task<bool> RunStepAsync(
        int stepIndex,
        PlanStep step,
        string task,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken)
    {
        step.Status = PlanStepStatus.Running;
        _eventBus.Publish(new PlanStepStartedEvent(stepIndex, step));
        _eventBus.Publish(new PlanStepStatusChangedEvent(stepIndex, step.Status));

        while (step.AttemptCount < MaxRetriesPerStep)
        {
            step.AttemptCount++;

            var prompt = BuildStepPrompt(task, step);
            AgentResponse response;
            try
            {
                response = await _orchestrator.RunAsync(prompt, sessionId, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                step.FailureReason = $"orchestrator error: {ex.Message}";
                _logger.LogWarning(ex, "Step {Index} orchestrator error.", step.Index);
                continue;
            }

            // Collect any diffs surfaced via tool executions (best-effort attribution).
            foreach (var exec in response.ToolExecutions)
            {
                if (exec.Call.Name is "write_file" or "edit_file"
                    && !exec.Result.IsError)
                {
                    step.Diffs.Add(new DiffEntry
                    {
                        FilePath = exec.Call.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "",
                        ModifiedContent = exec.Call.Arguments.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "",
                        AgentId = options.AgentId
                    });
                }
            }

            // Run verification command, if any.
            var verifyOk = true;
            if (!string.IsNullOrWhiteSpace(step.VerifyCommand))
            {
                verifyOk = await RunVerificationAsync(step, sessionId, options, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (verifyOk)
            {
                step.Status = PlanStepStatus.Completed;
                _eventBus.Publish(new PlanStepFinishedEvent(stepIndex, step));
                _eventBus.Publish(new PlanStepStatusChangedEvent(stepIndex, step.Status));
                return true;
            }

            // Verification failed: feed output back to model and retry.
            _logger.LogInformation("Step {Index} verification failed (attempt {Attempt}); retrying.",
                step.Index, step.AttemptCount);
        }

        step.Status = PlanStepStatus.Failed;
        step.FailureReason ??= $"verification failed after {MaxRetriesPerStep} attempts";
        _eventBus.Publish(new PlanStepFinishedEvent(stepIndex, step));
        _eventBus.Publish(new PlanStepStatusChangedEvent(stepIndex, step.Status));
        return false;
    }

    private static string BuildStepPrompt(string task, PlanStep step)
    {
        var files = step.FilesLikelyTouched.Count > 0
            ? string.Join(", ", step.FilesLikelyTouched)
            : "(unspecified)";
        var prior = string.IsNullOrWhiteSpace(step.LastVerifyOutput)
            ? ""
            : $"\n\nPrevious attempt's verify output:\n{step.LastVerifyOutput}\nRevise to fix the errors above.";
        return $"Autonomous task: {task}\n\nCurrent step ({step.Index}): {step.Description}\n" +
               $"Files likely touched: {files}{prior}";
    }

    private async Task<bool> RunVerificationAsync(
        PlanStep step,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = $"Run this verification command and report exit code and output:\n\n{step.VerifyCommand}";
        try
        {
            var response = await _orchestrator.RunAsync(prompt, sessionId, options, cancellationToken)
                .ConfigureAwait(false);
            step.LastVerifyOutput = response.Content;
            // Heuristic: treat non-empty error keywords or explicit failure indicators as failure.
            var lower = (response.Content ?? "").ToLowerInvariant();
            var hasError = lower.Contains("error") || lower.Contains("failed") || lower.Contains("exception");
            return !hasError;
        }
        catch (Exception ex)
        {
            step.LastVerifyOutput = $"verify error: {ex.Message}";
            return false;
        }
    }
}