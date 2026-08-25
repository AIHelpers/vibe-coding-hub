using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Default <see cref="ISubagentRunner"/> implementation. Spawns subagents that
/// run through <see cref="IAgentOrchestrator"/> with their own session id and
/// context window, so their tool calls never pollute the parent conversation.
/// Forking copies the parent's messages into the subagent's session before run.
/// </summary>
public class SubagentRunner : ISubagentRunner
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IContextManager _contextManager;
    private readonly IAgentEventBus? _eventBus;

    public SubagentRunner(
        IAgentOrchestrator orchestrator,
        IContextManager contextManager,
        IAgentEventBus? eventBus = null)
    {
        _orchestrator = orchestrator;
        _contextManager = contextManager;
        _eventBus = eventBus;
    }

    public async Task<SubagentSummary> RunAsync(
        string task,
        SubagentContext context,
        CancellationToken cancellationToken = default)
    {
        var subagentId = NewId();
        _eventBus?.Publish(new SubagentStartedEvent(subagentId, task, Forked: false));
        return await ExecuteAsync(subagentId, task, context, forked: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SubagentSummary> ForkAsync(
        string task,
        SubagentContext context,
        CancellationToken cancellationToken = default)
    {
        var subagentId = NewId();
        _eventBus?.Publish(new SubagentStartedEvent(subagentId, task, Forked: true));

        // Copy parent conversation into the subagent's session so it has context.
        try
        {
            if (!string.IsNullOrEmpty(context.ParentSessionId))
            {
                var parentMessages = await _contextManager
                    .GetContextAsync(context.ParentSessionId)
                    .ConfigureAwait(false);

                foreach (var msg in parentMessages)
                {
                    await _contextManager
                        .AddMessageAsync(subagentId, msg)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Forking is best-effort; if it fails, fall back to fresh context.
        }

        return await ExecuteAsync(subagentId, task, context, forked: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SubagentSummary> ExecuteAsync(
        string subagentId,
        string task,
        SubagentContext context,
        bool forked,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var options = BuildOptions(subagentId, context);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(context.Timeout);

        try
        {
            var response = await _orchestrator
                .RunAsync(task, subagentId, options, cts.Token)
                .ConfigureAwait(false);

            sw.Stop();
            var summary = new SubagentSummary
            {
                SubagentId = subagentId,
                Task = task,
                Forked = forked,
                Content = response.Content,
                ToolCallCount = response.ToolExecutions.Count,
                Usage = response.TotalUsage,
                Duration = sw.Elapsed,
                WasCancelled = response.WasCancelled,
            };

            _eventBus?.Publish(new SubagentFinishedEvent(summary));
            return summary;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            var summary = new SubagentSummary
            {
                SubagentId = subagentId,
                Task = task,
                Forked = forked,
                Content = "Subagent timed out.",
                Duration = sw.Elapsed,
                WasCancelled = true,
                Error = "timeout",
            };
            _eventBus?.Publish(new SubagentFinishedEvent(summary));
            return summary;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var summary = new SubagentSummary
            {
                SubagentId = subagentId,
                Task = task,
                Forked = forked,
                Content = "Subagent cancelled.",
                Duration = sw.Elapsed,
                WasCancelled = true,
            };
            _eventBus?.Publish(new SubagentFinishedEvent(summary));
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var summary = new SubagentSummary
            {
                SubagentId = subagentId,
                Task = task,
                Forked = forked,
                Content = $"Subagent error: {ex.Message}",
                Duration = sw.Elapsed,
                Error = ex.GetType().Name,
            };
            _eventBus?.Publish(new SubagentFinishedEvent(summary));
            return summary;
        }
        finally
        {
            // Best-effort cleanup of the subagent's isolated context window.
            try { await _contextManager.ClearAsync(subagentId).ConfigureAwait(false); }
            catch { /* ignored */ }
        }
    }

    private static AgentOptions BuildOptions(string subagentId, SubagentContext context)
    {
        return new AgentOptions
        {
            Model = context.Model,
            WorkingDirectory = context.WorkingDirectory,
            MaxIterations = context.MaxIterations,
            MaxTokens = context.MaxTokens,
            AutoApprove = true, // subagents run autonomously; permission inheritance handled at tool layer
            EnabledTools = context.EnabledTools ?? new(),
            SessionId = subagentId,
            AgentId = subagentId,
            Role = context.Role,
        };
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return "sub_" + Convert.ToHexString(bytes).ToLower();
    }
}

/// <summary>Emitted when a subagent session starts.</summary>
public record SubagentStartedEvent(string SubagentId, string Task, bool Forked) : AgentEvent;

/// <summary>Emitted when a subagent session finishes (success, failure, or cancellation).</summary>
public record SubagentFinishedEvent(SubagentSummary Summary) : AgentEvent;