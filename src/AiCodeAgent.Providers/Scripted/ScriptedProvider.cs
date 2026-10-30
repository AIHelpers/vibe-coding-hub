using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Scripted;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Scripted;

/// <summary>
/// E8-S1: a deterministic model that replays scenarios (see docs/testing.md). It needs no API key and costs nothing, so UI tests,
/// smoke tests, documentation screenshots and demos can show realistic agent behavior: streamed text, tool calls that need
/// approval, flows with review loops. Registered as provider <c>scripted</c>; listed only when
/// <see cref="ScriptedProviderSupport.IsEnabled"/> (<c>AIAGENT_ENABLE_SCRIPTED=1</c> or a Debug build).
/// </summary>
public sealed class ScriptedProvider : IAiProvider
{
    /// <summary>Multiplies every scripted delay. <c>AIAGENT_SCRIPTED_DELAY_SCALE=0</c> removes them (fast tests).</summary>
    public const string DelayScaleVariable = "AIAGENT_SCRIPTED_DELAY_SCALE";

    private readonly ScriptedEngine _engine;
    private readonly double _delayScale;

    /// <param name="config">The provider settings; a <c>BaseUrl</c> that is a folder or file is read for scenarios.</param>
    /// <param name="logger">Receives a warning for each scenario file that could not be read.</param>
    /// <param name="scenarios">Use these instead of loading files (tests).</param>
    /// <param name="delayScale">Overrides <see cref="DelayScaleVariable"/> (tests pass 0).</param>
    public ScriptedProvider(ProviderConfiguration? config = null, ILogger? logger = null, IEnumerable<ScriptedScenario>? scenarios = null, double? delayScale = null)
    {
        Problems = new List<string>();
        _engine = new ScriptedEngine(scenarios ?? ScenarioLoader.LoadAll(config?.BaseUrl, logger, (List<string>)Problems));
        _delayScale = delayScale ?? ReadDelayScale();
    }

    public string Name => ScriptedProviderSupport.ProviderName;
    public string[] SupportedModels => new[] { ScriptedProviderSupport.DefaultModel };

    public ScriptedEngine Engine => _engine;

    /// <summary>Scenario files that could not be read.</summary>
    public IReadOnlyList<string> Problems { get; }

    public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        var plan = _engine.Plan(request);
        await Delay(plan.DelayMs, cancellationToken).ConfigureAwait(false);
        return new CompletionResponse
        {
            Content = plan.Text,
            ToolCalls = plan.ToolCalls.ToList(),
            Usage = plan.Usage,
            Model = ScriptedProviderSupport.DefaultModel,
            StopReason = plan.FinishReason
        };
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = _engine.Plan(request);
        await Delay(plan.DelayMs, cancellationToken).ConfigureAwait(false);

        // Text arrives in pieces, like a real stream; the tool calls and the usage ride on the finishing chunk.
        for (var offset = 0; offset < plan.Text.Length; offset += plan.ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamChunk { Delta = plan.Text.Substring(offset, Math.Min(plan.ChunkSize, plan.Text.Length - offset)) };
            if (plan.ChunkDelayMs > 0) await Delay(plan.ChunkDelayMs, cancellationToken).ConfigureAwait(false);
        }

        yield return new StreamChunk
        {
            IsFinished = true,
            FinishReason = plan.FinishReason,
            ToolCalls = plan.ToolCalls.Count > 0 ? plan.ToolCalls.ToList() : null,
            Usage = plan.Usage
        };
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportedModels);

    private Task Delay(int milliseconds, CancellationToken cancellationToken) =>
        milliseconds <= 0 || _delayScale <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(milliseconds * _delayScale), cancellationToken);

    private static double ReadDelayScale() =>
        double.TryParse(Environment.GetEnvironmentVariable(DelayScaleVariable), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale >= 0 ? scale : 1.0;
}
