using System.Diagnostics;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Usage;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Asks a second model to review a change (E10-S4). One request without tools: the reviewer only reads the diff it is given, so it
/// cannot change anything and cannot be steered by files it was not shown. An answer that is not valid JSON gets one repair request.
/// </summary>
public sealed class IndependentReviewer
{
    private readonly ILogger? _logger;

    public IndependentReviewer(ILogger? logger = null) => _logger = logger;

    public async Task<ReviewOutcome> ReviewAsync(ReviewerChoice choice, ReviewInput input, CancellationToken cancellationToken = default)
    {
        if (!input.HasChanges)
            return ReviewOutcome.Failed("The run changed no files, so there is nothing to review.");

        var clock = Stopwatch.StartNew();
        var usage = new TokenUsage();
        var messages = new List<Message> { new() { Role = MessageRole.User, Content = ReviewerPrompt.User(input) } };
        var ids = input.Criteria.Select(c => c.Id).ToList();
        ReviewerResponseParser.ParsedReview? parsed = null;

        try
        {
            for (var attempt = 0; attempt < 2 && parsed == null; attempt++)
            {
                var response = await choice.Provider.CompleteAsync(new CompletionRequest
                {
                    SystemPrompt = ReviewerPrompt.System,
                    Messages = messages,
                    Options = new CompletionOptions { Model = choice.Model, Temperature = 0.1f, MaxTokens = 4096, Stream = false }
                }, cancellationToken).ConfigureAwait(false);

                usage = Add(usage, response.Usage);
                parsed = ReviewerResponseParser.Parse(response.Content, input.ChangedFiles, ids);
                if (parsed == null)
                {
                    _logger?.LogWarning("The reviewer ({Model}) did not return a usable answer (attempt {Attempt})", choice.Model, attempt + 1);
                    messages = new List<Message>(messages)
                    {
                        new() { Role = MessageRole.Assistant, Content = response.Content ?? string.Empty },
                        new() { Role = MessageRole.User, Content = ReviewerPrompt.RepairRequest }
                    };
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The independent review with {Model} failed", choice.Model);
            return ReviewOutcome.Failed($"The reviewer ({choice.Model} on {choice.ProviderName}) could not be reached: {ex.Message}");
        }

        if (parsed == null)
            return ReviewOutcome.Failed($"The reviewer ({choice.Model} on {choice.ProviderName}) did not return an answer in the expected format.");

        return ReviewOutcome.Ok(new IndependentReview
        {
            Provider = choice.ProviderName,
            Model = choice.Model,
            Independence = choice.Independence,
            Verdict = parsed.Verdict,
            Summary = parsed.Summary,
            Findings = parsed.Findings,
            Criteria = parsed.Criteria,
            UnreviewedFiles = input.UnreviewedFiles,
            DiffTruncated = input.Truncated,
            ReviewedUtc = DateTimeOffset.UtcNow,
            Seconds = clock.Elapsed.TotalSeconds,
            Tokens = usage.PromptTokens + usage.CompletionTokens + usage.CacheReadTokens + usage.CacheCreationTokens,
            CostUsd = ModelPricing.Cost(choice.Model, usage),
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheCreationTokens = usage.CacheCreationTokens
        });
    }

    private static TokenUsage Add(TokenUsage a, TokenUsage? b) => b == null ? a : new TokenUsage
    {
        PromptTokens = a.PromptTokens + b.PromptTokens,
        CompletionTokens = a.CompletionTokens + b.CompletionTokens,
        CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
        CacheCreationTokens = a.CacheCreationTokens + b.CacheCreationTokens
    };
}
