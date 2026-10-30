using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AiCodeAgent.Bench;

/// <summary>Exit codes: 0 report written from a complete run, 1 the run was cut short (a partial report is still written), 2 bad input.</summary>
internal static class BenchApp
{
    public static async Task<int> RunAsync(string[] args, IAgentRunner? agentOverride, TextWriter stdout, TextWriter stderr, CancellationToken cancellation)
    {
        var parsed = OptionsParser.Parse(args);
        if (parsed.ShowHelp) { stdout.WriteLine(Options.HelpText); return 0; }
        if (parsed.Error != null) { stderr.WriteLine(parsed.Error); return 2; }
        var options = parsed.Options!;

        try
        {
            var suite = TaskSuiteLoader.Filter(TaskSuiteLoader.Load(options.TasksPath), options.Only);
            var configs = BenchConfig.ParseList(options.Configs);

            if (options.List)
            {
                stdout.WriteLine($"{suite.Name}: {suite.Tasks.Count} task(s)");
                foreach (var t in suite.Tasks)
                    stdout.WriteLine($"  {t.Id,-32} {t.Language,-8} {t.Source ?? ""}");
                return 0;
            }
            if (options.Validate) return await ValidateAsync(options, suite, stdout, cancellation);
            return await BenchmarkAsync(options, suite, configs, agentOverride, stdout, stderr, cancellation);
        }
        catch (BenchException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("Cancelled.");
            return 1;
        }
    }

    private static async Task<int> ValidateAsync(Options options, TaskSuite suite, TextWriter stdout, CancellationToken cancellation)
    {
        var runner = new AttemptRunner(options, new NoAgent(), suite);
        var bad = 0;
        foreach (var task in suite.Tasks)
        {
            var (valid, message) = await runner.ValidateAsync(task, cancellation);
            stdout.WriteLine($"{(valid ? "ok     " : "INVALID")} {task.Id}: {message}");
            if (!valid) bad++;
        }
        stdout.WriteLine(bad == 0 ? $"All {suite.Tasks.Count} task(s) are valid." : $"{bad} of {suite.Tasks.Count} task(s) are invalid.");
        return bad == 0 ? 0 : 2;
    }

    private static async Task<int> BenchmarkAsync(
        Options options, TaskSuite suite, IReadOnlyList<BenchConfig> configs, IAgentRunner? agentOverride,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellation)
    {
        var agent = agentOverride ?? CliAgentRunner.Create(options);
        var agentVersion = agent is CliAgentRunner cli ? "aiagent " + await cli.DescribeAsync(cancellation) : "test agent";

        var outDir = Path.GetFullPath(options.OutDir);
        var storeFile = AttemptStore.FilePath(outDir);
        if (File.Exists(storeFile) && !options.Resume)
            throw new BenchException($"{storeFile} already exists. Use --resume to continue that run, or another --out folder. Results are never overwritten.");

        // The plan: seed by seed, task by task, configuration by configuration.
        var plan = new List<(BenchTask Task, BenchConfig Config, int Seed)>();
        for (var seed = 1; seed <= options.Seeds; seed++)
            foreach (var task in suite.Tasks)
                foreach (var config in configs)
                    plan.Add((task, config, seed));
        var planKeys = plan.Select(p => Attempt.KeyOf(p.Task.Id, p.Config.Name, p.Seed)).ToHashSet();

        var done = new Dictionary<string, Attempt>();
        foreach (var a in AttemptStore.Load(outDir))
            if (planKeys.Contains(a.Key)) done[a.Key] = a;

        var runner = new AttemptRunner(options, agent, suite);
        var started = done.Count > 0 ? done.Values.Min(a => a.StartedUtc) : DateTimeOffset.UtcNow;
        var interrupted = false;
        var index = 0;

        stdout.WriteLine($"{plan.Count} attempt(s) planned ({suite.Tasks.Count} task(s) x {configs.Count} configuration(s) x {options.Seeds} seed(s)); {done.Count} already done.");
        foreach (var (task, config, seed) in plan)
        {
            index++;
            if (done.ContainsKey(Attempt.KeyOf(task.Id, config.Name, seed))) continue;
            if (cancellation.IsCancellationRequested) { interrupted = true; break; }

            Attempt attempt;
            try { attempt = await runner.RunAsync(task, config, seed, cancellation); }
            catch (OperationCanceledException) { interrupted = true; break; }

            AttemptStore.Append(outDir, attempt);
            done[attempt.Key] = attempt;
            stdout.WriteLine($"[{index}/{plan.Count}] {task.Id} · {config.Name} · seed {seed}: {attempt.Status}" +
                             (attempt.CostUsd is { } usd ? $" · ${usd.ToString("0.000", CultureInfo.InvariantCulture)}" : "") +
                             $" · {attempt.AgentSeconds.ToString("0", CultureInfo.InvariantCulture)} s" +
                             (attempt.Note != null && attempt.Status is AttemptStatus.Error ? " · " + ProcessRunner.Tail(attempt.Note, 160) : ""));
        }

        var attempts = done.Values.ToList();
        var context = new ReportContext(suite, options, configs, suite.Tasks.Select(t => t.Id).ToList(), plan.Count, started, DateTimeOffset.UtcNow,
            agentVersion, HarnessDescription());

        Directory.CreateDirectory(outDir);
        var csvPath = Path.Combine(outDir, "attempts.csv");
        var reportPath = Path.Combine(outDir, "report.md");
        await File.WriteAllTextAsync(csvPath, ReportBuilder.Csv(attempts), CancellationToken.None);
        await File.WriteAllTextAsync(reportPath, ReportBuilder.Markdown(context, attempts), CancellationToken.None);

        stdout.WriteLine();
        foreach (var c in configs)
        {
            var s = Summarizer.Summarize(c.Name, attempts);
            stdout.WriteLine($"{c.Name,-24} {s.Passed}/{s.Attempts} passed" +
                             (s.Attempts > 0 ? $" ({s.PassRate.ToString("P1", CultureInfo.InvariantCulture)})" : "") +
                             $" · cost ${s.TotalCostUsd.ToString("0.00", CultureInfo.InvariantCulture)}");
        }
        stdout.WriteLine($"Report: {reportPath}");
        stdout.WriteLine($"CSV:    {csvPath}");
        if (attempts.Count < plan.Count)
        {
            stderr.WriteLine($"Incomplete: {attempts.Count} of {plan.Count} attempts finished{(interrupted ? " (cancelled)" : "")}. Run again with --resume to finish.");
            return 1;
        }
        return 0;
    }

    private static string HarnessDescription()
    {
        var version = typeof(BenchApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        return $"tools/Bench {version} on {RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.FrameworkDescription}";
    }

    private sealed class NoAgent : IAgentRunner
    {
        public Task<AgentRun> RunAsync(AgentInvocation invocation, CancellationToken cancellation) =>
            throw new InvalidOperationException("No agent is used in this mode.");
    }
}
