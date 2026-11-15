using System.Globalization;

namespace AiCodeAgent.Bench;

/// <param name="Task">The task being solved.</param>
/// <param name="WorkDirectory">The attempt's workspace: a fresh copy of the snapshot.</param>
internal sealed record AgentInvocation(BenchTask Task, BenchConfig Config, int Seed, string WorkDirectory);

/// <param name="ExitCode">The agent's exit code; -1 when it was killed by the timeout.</param>
internal sealed record AgentRun(int ExitCode, bool TimedOut, double Seconds, UsageInfo Usage, string? Note);

/// <summary>Runs an agent on a prepared workspace. The only implementation that matters starts the aiagent CLI; tests use a fake.</summary>
internal interface IAgentRunner
{
    Task<AgentRun> RunAsync(AgentInvocation invocation, CancellationToken cancellation);
}

/// <summary>
/// Drives the <c>aiagent</c> CLI as a child process: <c>aiagent run</c> for <c>single</c> (chat mode, tools auto-approved) and
/// <c>aiagent pipeline run --non-interactive --permission FullAuto</c> for a flow, so both may change files and run commands.
/// </summary>
internal sealed class CliAgentRunner : IAgentRunner, ISetupPreparer
{
    private readonly Options _options;
    private readonly string _file;
    private readonly string[] _prefix;

    public CliAgentRunner(Options options, string file, string[] prefix)
    {
        _options = options;
        _file = file;
        _prefix = prefix;
    }

    /// <summary>The configured <c>--agent</c>, or the newest build of the CLI project found above the current folder.</summary>
    public static CliAgentRunner Create(Options options)
    {
        var path = options.AgentCommand;
        if (string.IsNullOrWhiteSpace(path)) path = FindBuiltAgent();
        if (path == null)
            throw new BenchException("The aiagent CLI was not found. Build it (dotnet build AiCodeAgent.slnx) or pass --agent <path to aiagent or aiagent.dll>.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new BenchException($"--agent: file not found: {path}");
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new CliAgentRunner(options, "dotnet", new[] { path })
            : new CliAgentRunner(options, path, Array.Empty<string>());
    }

    internal static string? FindBuiltAgent()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var project = Path.Combine(dir.FullName, "src", "AiCodeAgent.CLI");
                if (!Directory.Exists(project)) continue;
                var bin = Path.Combine(project, "bin");
                if (!Directory.Exists(bin)) continue;
                var found = Directory.EnumerateFiles(bin, "aiagent.dll", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>The CLI's own version line (for the methodology section); "unknown" when it cannot be read.</summary>
    public async Task<string> DescribeAsync(CancellationToken cancellation)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(_file, _prefix.Concat(new[] { "--version" }), Directory.GetCurrentDirectory(),
                BaseEnvironment(0, "", ""), TimeSpan.FromSeconds(60), cancellation);
            var line = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(line) ? line! : "unknown";
        }
        catch (BenchException) { return "unknown"; }
    }

    private static readonly string[] BuiltInPipelines = { "full-sdlc", "quick-fix" };

    /// <summary>
    /// Prepares the isolated agent home of a matrix setup: points the provider at its endpoint and installs the flow templates
    /// (with their characters) that the configurations use, exactly as a user would with <c>config add-provider</c> and <c>pipeline copy</c>.
    /// </summary>
    public async Task<string?> PrepareAsync(MatrixSetup setup, IReadOnlyList<BenchConfig> configs, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(_options.AgentHome)) return "no agent home for the setup";
        var home = Path.GetFullPath(_options.AgentHome);
        Directory.CreateDirectory(home);

        var steps = new List<string[]>();
        if (setup.BaseUrl != null)
            steps.Add(new[] { "config", "add-provider", setup.Provider, "--url", setup.BaseUrl, "--model", setup.Model });
        foreach (var pipeline in configs.Where(c => !c.IsSingle).Select(c => c.Pipeline!).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!BuiltInPipelines.Contains(pipeline, StringComparer.OrdinalIgnoreCase))
                steps.Add(new[] { "pipeline", "copy", pipeline, "--to", "global", "--with-characters" });

