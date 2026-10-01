using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Providers.Anthropic;

/// <summary>Maps <see cref="ReasoningEffort"/> onto Anthropic's extended-thinking request fields.</summary>
public static class AnthropicThinking
{
    private static readonly Regex ThinkingModels = new(
        @"claude-3-7|claude-(opus|sonnet|haiku)-[4-9]|claude-[4-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for models that accept the <c>thinking</c> parameter (Claude 3.7 and the 4.x+ families).</summary>
    public static bool Supports(string? model) => !string.IsNullOrEmpty(model) && ThinkingModels.IsMatch(model);

    /// <summary>Thinking token budget for an effort level, or null when thinking should stay off.</summary>
    public static int? Budget(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.Low => 2_048,
        ReasoningEffort.Medium => 8_192,
        ReasoningEffort.High => 24_000,
        _ => null
    };

    /// <summary>The API needs max_tokens to exceed the thinking budget; leave room for the visible answer.</summary>
    public static int MaxTokensFor(int requested, int budget) => Math.Max(requested, budget + 4_096);
}
