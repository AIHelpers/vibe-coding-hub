namespace AiCodeAgent.Bench;

/// <summary>
/// One attempt from start to verdict: fresh workspace, setup, the agent, the hidden test. The verdict is the hidden test on the
/// workspace the agent left behind, whatever the agent said about itself; only a timeout is judged without running the test.
/// </summary>
internal sealed class AttemptRunner
{
    private readonly Options _options;
    private readonly IAgentRunner _agent;
    private readonly SnapshotProvider _snapshots;
    private readonly string _suiteDirectory;

    public AttemptRunner(Options options, IAgentRunner agent, TaskSuite suite)
    {
        _options = options;
        _agent = agent;
        _suiteDirectory = suite.BaseDirectory;
        _snapshots = new SnapshotProvider(suite.BaseDirectory, options.ResolvedWorkDir);
    }

    public string WorkspacePath(BenchTask task, BenchConfig config, int seed) =>
        Path.Combine(_options.ResolvedWorkDir, "runs", FileSystemUtil.SafeName($"{task.Id}--{config.Name}--s{seed}"));

    public async Task<Attempt> RunAsync(BenchTask task, BenchConfig config, int seed, CancellationToken cancellation)
    {
        var started = DateTimeOffset.UtcNow;
        var workspace = WorkspacePath(task, config, seed);
        Attempt Result(string status, string? note, AgentRun? run = null, ProcessResult? test = null) => new()
        {
            Task = task.Id,
            Config = config.Name,
            Seed = seed,
            Status = status,
            AgentExitCode = run?.ExitCode,
            TestExitCode = test?.ExitCode,
            Tokens = run?.Usage.Tokens,
            CostUsd = run?.Usage.CostUsd,
            Unpriced = run?.Usage.Unpriced ?? false,
            AgentSeconds = Math.Round(run?.Seconds ?? 0, 2),
            TestSeconds = Math.Round(test?.Duration.TotalSeconds ?? 0, 2),
            Models = run?.Usage.Models.ToArray() ?? Array.Empty<string>(),
            RunId = run?.Usage.RunId,
            StartedUtc = started,
            Note = note
        };

        try
        {
            await _snapshots.CopyToAsync(task, workspace, cancellation);

            if (!string.IsNullOrWhiteSpace(task.SetupCommand))
            {
                var setup = await ProcessRunner.RunShellAsync(task.SetupCommand, workspace, null, TimeSpan.FromSeconds(_options.TestTimeoutSeconds), cancellation);
                if (setup.ExitCode != 0)
                    return Result(AttemptStatus.Error, "setup command failed: " + ProcessRunner.Tail(setup.Stderr + setup.Stdout, 300));
            }

            var run = await _agent.RunAsync(new AgentInvocation(task, config, seed, workspace), cancellation);
            if (run.TimedOut) return Result(AttemptStatus.Timeout, run.Note, run);
            // 3 = invalid arguments and 4 = configuration error (no key, unknown pipeline): the flow never ran, which is not a failed fix.
            if (!config.IsSingle && run.ExitCode is 3 or 4) return Result(AttemptStatus.Error, run.Note ?? $"flow exit code {run.ExitCode}", run);

            if (!string.IsNullOrWhiteSpace(task.TestPatch))
            {
                var patch = Path.GetFullPath(Path.Combine(_suiteDirectory, task.TestPatch));
                var apply = await ProcessRunner.GitAsync(workspace, new[] { "apply", "--whitespace=nowarn", patch }, cancellation);
                if (apply.ExitCode != 0)
                    return Result(AttemptStatus.Error, "the hidden test patch does not apply to the agent's result: " + ProcessRunner.Tail(apply.Stderr, 300), run);
            }

            var test = await ProcessRunner.RunShellAsync(task.TestCommand, workspace, null, TimeSpan.FromSeconds(_options.TestTimeoutSeconds), cancellation);
            var note = run.Note;
            if (test.TimedOut) note = Join(note, "the hidden test timed out");
            return Result(test.ExitCode == 0 && !test.TimedOut ? AttemptStatus.Pass : AttemptStatus.Fail, note, run, test);
        }
        catch (BenchException ex)
        {
            return Result(AttemptStatus.Error, ex.Message);
        }
        finally
        {
            if (!_options.KeepWorkspaces)
            {
                try { FileSystemUtil.ForceDelete(workspace); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Task validation: the hidden test must fail on the untouched snapshot, otherwise the task proves nothing.</summary>
    public async Task<(bool Valid, string Message)> ValidateAsync(BenchTask task, CancellationToken cancellation)
    {
        var workspace = Path.Combine(_options.ResolvedWorkDir, "validate", FileSystemUtil.SafeName(task.Id));
        try
        {
            await _snapshots.CopyToAsync(task, workspace, cancellation);
            if (!string.IsNullOrWhiteSpace(task.SetupCommand))
            {
                var setup = await ProcessRunner.RunShellAsync(task.SetupCommand, workspace, null, TimeSpan.FromSeconds(_options.TestTimeoutSeconds), cancellation);
                if (setup.ExitCode != 0) return (false, "setup command failed: " + ProcessRunner.Tail(setup.Stderr + setup.Stdout, 300));
            }
            if (!string.IsNullOrWhiteSpace(task.TestPatch))
            {
                var patch = Path.GetFullPath(Path.Combine(_suiteDirectory, task.TestPatch));
                var apply = await ProcessRunner.GitAsync(workspace, new[] { "apply", "--whitespace=nowarn", patch }, cancellation);
                if (apply.ExitCode != 0) return (false, "the hidden test patch does not apply: " + ProcessRunner.Tail(apply.Stderr, 300));
            }
            var test = await ProcessRunner.RunShellAsync(task.TestCommand, workspace, null, TimeSpan.FromSeconds(_options.TestTimeoutSeconds), cancellation);
            if (test.TimedOut) return (false, "the test command timed out");
            return test.ExitCode == 0
                ? (false, "the test already PASSES on the untouched snapshot, so it cannot tell a fix from no fix")
                : (true, $"fails on the snapshot as required (exit code {test.ExitCode})");
        }
        catch (BenchException ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { FileSystemUtil.ForceDelete(workspace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;
}
