namespace AiCodeAgent.Bench.Tests;

/// <summary>A throwaway folder with a one-file repository snapshot and a task file pointing at it.</summary>
internal sealed class BenchSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vch-bench-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public string Snapshot => Path.Combine(Root, "snapshot");
    public string WorkDir => Path.Combine(Root, "work");
    public string OutDir => Path.Combine(Root, "out");
    public string TasksFile => Path.Combine(Root, "tasks.json");

    public BenchSandbox(params (string Id, string TestCommand)[] tasks)
    {
        Directory.CreateDirectory(Snapshot);
        File.WriteAllText(Path.Combine(Snapshot, "README.txt"), "snapshot");
        var entries = tasks.Select(t =>
            $$"""{ "id": "{{t.Id}}", "language": "csharp", "prompt": "Fix {{t.Id}}", "testCommand": "{{t.TestCommand}}", "repo": { "path": "snapshot" } }""");
        File.WriteAllText(TasksFile, "{ \"name\": \"unit-test suite\", \"tasks\": [" + string.Join(",", entries) + "] }");
    }

    public TaskSuite Suite() => TaskSuiteLoader.Load(TasksFile);

    public Options MakeOptions(Func<Options, Options>? change = null)
    {
        var o = new Options { Model = "test-model", WorkDir = WorkDir, OutDir = OutDir, TasksPath = TasksFile };
        return change == null ? o : change(o);
    }

    public void Dispose()
    {
        try { FileSystemUtil.ForceDelete(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string FileExistsTest(string name) =>
        OperatingSystem.IsWindows() ? $"if exist {name} (exit 0) else (exit 1)" : $"test -f {name}";
}

/// <summary>An agent that does what the test tells it to, so the harness can be checked without a model.</summary>
internal sealed class FakeAgent : IAgentRunner
{
    public List<AgentInvocation> Calls { get; } = new();
    public Func<AgentInvocation, AgentRun>? Behaviour { get; set; }
    public bool WorkspaceWasGitRepo { get; private set; }

    public Task<AgentRun> RunAsync(AgentInvocation invocation, CancellationToken cancellation)
    {
        Calls.Add(invocation);
        WorkspaceWasGitRepo = Directory.Exists(Path.Combine(invocation.WorkDirectory, ".git"));
        var run = Behaviour?.Invoke(invocation) ?? Ok(invocation);
        return Task.FromResult(run);
    }

    public static AgentRun Ok(AgentInvocation inv, long tokens = 1000, decimal cost = 0.10m, double seconds = 5, int exit = 0) =>
        new(exit, false, seconds, new UsageInfo(tokens, cost, false, new[] { "test-model" }, "run-" + inv.Seed, "completed"), null);
}
