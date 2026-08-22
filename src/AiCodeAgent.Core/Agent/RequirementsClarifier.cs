using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Runs the conversational requirements clarification loop (Feature 8).
/// The loop detects missing requirement dimensions via
/// <see cref="IRequirementsAnalyzer"/>, asks the user up to 5 questions,
/// updates the <see cref="AppRequirements"/> with each answer, and stops
/// when no critical gaps remain or the user chooses "just build it".
/// </summary>
public interface IRequirementsClarifier
{
    /// <summary>
    /// Runs the clarification loop. <paramref name="askFunc"/> presents a
    /// question to the user and returns their answer (or null/empty to skip).
    /// Returns the confirmed <see cref="AppRequirements"/>; <see cref="AppRequirements.Skipped"/>
    /// is true when the user chose "just build it".
    /// </summary>
    Task<AppRequirements> RunAsync(
        string userPrompt,
        Func<string, string, Task<string?>> askFunc,
        CancellationToken cancellationToken = default);
}

/// <summary>LLM-backed implementation of <see cref="IRequirementsClarifier"/>.</summary>
public sealed class RequirementsClarifier : IRequirementsClarifier
{
    private readonly IRequirementsAnalyzer _analyzer;
    private readonly ILogger<RequirementsClarifier> _logger;

    private const int MaxQuestions = 5;
    // Sentinel phrase the user can type to short-circuit the loop.
    private const string SkipPhrase = "just build it";

    public RequirementsClarifier(IRequirementsAnalyzer analyzer, ILogger<RequirementsClarifier> logger)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public async Task<AppRequirements> RunAsync(
        string userPrompt,
        Func<string, string, Task<string?>> askFunc,
        CancellationToken cancellationToken = default)
    {
        var requirements = new AppRequirements();
        var asked = 0;

        while (asked < MaxQuestions && !cancellationToken.IsCancellationRequested)
        {
            var gaps = await _analyzer.AnalyzeAsync(userPrompt, requirements, cancellationToken).ConfigureAwait(false);
            if (gaps.Count == 0)
            {
                _logger.LogDebug("No critical requirement gaps remain; finishing clarification.");
                break;
            }

            // Ask the highest-priority remaining gap.
            var gap = gaps[0];
            var answer = await askFunc(gap.Dimension, gap.Question).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(answer))
            {
                // Skip this dimension; analyzer will see it as unspecified and
                // may re-surface it next pass. Cap iterations via asked counter.
                asked++;
                continue;
            }

            if (answer.Trim().Equals(SkipPhrase, StringComparison.OrdinalIgnoreCase))
            {
                requirements.Skipped = true;
                requirements.Notes.Add("User chose to skip clarification (\"just build it\").");
                _logger.LogInformation("User skipped requirements clarification.");
                break;
            }

            ApplyAnswer(requirements, gap.Dimension, answer.Trim());
            asked++;
        }

        return requirements;
    }

    private static void ApplyAnswer(AppRequirements req, string dimension, string answer)
    {
        switch (dimension.ToLowerInvariant())
        {
            case "platform":
                req.Platform = answer;
                break;
            case "auth":
                req.Auth = answer;
                break;
            case "datamodel":
            case "data model":
                req.DataModel = answer;
                break;
            case "styling":
                req.Styling = answer;
                break;
            case "integrations":
            case "integration":
                req.Integrations = answer;
                break;
            case "deployment":
                req.Deployment = answer;
                break;
            default:
                req.Notes.Add($"{dimension}: {answer}");
                break;
        }
    }
}