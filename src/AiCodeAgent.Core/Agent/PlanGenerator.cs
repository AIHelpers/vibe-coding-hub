using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>Generates a structured plan from a task description.</summary>
public interface IPlanGenerator
{
    Task<List<PlanStep>> GenerateAsync(
        string task,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces a user-visible, structured multi-step plan from a natural-language
/// task via a single LLM call with a strict JSON output schema.
/// </summary>
public class PlanGenerator : IPlanGenerator
{
    private readonly IAiProvider _provider;
    private readonly ILogger<PlanGenerator> _logger;

    private const string PlanSystemPrompt = """
        You are a planning assistant for an autonomous coding agent.
        Given a task description, produce a concise, ordered, multi-step plan.
        Each step may touch multiple files and/or run shell commands
        (build, test, lint). Output STRICT JSON only — no prose, no code fences.

        Schema:
        {
          "steps": [
            {
              "description": "short imperative description",
              "files_likely_touched": ["relative/path.ext", ...],
              "verify_command": "optional shell command to run after the step (e.g. dotnet build, npm test) or null"
            }
          ]
        }

        Rules:
        - 1-8 steps, ordered.
        - Prefer small, verifiable steps.
        - verify_command should be null when no verification is appropriate.
        - Output ONLY the JSON object.
        """;

    public PlanGenerator(IAiProvider provider, ILogger<PlanGenerator> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task<List<PlanStep>> GenerateAsync(
        string task,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var request = new CompletionRequest
        {
            SystemPrompt = PlanSystemPrompt,
            Messages = new List<Message>
            {
                new() { Role = MessageRole.User, Content = $"Task: {task}" }
            },
            Options = new CompletionOptions { Temperature = 0.2f, MaxTokens = 2048, Stream = false }
        };

        const int maxParseAttempts = 2;
        for (var attempt = 0; attempt < maxParseAttempts; attempt++)
        {
            try
            {
                var response = await _provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                var steps = ParsePlan(response.Content);
                if (steps.Count > 0)
                    return steps;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Plan parse attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
            }

            // Re-prompt with a correction hint on retry
            request = request with
            {
                Messages = new List<Message>(request.Messages)
                {
                    new() { Role = MessageRole.Assistant, Content = attempt == 0 ? "" : "Invalid JSON." },
                    new() { Role = MessageRole.User, Content = "Output ONLY valid JSON matching the schema." }
                }
            };
        }

        // Fallback: single degenerate step so the runner can still attempt the task.
        _logger.LogWarning("Plan parsing failed after {Attempts} attempts; using fallback single-step plan.", maxParseAttempts);
        return new List<PlanStep>
        {
            new() { Index = 1, Description = task }
        };
    }

    private static List<PlanStep> ParsePlan(string content)
    {
        // Tolerate surrounding prose / code fences by extracting the first JSON object.
        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("No JSON object found in planner response.");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing 'steps' array.");

        var result = new List<PlanStep>();
        var index = 1;
        foreach (var el in stepsEl.EnumerateArray())
        {
            var step = new PlanStep { Index = index++ };
            if (el.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                step.Description = descEl.GetString() ?? string.Empty;
            else
                continue;

            if (el.TryGetProperty("files_likely_touched", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in filesEl.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.String)
                        step.FilesLikelyTouched.Add(f.GetString() ?? string.Empty);
                }
            }

            if (el.TryGetProperty("verify_command", out var vEl) && vEl.ValueKind == JsonValueKind.String)
            {
                var cmd = vEl.GetString();
                if (!string.IsNullOrWhiteSpace(cmd))
                    step.VerifyCommand = cmd;
            }

            result.Add(step);
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