        foreach (var step in steps)
        {
            var result = await ProcessRunner.RunAsync(_file, _prefix.Concat(step), home, BaseEnvironment(0, "", ""), TimeSpan.FromMinutes(2), cancellation);
            if (result.ExitCode != 0)
                return $"'aiagent {string.Join(' ', step.Take(3))}' failed (exit code {result.ExitCode}): {ProcessRunner.Tail(result.Stderr + result.Stdout, 300)}";
        }
        return null;
    }

    public async Task<AgentRun> RunAsync(AgentInvocation inv, CancellationToken cancellation)
    {
        var args = new List<string>(_prefix);
        if (inv.Config.IsSingle)
        {
            args.AddRange(new[] { "run", inv.Task.Prompt, "--model", _options.Model! });
        }
        else
        {
            args.AddRange(new[] { "pipeline", "run", inv.Task.Prompt, "--pipeline", inv.Config.Pipeline!, "--non-interactive",
                "--output", "json", "--permission", "FullAuto", "--model", _options.Model!,
                "--timeout", _options.TimeoutSeconds.ToString(CultureInfo.InvariantCulture) });
            if (_options.MaxBudgetUsd is { } cap) args.AddRange(new[] { "--max-budget", cap.ToString(CultureInfo.InvariantCulture) });
        }
        if (!string.IsNullOrWhiteSpace(_options.Provider)) args.AddRange(new[] { "--provider", _options.Provider });
        args.AddRange(new[] { "--dir", inv.WorkDirectory });

        var runsRoot = RunsRoot();
        var before = inv.Config.IsSingle ? AuditFiles(runsRoot) : new HashSet<string>();
        // The flow has its own timeout (exit code 1); the process limit is a little longer so the flow can report before it is killed.
        var limit = TimeSpan.FromSeconds(_options.TimeoutSeconds + (inv.Config.IsSingle ? 0 : 120));
        var result = await ProcessRunner.RunAsync(_file, args, inv.WorkDirectory,
            BaseEnvironment(inv.Seed, inv.Task.Id, inv.Config.Name), limit, cancellation);

        UsageInfo usage;
        string? note = null;
        if (inv.Config.IsSingle)
        {
            usage = ReadNewAudit(runsRoot, before);
            if (usage.Tokens == null) note = "no usage found in the run report (audit file)";
            if (result.ExitCode != 0 && !result.TimedOut) note = AppendNote(note, "exit code " + result.ExitCode + ": " + ErrorSummary(result.Stderr));
        }
        else
        {
            usage = UsageParsers.FromFlowJson(result.Stdout, out var reportedExit);
            if (usage.Tokens == null) note = "no usage in the flow report";
            if (result.ExitCode != 0 && !result.TimedOut)
                note = AppendNote(note, $"exit code {reportedExit ?? result.ExitCode}: " + ProcessRunner.Tail(result.Stderr, 300));
        }
        return new AgentRun(result.ExitCode, result.TimedOut, result.Duration.TotalSeconds, usage, result.TimedOut ? "agent timed out" : note);
    }

    private IReadOnlyDictionary<string, string?> BaseEnvironment(int seed, string taskId, string config)
    {
        var env = new Dictionary<string, string?>
        {
            // The seed numbers the repetition. Providers that accept one could use it; the agent does not promise reproducible sampling.
            ["AIAGENT_BENCH_SEED"] = seed.ToString(CultureInfo.InvariantCulture),
            ["AIAGENT_BENCH_TASK"] = taskId,
            ["AIAGENT_BENCH_CONFIG"] = config
        };
        if (!string.IsNullOrWhiteSpace(_options.AgentHome)) env["AIAGENT_HOME"] = Path.GetFullPath(_options.AgentHome);
        if (_options.AgentEnvironment != null)
            foreach (var (key, value) in _options.AgentEnvironment) env[key] = value;
        return env;
    }

    private string RunsRoot()
    {
        var home = !string.IsNullOrWhiteSpace(_options.AgentHome) ? Path.GetFullPath(_options.AgentHome)
            : Environment.GetEnvironmentVariable("AIAGENT_HOME") is { Length: > 0 } fromEnv ? fromEnv
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aiagent");
        return Path.Combine(home, "runs");
    }

    private static HashSet<string> AuditFiles(string runsRoot) =>
        Directory.Exists(runsRoot)
            ? Directory.EnumerateFiles(runsRoot, "*.audit.jsonl").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attempts run one after the other, so the one audit file that appeared during the run is this attempt's.</summary>
    private static UsageInfo ReadNewAudit(string runsRoot, HashSet<string> before)
    {
        var created = AuditFiles(runsRoot).Where(f => !before.Contains(f)).OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        if (created == null) return UsageInfo.Unknown;
        try
        {
            using var stream = new FileStream(created, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return UsageParsers.FromAuditLines(lines, Path.GetFileName(created).Replace(".audit.jsonl", "", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return UsageInfo.Unknown; }
    }

    /// <summary>The first line of the error (what went wrong) and the end of it; a stack trace alone buries the message.</summary>
    internal static string ErrorSummary(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return "";
        var first = ProcessRunner.Tail(lines[0], 200);
        var tail = ProcessRunner.Tail(stderr, 300);
        return lines.Length == 1 || tail.Contains(lines[0], StringComparison.Ordinal) ? tail : first + " ... " + tail;
    }

    private static string AppendNote(string? note, string more) => string.IsNullOrEmpty(note) ? more : note + "; " + more;
}
