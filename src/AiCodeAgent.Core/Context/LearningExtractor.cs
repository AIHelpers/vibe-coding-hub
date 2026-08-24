using System.Text.RegularExpressions;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>
/// Lightweight heuristic analyzer that inspects conversation turns and extracts
/// candidate learnings (preferences, conventions, corrections).
/// </summary>
public class LearningExtractor
{
    private readonly ILogger<LearningExtractor>? _logger;

    public LearningExtractor(ILogger<LearningExtractor>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze a user message and assistant response pair, returning any
    /// candidate learnings extracted from the user's wording.
    /// </summary>
    public IReadOnlyList<Learning> Extract(string sessionId, string userMessage, string? assistantResponse = null)
    {
        var results = new List<Learning>();
        if (string.IsNullOrWhiteSpace(userMessage))
            return results;

        var text = userMessage.Trim();

        // 1. Corrections: "no, use X instead", "don't use Y", "actually, prefer Z"
        var correctionPatterns = new[]
        {
            @"(?:^|\s)(?:no,?\s+)?(?:don't|do not|never)\s+use\s+(.+?)(?:[.;]|$)",
            @"(?:^|\s)use\s+(.+?)\s+instead",
            @"(?:^|\s)actually,?\s*(?:prefer|use|always use)\s+(.+?)(?:[.;]|$)",
            @"(?:^|\s)please\s+(?:use|prefer|always use)\s+(.+?)(?:[.;]|$)"
        };

        foreach (var pattern in correctionPatterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var candidate = m.Groups[1].Value.Trim().TrimEnd('.');
                if (candidate.Length > 3 && candidate.Length < 200)
                {
                    results.Add(new Learning
                    {
                        SessionId = sessionId,
                        Text = $"Prefer {candidate}",
                        Category = "correction",
                        CapturedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // 2. Stated preferences/conventions: "always use X", "I prefer Y", "please use Z"
        var preferencePatterns = new[]
        {
            @"(?:^|\s)always\s+use\s+(.+?)(?:[.;]|$)",
            @"(?:^|\s)i\s+prefer\s+(.+?)(?:[.;]|$)",
            @"(?:^|\s)make\s+sure\s+(?:to\s+)?(.+?)(?:[.;]|$)",
            @"(?:^|\s)remember\s+to\s+(.+?)(?:[.;]|$)"
        };

        foreach (var pattern in preferencePatterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var candidate = m.Groups[1].Value.Trim().TrimEnd('.');
                if (candidate.Length > 3 && candidate.Length < 200)
                {
                    results.Add(new Learning
                    {
                        SessionId = sessionId,
                        Text = char.ToUpper(candidate[0]) + candidate[1..],
                        Category = "preference",
                        CapturedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // 3. "always/never <verb>" convention statements
        var conventionPattern = @"(?:^|\s)(always|never)\s+(\w[\w\s]{2,}?)(?:[.;]|$)";
        foreach (Match m in Regex.Matches(text, conventionPattern, RegexOptions.IgnoreCase))
        {
            var candidate = $"{m.Groups[1].Value} {m.Groups[2].Value.Trim()}".TrimEnd('.');
            if (candidate.Length > 5 && candidate.Length < 200)
            {
                results.Add(new Learning
                {
                    SessionId = sessionId,
                    Text = char.ToUpper(candidate[0]) + candidate[1..],
                    Category = "convention",
                    CapturedAt = DateTime.UtcNow
                });
            }
        }

        _logger?.LogDebug("Extracted {Count} candidate learnings from session {Session}", results.Count, sessionId);
        return results;
    }
}