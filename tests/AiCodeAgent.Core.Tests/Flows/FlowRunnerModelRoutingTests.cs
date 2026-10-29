using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E5-S3: a flow step can pick its own model (a tier or an id) and provider.</summary>
public class FlowRunnerModelRoutingTests : IDisposable
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private static readonly ModelCatalog Catalog = new(new[]
    {
        new CatalogModel("main-fast", "main", ModelTier.Fast),
        new CatalogModel("main-balanced", "main", ModelTier.Balanced),
        new CatalogModel("main-frontier", "main", ModelTier.Frontier),
        new CatalogModel("other-fast", "other", ModelTier.Fast),
        new CatalogModel("other-frontier", "other", ModelTier.Frontier)
    });

    /// <summary>Fake provider that records which model each request asked for.</summary>
    private sealed class RecordingProvider : IAiProvider
    {
        private readonly ConcurrentQueue<(string Provider, string? Model)> _log;
        public RecordingProvider(string name, ConcurrentQueue<(string Provider, string? Model)> log) { Name = name; _log = log; }
        public string Name { get; }
        public string[] SupportedModels => Array.Empty<string>();
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _log.Enqueue((Name, request.Options?.Model));
            await Task.Yield();
            yield return new StreamChunk { Delta = "ok", IsFinished = true };
        }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<string>());
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        private readonly Dictionary<string, IAiProvider> _providers;
        public FakeResolver(string? defaultName, Dictionary<string, IAiProvider> providers) { DefaultProviderName = defaultName; _providers = providers; }
        public string? DefaultProviderName { get; }
        public IAiProvider? Resolve(string providerName) => _providers.TryGetValue(providerName, out var p) ? p : null;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-routing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private readonly ConcurrentQueue<(string Provider, string? Model)> _calls = new();

    /// <summary>A real orchestrator on the "main" provider; "other" is reachable through the resolver only.</summary>
    private SdlcPipelineRunner Build(ICharacterRegistry? characters = null)
    {
        var main = new RecordingProvider("main", _calls);
        var other = new RecordingProvider("other", _calls);
        var resolver = new FakeResolver("main", new() { ["main"] = main, ["other"] = other });
        var context = Substitute.For<IContextManager>();
        context.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());
        var orchestrator = new AgentOrchestrator(
            main, context, Substitute.For<IToolRegistry>(), new AgentConfiguration(),
            Substitute.For<ILogger<AgentOrchestrator>>(), Substitute.For<IPermissionService>(), Substitute.For<ICheckpointManager>(),
            providerResolver: resolver);
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets, characters: characters);
        return new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, characters, resolver, Catalog);
    }

    private static async Task<FlowRunResult> RunAsync(SdlcPipelineRunner runner, SdlcPipelineDefinition flow, string? model = null)
    {
        FlowRunResult? result = null;
        await foreach (var e in runner.RunAsync(flow, "ship it", "s1", Path.GetTempPath(), model: model))
            if (e is FlowFinishedEvent f) result = f.Result;
        return Assert.IsType<FlowRunResult>(result);
    }

    [Fact]
    public async Task Flow_CallsTheModelEachStepAsks_AndInheritsTheSessionModelOtherwise()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "routing",
            Stages = new()
            {
                Stage("design", "planner") with { Model = "frontier" },
                Stage("release-notes") with { Model = "fast" },
                Stage("polish") with { Model = "balanced" },
                Stage("plain")
            },
            Edges = new() { Edge("design", "release-notes"), Edge("release-notes", "polish"), Edge("polish", "plain") }
        };

        var result = await RunAsync(Build(), flow, model: "session-model");

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(
            new[] { "main-frontier", "main-fast", "main-balanced", "session-model" },
            _calls.Select(c => c.Model).ToArray());
        Assert.All(_calls, c => Assert.Equal("main", c.Provider));
    }

    [Fact]
    public async Task ParallelSteps_CanUseDifferentProviders()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "parallel-providers",
            Stages = new()
            {
                Stage("plan", "planner"),
                Stage("cheap") with { Provider = "other", Model = "fast" },
                Stage("strong") with { Provider = "main", Model = "frontier" },
                Stage("join", "reviewer")
            },
            Edges = new() { Edge("plan", "cheap"), Edge("plan", "strong"), Edge("cheap", "join"), Edge("strong", "join") }
        };

        var result = await RunAsync(Build(), flow, model: "session-model");

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Contains(("other", "other-fast"), _calls);
        Assert.Contains(("main", "main-frontier"), _calls);
        Assert.Equal(4, _calls.Count);
    }

    [Fact]
    public async Task OldFlowWithoutTheFields_BehavesAsBefore()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "old",
            Stages = new() { Stage("a", "planner"), Stage("b") },
            Edges = new() { Edge("a", "b") }
        };

        await RunAsync(Build(), flow, model: "session-model");

        Assert.Equal(new[] { ("main", (string?)"session-model"), ("main", "session-model") }, _calls.ToArray());
    }

    [Fact]
    public async Task UnknownProvider_FallsBackToTheDefaultProvider()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "typo",
            // Two steps with an edge: a single stage without edges is a linear pipeline, which has no flow result.
            Stages = new() { Stage("a") with { Provider = "nope" }, Stage("b") },
            Edges = new() { Edge("a", "b") }
        };

        var result = await RunAsync(Build(), flow, model: "session-model");

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        var calls = _calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("main", calls[0].Provider);
        Assert.Null(calls[0].Model); // the session model belongs to the default provider; "nope" asked for its own default
        Assert.Equal(("main", (string?)"session-model"), calls[1]);
    }

    [Fact]
    public void ApplyStageModel_Precedence_StageBeatsCharacterBeatsSession()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sam-dev.md"), """
            ---
            id: sam-dev
            description: Developer
            base-role: implementer
            model: character-model
            ---
            SAM
            """);
        var characters = new CharacterRegistry(Presets, NullLogger<CharacterRegistry>.Instance, _root);
        var runner = Build(characters);

        SdlcStageDefinition Dev(string? model) => new() { Id = "dev", Role = "implementer", Character = "sam-dev", Name = "dev", PromptTemplate = "{task}", Model = model };
        string? ModelOf(SdlcStageDefinition stage) => runner.BuildStageStep(stage, "p", "/work", "session-model", agentId: "dev").Options.Model;

        Assert.Equal("character-model", ModelOf(Dev(null)));          // character > session
        Assert.Equal("main-fast", ModelOf(Dev("fast")));              // stage (tier) > character
        Assert.Equal("my-own-model", ModelOf(Dev("my-own-model")));   // stage (id) > character
        Assert.Equal("character-model", ModelOf(Dev("  ")));          // blank = inherit
    }

    [Fact]
    public void ApplyStageModel_TierWithNoModelForTheProvider_DoesNotReuseTheSessionModel()
    {
        var runner = Build();
        var options = runner.ApplyStageModel(
            new SdlcStageDefinition { Id = "x", Provider = "unknown-provider", Model = "fast" },
            new AgentOptions { Model = "session-model" });

        // No catalog entry for that provider → nothing to resolve to; the other provider cannot use the session model.
        Assert.Null(options.Model);
        Assert.Equal("unknown-provider", options.Provider);
    }

    [Fact]
    public void ApplyStageModel_SameProviderAsDefault_DoesNotRoute()
    {
        var runner = Build();
        var options = runner.ApplyStageModel(
            new SdlcStageDefinition { Id = "x", Provider = "MAIN", Model = "frontier" },
            new AgentOptions { Model = "session-model" });

        Assert.Equal("main-frontier", options.Model);
        Assert.Null(options.Provider);
    }

    // ------------------------------------------------------------ JSON

    [Fact]
    public void Json_RoundTrips_ModelAndProvider()
    {
        var def = new SdlcPipelineDefinition
        {
            Name = "rt",
            Stages = new()
            {
                Stage("design", "planner") with { Model = "frontier" },
                Stage("notes") with { Model = "fast", Provider = "openai" },
                Stage("plain")
            },
            Edges = new() { Edge("design", "notes"), Edge("notes", "plain") }
        };

        var json = JsonSerializer.Serialize(def, SdlcPipelineLoader.FileJsonOptions);
        var back = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;

        Assert.Contains("\"model\": \"frontier\"", json);
        Assert.Contains("\"provider\": \"openai\"", json);
        Assert.Equal("frontier", back.Stages[0].Model);
        Assert.Null(back.Stages[0].Provider);
        Assert.Equal("fast", back.Stages[1].Model);
        Assert.Equal("openai", back.Stages[1].Provider);
        Assert.Null(back.Stages[2].Model);
    }

    [Fact]
    public void Json_OldFile_LoadsWithoutTheFields_AndDoesNotWriteThem()
    {
        const string old = """
            { "name": "old", "stages": [ { "id": "a", "role": "planner", "name": "A", "promptTemplate": "{task}" } ] }
            """;

        var def = JsonSerializer.Deserialize<SdlcPipelineDefinition>(old, SdlcPipelineLoader.FileJsonOptions)!;

        Assert.Null(def.Stages[0].Model);
        Assert.Null(def.Stages[0].Provider);
        var json = JsonSerializer.Serialize(def, SdlcPipelineLoader.FileJsonOptions);
        Assert.DoesNotContain("\"model\"", json);
        Assert.DoesNotContain("\"provider\"", json);
    }

    // ------------------------------------------------------------ catalog helpers

    [Theory]
    [InlineData("fast", true, ModelTier.Fast)]
    [InlineData("Balanced", true, ModelTier.Balanced)]
    [InlineData("FRONTIER", true, ModelTier.Frontier)]
    [InlineData("smart", true, ModelTier.Frontier)]
    [InlineData("auto", true, ModelTier.Balanced)]
    [InlineData("gpt-5", false, ModelTier.Fast)]
    [InlineData("", false, ModelTier.Fast)]
    public void TryTierOfName_AcceptsTierNamesAndPickerAliases(string text, bool ok, ModelTier tier)
    {
        Assert.Equal(ok, ModelCatalog.TryTierOfName(text, out var parsed));
        if (ok) Assert.Equal(tier, parsed);
    }

    [Fact]
    public void ResolveModelSetting_ResolvesTiers_KeepsIds_DropsRetiredAndEmpty()
    {
        var catalog = new ModelCatalog(new[]
        {
            new CatalogModel("a-fast", "p", ModelTier.Fast),
            new CatalogModel("a-old", "p", ModelTier.Balanced, Retired: true)
        });

        Assert.Equal("a-fast", catalog.ResolveModelSetting("fast", "p"));
        Assert.Equal("a-fast", catalog.ResolveModelSetting("balanced", "p")); // falls back to a neighbour tier
        Assert.Equal("custom-id", catalog.ResolveModelSetting(" custom-id ", "p"));
        Assert.Null(catalog.ResolveModelSetting("a-old", "p"));
        Assert.Null(catalog.ResolveModelSetting("", "p"));
        Assert.Null(catalog.ResolveModelSetting("fast", "no-such-provider"));
    }
}
