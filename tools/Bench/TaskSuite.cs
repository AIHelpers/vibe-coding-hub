using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCodeAgent.Bench;

/// <summary>Where a task's repository snapshot comes from: a folder next to the task file, or a git commit.</summary>
internal sealed record RepoSpec
{
    public string? Path { get; init; }
    public string? Git { get; init; }
    public string? Commit { get; init; }
}

/// <summary>One benchmark task (SWE-bench-lite style).</summary>
internal sealed record BenchTask
{
    public string Id { get; init; } = "";
    public string? Language { get; init; }
    /// <summary>Where the task comes from (issue or commit URL), shown in the report.</summary>
    public string? Source { get; init; }
    /// <summary>The task text. Both a single agent and a flow get exactly this text.</summary>
    public string Prompt { get; init; } = "";
    public RepoSpec Repo { get; init; } = new();
    /// <summary>Runs once in the fresh workspace before the agent starts (restore packages and so on). Not timed.</summary>
    public string? SetupCommand { get; init; }
    /// <summary>The hidden test: exit code 0 = the task is solved. Never shown to the agent.</summary>
    public string TestCommand { get; init; } = "";
    /// <summary>A patch with the hidden tests, applied after the agent finished and before <see cref="TestCommand"/>.</summary>
    public string? TestPatch { get; init; }
}

internal sealed record TaskSuite(string Name, string? Description, IReadOnlyList<BenchTask> Tasks, string BaseDirectory);

internal static class TaskSuiteLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal sealed record FileModel
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public List<BenchTask>? Tasks { get; init; }
    }

    public static TaskSuite Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new BenchException($"Task file not found: {full}");
        string json;
        try { json = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BenchException($"Cannot read the task file {full}: {ex.Message}", ex);
        }
        return Parse(json, Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full));
    }

    public static TaskSuite Parse(string json, string baseDirectory, string fallbackName = "tasks")
    {
        FileModel? model;
        try { model = JsonSerializer.Deserialize<FileModel>(json, JsonOptions); }
        catch (JsonException ex) { throw new BenchException("The task file is not valid: " + ex.Message, ex); }
        if (model?.Tasks == null || model.Tasks.Count == 0)
            throw new BenchException("The task file has no tasks. Expected {\"tasks\": [ ... ]}.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in model.Tasks)
        {
            var label = string.IsNullOrWhiteSpace(t.Id) ? "(a task without an id)" : $"Task '{t.Id}'";
            if (string.IsNullOrWhiteSpace(t.Id)) throw new BenchException($"{label}: 'id' is required.");
            if (!t.Id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new BenchException($"{label}: the id may contain only letters, digits, '-', '_' and '.'.");
            if (!seen.Add(t.Id)) throw new BenchException($"{label}: the id is used twice.");
            if (string.IsNullOrWhiteSpace(t.Prompt)) throw new BenchException($"{label}: 'prompt' is required.");
            if (string.IsNullOrWhiteSpace(t.TestCommand)) throw new BenchException($"{label}: 'testCommand' is required (it decides pass or fail).");
            var hasPath = !string.IsNullOrWhiteSpace(t.Repo.Path);
            var hasGit = !string.IsNullOrWhiteSpace(t.Repo.Git);
            if (hasPath == hasGit) throw new BenchException($"{label}: 'repo' needs either 'path' or 'git' (with 'commit').");
            if (hasGit && string.IsNullOrWhiteSpace(t.Repo.Commit))
                throw new BenchException($"{label}: 'repo.commit' is required with 'repo.git' so the snapshot is exact.");
        }
        return new TaskSuite(string.IsNullOrWhiteSpace(model.Name) ? fallbackName : model.Name!, model.Description, model.Tasks, baseDirectory);
    }

    /// <summary>Keeps the tasks whose ids are listed; an unknown id is an error, never ignored.</summary>
    public static TaskSuite Filter(TaskSuite suite, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return suite;
        var unknown = ids.Where(id => !suite.Tasks.Any(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw new BenchException("Unknown task id(s): " + string.Join(", ", unknown));
        return suite with { Tasks = suite.Tasks.Where(t => ids.Contains(t.Id, StringComparer.OrdinalIgnoreCase)).ToList() };
    }
}
