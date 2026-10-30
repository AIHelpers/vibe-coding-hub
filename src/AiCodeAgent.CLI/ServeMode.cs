using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>aiagent serve</c> (E7-S3 prototype): runs one flow here and streams its events over a WebSocket so the desktop app
/// (or any client) can watch it, answer approvals and stop it. Exit codes follow <see cref="FlowExitCodes"/>.
/// </summary>
internal static class ServeMode
{
    public static void AddCommands(RootCommand root, Func<string?, string?, string?, Task<IServiceProvider>> buildServices)
    {
        var serve = new Command("serve", "Run a flow and stream its events over a WebSocket so the desktop app can attach (prototype)");
        var task = new Argument<string>("task", () => string.Empty, "The task for the flow (or use --task-file)");
        var taskFile = new Option<string?>("--task-file", "Read the task from this file");
        var pipeline = new Option<string>("--pipeline", () => "full-sdlc", "Flow to run (see 'pipeline list')");
        var host = new Option<string>("--host", () => "127.0.0.1", "Address to listen on. Loopback by default; anything else exposes the run to the network (put TLS in front)");
        var port = new Option<int>("--port", () => 0, "Port (0 = pick a free one)");
        var token = new Option<string?>("--token", "Bearer token clients must send (default: AIAGENT_SERVE_TOKEN, else a random one is printed)");
        var permission = new Option<string?>("--permission", "Permission mode for every step: Plan, Ask, AutoEdit or FullAuto. Questions go to the attached clients");
        var cast = new Option<string?>("--cast", "role=character[,role=character...]");
        var maxBudget = new Option<decimal?>("--max-budget", "Spend cap in USD");
        var maxParallel = new Option<int?>("--max-parallel", "Most steps at the same time");
        var wait = new Option<int>("--wait-for-client", () => 0, "Seconds to wait for the first client before the flow starts (0 = start at once; late clients get the whole history)");
        var linger = new Option<int>("--linger", () => 10, "Seconds to keep serving after the flow finished so clients can read the end");
        var approvalTimeout = new Option<int>("--approval-timeout", () => 600, "Seconds a question waits for a client before it is denied (0 = forever)");
        var provider = new Option<string?>("--provider", "AI provider to use");
        var model = new Option<string?>("--model", "Model to use");
        var dir = new Option<string?>("--dir", "Working directory");
        foreach (var a in new Symbol[] { task, taskFile, pipeline, host, port, token, permission, cast, maxBudget, maxParallel, wait, linger, approvalTimeout, provider, model, dir })
        {
            if (a is Argument arg) serve.AddArgument(arg);
            else serve.AddOption((Option)a);
        }

        serve.SetHandler(async (InvocationContext ctx) =>
        {
            var r = ctx.ParseResult;
            ctx.ExitCode = 0;
            Environment.ExitCode = await RunAsync(new ServeArgs(
                r.GetValueForOption(pipeline)!, r.GetValueForArgument(task), r.GetValueForOption(taskFile),
                r.GetValueForOption(host)!, r.GetValueForOption(port),
                r.GetValueForOption(token) ?? Environment.GetEnvironmentVariable("AIAGENT_SERVE_TOKEN"),
                r.GetValueForOption(permission), r.GetValueForOption(cast), r.GetValueForOption(maxBudget), r.GetValueForOption(maxParallel),
                r.GetValueForOption(wait), r.GetValueForOption(linger), r.GetValueForOption(approvalTimeout),
                r.GetValueForOption(model), r.GetValueForOption(dir)),
                () => buildServices(r.GetValueForOption(provider), r.GetValueForOption(model), r.GetValueForOption(dir)),
                ctx.GetCancellationToken());
        });
        root.AddCommand(serve);
    }

    internal sealed record ServeArgs(
        string Pipeline, string? Task, string? TaskFile, string Host, int Port, string? Token, string? Permission, string? Cast,
        decimal? MaxBudgetUsd, int? MaxParallel, int WaitForClientSeconds, int LingerSeconds, int ApprovalTimeoutSeconds,
        string? Model, string? WorkingDirectory);

