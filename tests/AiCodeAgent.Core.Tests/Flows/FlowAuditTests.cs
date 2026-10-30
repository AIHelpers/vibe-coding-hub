using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowCostAggregationTests;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E7-S2: every saved flow run carries a hash-chained <c>audit.jsonl</c> next to its <c>run.json</c>.</summary>
public sealed class FlowAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flowaudit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SdlcPipelineDefinition Diamond() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("backend"), Stage("model"), Stage("review", "reviewer") },
        Edges = new() { Edge("design", "backend"), Edge("design", "model"), Edge("backend", "review"), Edge("model", "review") }
    };

    private async Task<(FlowRunResult Result, string AuditFile, FlowRunStore Store)> RunAsync()
    {
        var store = new FlowRunStore(_root);
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, new UsageOrchestrator(), NullLogger<SdlcPipelineRunner>.Instance,
            null, null, null, store);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(Diamond(), "ship it", "s1", Path.GetTempPath(), model: Sonnet))
            events.Add(e);
        var result = Assert.Single(events.OfType<FlowFinishedEvent>()).Result;
        return (result, Path.Combine(store.RunDirectory("diamond", result.RunId!), AuditChain.FileName), store);
    }

    [Fact]
    public async Task ASavedRun_HasAnAuditFile_ThatVerifies()
    {
        var (result, file, _) = await RunAsync();

        Assert.True(File.Exists(file));
        var verification = AuditChainVerifier.Verify(file);
        Assert.True(verification.Ok, verification.Failure?.Problem);
        Assert.True(verification.HasEndRecord);
        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheAuditFile_TellsTheStoryOfTheRun()
    {
        var (result, file, _) = await RunAsync();

        var entries = AuditExporter.Read(file);
        var start = entries.First();
        Assert.Equal("run.start", start.Kind);
        Assert.Equal("flow", start.Str("runKind"));
        Assert.Equal("diamond", start.Str("name"));
        Assert.Equal("ship it", start.Str("task"));
        Assert.Equal(result.RunId, start.Str("runId"));

        var started = entries.Where(e => e.Kind == "step.start").Select(e => e.Agent).ToList();
        var finished = entries.Where(e => e.Kind == "step.end").Select(e => e.Str("step")).ToList();
        Assert.Equal(new[] { "backend", "design", "model", "review" }, started.OrderBy(x => x));
        Assert.Equal(new[] { "backend", "design", "model", "review" }, finished.OrderBy(x => x));
        Assert.All(entries.Where(e => e.Kind == "step.start"), e => Assert.Equal(Sonnet, e.Entry["model"]!.GetValue<string>()));
        Assert.NotEmpty(entries.Where(e => e.Kind == "flow.edge"));

        var end = entries.Last();
        Assert.Equal("run.end", end.Kind);
        Assert.Equal("succeeded", end.Str("status"));
        Assert.Equal(result.Usage.Tokens.ToString(), end.Str("tokens"));
        Assert.True(decimal.Parse(end.Str("costUsd")!, System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal(result.Usage.CostUsd, decimal.Parse(end.Str("costUsd")!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TheRunRecord_KeepsTheHeadHash_SoATrimmedLogCanBeSpotted()
    {
        var (result, file, store) = await RunAsync();

        var record = store.Load("diamond", result.RunId!)!;

        var verification = AuditChainVerifier.Verify(file);
        Assert.Equal(verification.HeadHash, record.AuditHead);
        Assert.Equal(verification.Lines, record.AuditEntries);

        // Cut the last line off: the chain still holds, but the head hash no longer matches the record.
        File.WriteAllLines(file, File.ReadAllLines(file).SkipLast(1));
        var trimmed = AuditChainVerifier.Verify(file);
        Assert.True(trimmed.Ok);
        Assert.NotEqual(record.AuditHead, trimmed.HeadHash);
    }

    [Fact]
    public async Task EditingTheAuditFileOfARun_IsDetected()
    {
        var (_, file, _) = await RunAsync();
        var lines = File.ReadAllLines(file);
        var target = Array.FindIndex(lines, l => l.Contains("\"kind\":\"step.end\"") && l.Contains("\"step\":\"design\""));
        Assert.True(target > 0);
        lines[target] = lines[target].Replace("\"state\":\"done\"", "\"state\":\"failed\"");
        File.WriteAllLines(file, lines);

        var result = AuditChainVerifier.Verify(file);

        Assert.False(result.Ok);
        Assert.Equal(target + 1, result.Failure!.Line);
    }

    [Fact]
    public async Task NoAuditFileIsWritten_WhenTheRunIsNotRecorded()
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, new UsageOrchestrator(), NullLogger<SdlcPipelineRunner>.Instance);

        await foreach (var _ in runner.RunAsync(Diamond(), "ship it", "s1", Path.GetTempPath(), model: Sonnet)) { }

        Assert.False(Directory.Exists(_root));
    }
}
