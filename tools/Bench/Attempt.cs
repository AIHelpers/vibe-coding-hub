using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCodeAgent.Bench;

internal static class AttemptStatus
{
    /// <summary>The hidden test passed.</summary>
    public const string Pass = "pass";
    /// <summary>The agent finished (or stopped) and the hidden test failed.</summary>
    public const string Fail = "fail";
    /// <summary>The agent hit the wall-clock limit. Counts as a failure.</summary>
    public const string Timeout = "timeout";
    /// <summary>The harness or the setup broke (no snapshot, setup failed, flow could not start). Counts as a failure, listed separately.</summary>
    public const string Error = "error";
}

/// <summary>One run of one task in one configuration with one seed: the row of the CSV.</summary>
internal sealed record Attempt
{
    public string Task { get; init; } = "";
    public string Config { get; init; } = "";
    public int Seed { get; init; }
    public string Status { get; init; } = AttemptStatus.Error;
    public int? AgentExitCode { get; init; }
    public int? TestExitCode { get; init; }
    public long? Tokens { get; init; }
    public decimal? CostUsd { get; init; }
    /// <summary>The model had no known price, so the cost understates the spend.</summary>
    public bool Unpriced { get; init; }
    /// <summary>Wall-clock seconds of the agent run (the same clock for every configuration; the hidden test is not included).</summary>
    public double AgentSeconds { get; init; }
    public double TestSeconds { get; init; }
    public string[] Models { get; init; } = Array.Empty<string>();
    public DateTimeOffset StartedUtc { get; init; }
    public string? RunId { get; init; }
    public string? Note { get; init; }

    [JsonIgnore]
    public bool Passed => Status == AttemptStatus.Pass;
    [JsonIgnore]
    public string Key => KeyOf(Task, Config, Seed);
    public static string KeyOf(string task, string config, int seed) => $"{task}\u001f{config}\u001f{seed}";
}

/// <summary>The file the attempts are appended to, one JSON object per line, as they finish.</summary>
internal static class AttemptStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string FilePath(string outDirectory) => Path.Combine(outDirectory, "attempts.jsonl");

    public static List<Attempt> Load(string outDirectory)
    {
        var path = FilePath(outDirectory);
        var list = new List<Attempt>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var a = JsonSerializer.Deserialize<Attempt>(line, Json);
                if (a != null) list.Add(a);
            }
            catch (JsonException) { /* a half-written last line from a killed run: that attempt is simply repeated */ }
        }
        return list;
    }

    public static void Append(string outDirectory, Attempt attempt)
    {
        Directory.CreateDirectory(outDirectory);
        File.AppendAllText(FilePath(outDirectory), JsonSerializer.Serialize(attempt, Json) + Environment.NewLine);
    }
}