    internal static async Task<int> RunAsync(
        ServeArgs args, Func<Task<IServiceProvider>> buildServices, CancellationToken cancellationToken, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        if (!HeadlessPipelineMode.TryResolveTask(args.Task, args.TaskFile, out var task, out var taskError))
        {
            stderr.WriteLine(taskError);
            return FlowExitCodes.Validation;
        }
        PermissionMode? permission = null;
        if (!string.IsNullOrWhiteSpace(args.Permission))
        {
            permission = HeadlessPipelineMode.ParsePermission(args.Permission);
            if (permission == null)
            {
                stderr.WriteLine($"Unknown --permission '{args.Permission}'. Use Plan, Ask, AutoEdit or FullAuto.");
                return FlowExitCodes.Validation;
            }
        }
        Dictionary<string, string> cast;
        try { cast = SdlcPipelineRunner.ParseCast(args.Cast); }
        catch (FormatException ex) { stderr.WriteLine(ex.Message); return FlowExitCodes.Validation; }

        SdlcPipelineRunner runner;
        SdlcPipelineDefinition definition;
        try
        {
            var services = await buildServices();
            runner = services.GetRequiredService<SdlcPipelineRunner>();
            var entry = services.GetRequiredService<SdlcPipelineLoader>().GetEntry(args.Pipeline);
            if (entry?.Pipeline == null)
            {
                stderr.WriteLine($"Unknown pipeline '{args.Pipeline}'. Run 'pipeline list' to see available pipelines.");
                return FlowExitCodes.Validation;
            }
            definition = entry.Pipeline;
        }
        catch (Exception ex)
        {
            stderr.WriteLine("Configuration error: " + ex.GetBaseException().Message);
            return FlowExitCodes.Configuration;
        }
        if (!definition.IsFlow)
        {
            stderr.WriteLine($"'{definition.Name}' is a linear pipeline. Serving needs a flow (a pipeline with edges).");
            return FlowExitCodes.Validation;
        }

        var generated = string.IsNullOrWhiteSpace(args.Token);
        var token = generated ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() : args.Token!;
        var runId = Guid.NewGuid().ToString();
        var hello = new RemoteHello(RemoteProtocol.Version, runId, definition.Name, task, DateTimeOffset.UtcNow, definition);

        RemoteRunServer server;
        try
        {
            server = new RemoteRunServer(hello, token, args.Host, args.Port,
                args.ApprovalTimeoutSeconds > 0 ? TimeSpan.FromSeconds(args.ApprovalTimeoutSeconds) : TimeSpan.Zero);
            server.Start();
        }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            stderr.WriteLine($"Cannot listen on {args.Host}:{args.Port}: {ex.Message}");
            return FlowExitCodes.Configuration;
        }

        await using (server)
        {
            stderr.WriteLine($"Serving flow '{definition.Name}' (run {runId}) on {server.Url}");
            stderr.WriteLine(generated ? $"Token: {token}" : "Token: (from --token / AIAGENT_SERVE_TOKEN)");
            stderr.WriteLine("Attach from the desktop app: Flows → Attach. Anyone with the token can answer approvals and stop the run.");
            if (args.Host is not ("127.0.0.1" or "localhost" or "::1"))
                stderr.WriteLine("Warning: listening beyond loopback without TLS. Use an SSH tunnel, Tailscale or a TLS proxy.");

            if (args.WaitForClientSeconds > 0)
            {
                stderr.WriteLine($"Waiting up to {args.WaitForClientSeconds}s for a client...");
                await server.WaitForClientAsync(TimeSpan.FromSeconds(args.WaitForClientSeconds), cancellationToken);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, server.RunCancelRequested);
            FlowRunResult? result = null;
            try
            {
                await foreach (var evt in runner.RunAsync(definition, task, runId, args.WorkingDirectory ?? Directory.GetCurrentDirectory(), args.Model,
                                   cancellationToken: linked.Token, cast: cast, maxParallel: args.MaxParallel,
                                   maxBudgetUsd: args.MaxBudgetUsd, permissionMode: permission).ConfigureAwait(false))
                {
                    server.Publish(evt);
                    if (evt is FlowFinishedEvent finished) result = finished.Result;
                    else if (evt is FlowNodeStatusEvent { State: FlowNodeState.Running } n) stderr.WriteLine($"▶ {n.NodeId}");
                    else if (evt is FlowNodeStatusEvent { State: FlowNodeState.Done } d) stderr.WriteLine($"✓ {d.NodeId}");
                }
            }
            catch (OperationCanceledException) { }

            await server.FinishAsync(TimeSpan.FromSeconds(Math.Max(0, args.LingerSeconds)));
            if (result == null)
            {
                stderr.WriteLine(server.RunCancelRequested.IsCancellationRequested ? "The run was stopped by a client." : "The flow ended without a result.");
                return FlowExitCodes.Failed;
            }
            stderr.WriteLine($"Flow finished: {result.Message}");
            return FlowExitCodes.FromStatus(result.Status);
        }
    }
}
