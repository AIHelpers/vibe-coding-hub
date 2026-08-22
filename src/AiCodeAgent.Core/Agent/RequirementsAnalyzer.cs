using System.Text.Json;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Detects missing requirement dimensions in a vague user prompt and produces
/// clarifying questions (Feature 8). Uses a single LLM call with a strict JSON
/// output schema, mirroring the <see cref="PlanGenerator"/> pattern.
/// </summary>
public interface IRequirementsAnalyzer
{
    /// <summary>
    /// Analyzes the user prompt against already-confirmed requirements and
    /// returns a list of clarifying questions for dimensions that remain
    /// unspecified. Returns an empty list when no critical gaps remain.
    /// </summary>
    Task<List<RequirementGap>> AnalyzeAsync(
        string userPrompt,
        AppRequirements? current = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A single clarification gap for a requirement dimension.</summary>
public sealed record RequirementGap
{
    /// <summary>The dimension identifier (platform, auth, dataModel, styling, integrations, deployment).</summary>
    public string Dimension { get; init; } = string.Empty;

    /// <summary>The clarifying question to ask the user.</summary>
    public string Question { get; init; } = string.Empty;
}

/// <summary>
/// LLM-backed implementation of <see cref="IRequirementsAnalyzer"/>.
/// </summary>
public sealed class RequirementsAnalyzer : IRequirementsAnalyzer
{
    private readonly IAiProvider _provider;
    private readonly ILogger<RequirementsAnalyzer> _logger;

    private const string AnalyzerSystemPrompt = """
        You are a requirements analysis assistant for an autonomous coding agent.
        Given a user's app-building prompt and any already-confirmed requirements,
        identify which requirement dimensions are MISSING or AMBIGUOUS and produce
        concise clarifying questions. Output STRICT JSON only — no prose, no code fences.

        Dimensions to evaluate:
        - platform: target runtime (web, mobile, desktop, cli, api, etc.)
        - auth: authentication strategy (none, JWT, OAuth2, session, API key, etc.)
        - dataModel: core entities, relationships, and storage (e.g. "PostgreSQL with Users/Posts")
        - styling: UI/UX guidance (tailwind, material, minimal, dark, etc.)
        - integrations: external services (Stripe, SendGrid, S3, none, etc.)
        - deployment: target environment (docker, vercel, on-prem, serverless, etc.)

        Schema:
        {
          "gaps": [
            {
              "dimension": "platform",
              "question": "Which platform should this target (web, mobile, desktop, CLI)?"
            }
          ]
        }

        Rules:
        - Only include dimensions that are genuinely missing or ambiguous.
        - Skip dimensions already covered in the confirmed requirements.
        - Ask at most 5 questions, prioritizing the most critical dimensions
          (platform, dataModel, auth first; then styling, integrations, deployment).
        - Each question must be specific and answerable in one short sentence.
        - If the prompt is detailed enough that no critical gaps remain, return
          an empty "gaps" array.
        - Output ONLY the JSON object.
        """;

    public RequirementsAnalyzer(IAiProvider provider, ILogger<RequirementsAnalyzer> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task<List<RequirementGap>> AnalyzeAsync(
        string userPrompt,
        AppRequirements? current = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
            return new List<RequirementGap>();

        var userContent = current is null
            ? $"User prompt: {userPrompt}"
            : $"User prompt: {userPrompt}\n\nConfirmed requirements so far:\n{current.ToPromptBlock()}";

        var request = new CompletionRequest
        {
            SystemPrompt = AnalyzerSystemPrompt,
            Messages = new List<Message>
            {
                new() { Role = MessageRole.User, Content = userContent }
            },
            Options = new CompletionOptions { Temperature = 0.2f, MaxTokens = 1024, Stream = false }
        };

        const int maxParseAttempts = 2;
        for (var attempt = 0; attempt < maxParseAttempts; attempt++)
        {
            try
            {
                var response = await _provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                var gaps = ParseGaps(response.Content);
                // Cap at 5 questions per spec
                if (gaps.Count > 5)
                    gaps = gaps.Take(5).ToList();
                return gaps;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Requirements analysis parse attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
            }

            request = request with
            {
                Messages = new List<Message>(request.Messages)
                {
                    new() { Role = MessageRole.Assistant, Content = attempt == 0 ? "" : "Invalid JSON." },
                    new() { Role = MessageRole.User, Content = "Output ONLY valid JSON matching the schema." }
                }
            };
        }

        _logger.LogWarning("Requirements analysis parsing failed after {Attempts} attempts; returning no gaps.", maxParseAttempts);
        return new List<RequirementGap>();
    }

    private static List<RequirementGap> ParseGaps(string content)
    {
        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("No JSON object found in requirements analysis response.");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("gaps", out var gapsEl) || gapsEl.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing 'gaps' array.");

        var result = new List<RequirementGap>();
        foreach (var el in gapsEl.EnumerateArray())
        {
            var dimension = el.TryGetProperty("dimension", out var dimEl) && dimEl.ValueKind == JsonValueKind.String
                ? dimEl.GetString() ?? string.Empty
                : string.Empty;
            var question = el.TryGetProperty("question", out var qEl) && qEl.ValueKind == JsonValueKind.String
                ? qEl.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(dimension) && !string.IsNullOrWhiteSpace(question))
            {
                result.Add(new RequirementGap { Dimension = dimension, Question = question });
            }
        }

        return result;
    }

    private static string ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var start = content.IndexOf('{');
        if (start < 0) return string.Empty;
        var end = content.LastIndexOf('}');
        if (end <= start) return string.Empty;
        return content.Substring(start, end - start + 1);
    }
}