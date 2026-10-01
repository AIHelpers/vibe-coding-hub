using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Providers.OpenAI;

/// <summary>
/// OpenAI's reasoning models (o-series, gpt-5 family) take <c>reasoning_effort</c> and <c>max_completion_tokens</c> and
/// reject a custom <c>temperature</c>; every other model keeps the classic parameters.
/// </summary>
public static class OpenAiReasoning
{
    private static readonly Regex ReasoningModels = new(@"^(o[1-9]|gpt-5)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsReasoningModel(string? model) => !string.IsNullOrEmpty(model) && ReasoningModels.IsMatch(model);

    /// <summary>The <c>reasoning_effort</c> value to send, or null to leave the model's default.</summary>
    public static string? Effort(string? model, ReasoningEffort? effort)
    {
        if (!IsReasoningModel(model) || effort is null) return null;
        return effort.Value switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            // "minimal" exists for gpt-5 only; the o-series has no way to switch reasoning off.
            _ => model!.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ? "minimal" : "low"
        };
    }
}
