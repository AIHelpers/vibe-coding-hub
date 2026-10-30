using System.Text.Json;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Providers.Scripted;

/// <summary>What the scripted model answers to one request.</summary>
public sealed record ScriptedPlan(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    TokenUsage Usage,
    int DelayMs,
    int ChunkDelayMs,
    int ChunkSize,
    string? Scenario,
    string? Agent,
    int Round,
    int TurnIndex,
    bool Matched)
{
    public string FinishReason => ToolCalls.Count > 0 ? "tool_calls" : "stop";
}

/// <summary>
/// The logic of the scripted provider, without I/O: given the scenarios and a request, work out which script applies, which
/// turn comes next and what it says. The turn is the number of assistant messages already in the conversation, so the provider
/// is stateless inside a conversation. The only state is the round counter of each script (how often its conversation started).
/// </summary>
public sealed class ScriptedEngine
{
    public const int DefaultChunkSize = 32;

    private sealed record Candidate(ScriptedScenario Scenario, ScriptedAgent Agent, string Name, IReadOnlyList<IReadOnlyList<ScriptedTurn>> Rounds)
    {
        public string Key => Scenario.Name + "/" + Name;
    }

    private readonly List<Candidate> _candidates = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _starts = new();
    private readonly Dictionary<string, int> _roundOfConversation = new();

    public ScriptedEngine(IEnumerable<ScriptedScenario> scenarios)
    {
        Scenarios = scenarios.ToList();
        foreach (var scenario in Scenarios)
        {
            var own = scenario.AllRounds();
            if (own.Count > 0) _candidates.Add(new Candidate(scenario, scenario, scenario.Name ?? "", own));
            foreach (var agent in scenario.Agents ?? new List<ScriptedAgent>())
            {
                var rounds = agent.AllRounds();
                if (rounds.Count > 0) _candidates.Add(new Candidate(scenario, agent, agent.Name ?? "agent" + _candidates.Count, rounds));
            }
        }
    }

    public IReadOnlyList<ScriptedScenario> Scenarios { get; }

    /// <summary>Forgets how often each conversation started (every script begins at round 1 again).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _starts.Clear();
            _roundOfConversation.Clear();
        }
    }

    public ScriptedPlan Plan(CompletionRequest request)
    {
        var system = SystemText(request);
        var firstUser = request.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content ?? string.Empty;
        var assistantTurns = request.Messages.Count(m => m.Role == MessageRole.Assistant);

        var candidate = _candidates.FirstOrDefault(c => Matches(c, system, firstUser));
        if (candidate == null)
            return Failure($"No scripted scenario matches this conversation. Known scenarios: {string.Join(", ", Scenarios.Select(s => s.Name))}. " +
                           "See docs/testing.md for how scenarios match.", request, null, null, 0, assistantTurns, matched: false);

        // A new conversation of this script starts a new round; later requests of the same conversation keep it.
        var conversation = candidate.Key + "\u0001" + system.GetHashCode() + "\u0001" + firstUser.GetHashCode();
        int round;
        lock (_gate)
        {
            if (assistantTurns == 0)
            {
                var started = _starts.GetValueOrDefault(candidate.Key);
                _starts[candidate.Key] = started + 1;
                round = Math.Min(started, candidate.Rounds.Count - 1);
                _roundOfConversation[conversation] = round;
            }
            else
            {
                round = _roundOfConversation.GetValueOrDefault(conversation);
            }
        }

        var turns = candidate.Rounds[round];
        if (assistantTurns >= turns.Count)
            return new ScriptedPlan("(The scripted scenario has no more turns.)", Array.Empty<ToolCall>(), Estimate(request, "x", 0), 0, 0, DefaultChunkSize,
                candidate.Scenario.Name, candidate.Name, round + 1, assistantTurns, true);

        var previousResult = LastToolResults(request);
        var turn = turns[assistantTurns];
        for (var depth = 0; turn.IfResultContains != null; depth++)
        {
            if (previousResult.Contains(turn.IfResultContains, StringComparison.OrdinalIgnoreCase)) break;
            if (turn.Else == null || depth > 8)
                return Failure($"Scenario '{candidate.Scenario.Name}', {candidate.Name}, round {round + 1}, turn {assistantTurns + 1}: " +
                               $"expected the previous tool result to contain \"{turn.IfResultContains}\" and the turn has no \"else\". " +
                               $"Previous result: {Shorten(previousResult)}", request, candidate.Scenario.Name, candidate.Name, round + 1, assistantTurns, matched: true);
            turn = turn.Else;
        }

        var toolCalls = turn.AllToolCalls()
            .Select((c, i) => new ToolCall
            {
                Id = $"scripted-{candidate.Name}-r{round + 1}-t{assistantTurns + 1}-{i + 1}",
                Name = c.Name,
                Arguments = c.Args.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value))
            })
            .ToList();
        var text = turn.Text ?? string.Empty;
        var usage = turn.Usage != null
            ? new TokenUsage { PromptTokens = turn.Usage.Prompt, CompletionTokens = turn.Usage.Completion }
            : Estimate(request, text + string.Join("", toolCalls.Select(c => c.Name + JsonSerializer.Serialize(c.Arguments))), 0);
        return new ScriptedPlan(text, toolCalls, usage, Math.Max(0, turn.DelayMs), Math.Max(0, turn.ChunkDelayMs),
            turn.Chunk is > 0 ? turn.Chunk.Value : DefaultChunkSize, candidate.Scenario.Name, candidate.Name, round + 1, assistantTurns, true);
    }

    private static bool Matches(Candidate c, string system, string firstUser)
    {
        var scenarioMatch = c.Scenario.Match;
        var agentMatch = ReferenceEquals(c.Agent, c.Scenario) ? null : c.Agent.Match;
        // A script with no condition at all would answer every conversation: it never matches by accident.
        if ((scenarioMatch == null || scenarioMatch.IsEmpty) && (agentMatch == null || agentMatch.IsEmpty)) return false;
        return (scenarioMatch?.IsMatch(system, firstUser) ?? true) && (agentMatch?.IsMatch(system, firstUser) ?? true);
    }

    private static ScriptedPlan Failure(string text, CompletionRequest request, string? scenario, string? agent, int round, int turn, bool matched) =>
        new("Scripted provider: " + text, Array.Empty<ToolCall>(), Estimate(request, text, 0), 0, 0, DefaultChunkSize, scenario, agent, round, turn, matched);

    private static string SystemText(CompletionRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) parts.Add(request.SystemPrompt);
        parts.AddRange(request.Messages.Where(m => m.Role == MessageRole.System).Select(m => m.Content));
        return string.Join("\n", parts);
    }

    /// <summary>The tool results that followed the last assistant message, joined.</summary>
    private static string LastToolResults(CompletionRequest request)
    {
        var results = new List<string>();
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var m = request.Messages[i];
            if (m.Role == MessageRole.Assistant) break;
            if (m.Role == MessageRole.Tool) results.Insert(0, m.Content);
        }
        return string.Join("\n", results);
    }

    private static TokenUsage Estimate(CompletionRequest request, string output, int _)
    {
        var inputChars = (request.SystemPrompt?.Length ?? 0) + request.Messages.Sum(m => m.Content?.Length ?? 0);
        return new TokenUsage { PromptTokens = Math.Max(1, inputChars / 4), CompletionTokens = Math.Max(1, output.Length / 4) };
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";

    /// <summary>Arguments arrive as JSON values, the same shape the real providers hand to tools.</summary>
    private static object? ToJsonValue(object? value)
    {
        if (value is JsonElement or null) return value;
        return JsonSerializer.SerializeToElement(value);
    }
}
