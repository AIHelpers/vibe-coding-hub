using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Bench;

/// <summary>
/// One real model setup the agent must work against (E9-S2): a provider, a model, and where to reach it.
/// The scripted test provider is deliberately not allowed: the matrix exists to find what it cannot (real streaming,
/// tool calls, context limits).
/// </summary>
internal sealed record MatrixSetup
{
    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Endpoint of a self-hosted or third-party server. Required for local setups and for any provider other than openai, anthropic and ollama.</summary>
    public string? BaseUrl { get; init; }
    /// <summary>Name of the environment variable that holds the API key. The key itself is never written to the matrix file, the results or the report.</summary>
    public string? ApiKeyEnv { get; init; }
    /// <summary>A server on this machine or network: skipped when nothing answers at <see cref="BaseUrl"/>.</summary>
    public bool Local { get; init; }
    public string? Description { get; init; }

    public static readonly string[] BuiltInProviders = { "openai", "anthropic", "ollama" };
}

internal sealed record MatrixDefinition(string Name, IReadOnlyList<MatrixSetup> Setups);

internal static class MatrixLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record FileModel
    {
        public string? Name { get; init; }
        public List<MatrixSetup>? Setups { get; init; }
    }

    public static MatrixDefinition Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new BenchException($"Matrix file not found: {full}");
        FileModel? model;
        try { model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(full), JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new BenchException($"Cannot read the matrix file {full}: {ex.Message}", ex);
        }
        if (model?.Setups is not { Count: > 0 }) throw new BenchException($"{full}: the file has no setups.");
        Validate(model.Setups, full);
        return new MatrixDefinition(model.Name ?? Path.GetFileNameWithoutExtension(full), model.Setups);
    }

    internal static void Validate(IReadOnlyList<MatrixSetup> setups, string source)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in setups)
        {
            string Fail(string why) => $"{source}: setup '{s.Id}': {why}";
            if (!Regex.IsMatch(s.Id, "^[a-z0-9][a-z0-9-]*$")) throw new BenchException($"{source}: setup id '{s.Id}' must be lower-case letters, digits and '-'.");
            if (!ids.Add(s.Id)) throw new BenchException(Fail("the id is used twice."));
            if (!Regex.IsMatch(s.Provider, "^[a-z][a-z0-9]*$")) throw new BenchException(Fail("provider must be lower-case letters and digits (for example openai, ollama, lmstudio)."));
            if (s.Provider == "scripted") throw new BenchException(Fail("the scripted provider is a test double; the matrix is for real models."));
            if (string.IsNullOrWhiteSpace(s.Model)) throw new BenchException(Fail("model is required."));
            if (s.BaseUrl != null && !(Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")))
                throw new BenchException(Fail("baseUrl must be an absolute http or https address."));
            if (s.Local && s.BaseUrl == null) throw new BenchException(Fail("a local setup needs baseUrl."));
            if (!MatrixSetup.BuiltInProviders.Contains(s.Provider) && s.BaseUrl == null)
                throw new BenchException(Fail($"provider '{s.Provider}' is not built in, so baseUrl is required."));
            if (s.ApiKeyEnv != null && !Regex.IsMatch(s.ApiKeyEnv, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new BenchException(Fail("apiKeyEnv must be the NAME of an environment variable, never the key itself."));
            if (s.ApiKeyEnv == null && !s.Local && (s.Provider == "openai" || s.Provider == "anthropic"))
                throw new BenchException(Fail($"{s.Provider} needs apiKeyEnv."));
        }
    }
}

/// <summary>Decides whether a setup can be tested at all on this machine. A setup that cannot is reported as skipped, never as failed.</summary>
internal sealed class MatrixAvailability
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Func<string, string?> _environment;
    private readonly Func<Uri, CancellationToken, Task<bool>> _reachable;

    public MatrixAvailability(Func<string, string?>? environment = null, Func<Uri, CancellationToken, Task<bool>>? reachable = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _reachable = reachable ?? Reach;
    }

    public string? KeyOf(MatrixSetup setup) =>
        setup.ApiKeyEnv != null && _environment(setup.ApiKeyEnv) is { Length: > 0 } key ? key : null;

    /// <summary>Null when the setup can run, otherwise the reason it cannot.</summary>
    public async Task<string?> CheckAsync(MatrixSetup setup, CancellationToken cancellation)
    {
        if (setup.ApiKeyEnv != null && KeyOf(setup) == null)
            return $"environment variable {setup.ApiKeyEnv} is not set";
        if (setup.Local && !await _reachable(new Uri(setup.BaseUrl!), cancellation))
            return $"nothing answers at {setup.BaseUrl} (start the server and load the model)";
        return null;
    }

    private static async Task<bool> Reach(Uri uri, CancellationToken cancellation)
    {
        try
        {
            // Any HTTP answer means a server is there; LM Studio answers 404 on the root, Ollama 200.
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return true;
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !cancellation.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>An agent runner that can set up an isolated agent home for a matrix setup (provider endpoint, flow templates).</summary>
internal interface ISetupPreparer
{
    /// <summary>Null when ready, otherwise what went wrong.</summary>
    Task<string?> PrepareAsync(MatrixSetup setup, IReadOnlyList<BenchConfig> configs, CancellationToken cancellation);
}

/// <summary>Removes a secret value from whatever the agent reported, so a key echoed in an error message cannot reach the results.</summary>
internal sealed class RedactingAgentRunner : IAgentRunner
{
    private readonly IAgentRunner _inner;
    private readonly string _secret;

    public RedactingAgentRunner(IAgentRunner inner, string secret)
    {
        _inner = inner;
        _secret = secret;
    }

    public async Task<AgentRun> RunAsync(AgentInvocation invocation, CancellationToken cancellation)
    {
        var run = await _inner.RunAsync(invocation, cancellation);
        return run.Note == null ? run : run with { Note = run.Note.Replace(_secret, "***", StringComparison.Ordinal) };
    }
}

internal enum MatrixState { Tested, Skipped, SetupFailed }

internal sealed record MatrixRow(MatrixSetup Setup, MatrixState State, string? Reason, IReadOnlyList<Attempt> Attempts)
{
    /// <summary>Every attempt ended with a verdict and the agent exited cleanly: nothing to investigate. This says nothing about model quality.</summary>
    public bool Clean => State == MatrixState.Tested && Attempts.Count > 0 && Attempts.All(IsClean);

    public static bool IsClean(Attempt a) =>
        (a.Status == AttemptStatus.Pass || a.Status == AttemptStatus.Fail) && (a.AgentExitCode ?? 0) == 0;
}

/// <summary>E9-S2: the same task and flows against several real setups, one table of what works.</summary>
internal static class MatrixApp
{
    public static readonly string[] DefaultConfigs = { "single", "bug-fix-flow", "feature-team-flow" };
    public const string DefaultOutDir = "bench/matrix-results";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> RunAsync(
        Options options, TaskSuite suite, IReadOnlyList<BenchConfig> configs, Func<Options, IAgentRunner>? agentFactory,
        MatrixAvailability availability, TextWriter stdout, TextWriter stderr, CancellationToken cancellation)
    {
        var definition = MatrixLoader.Load(options.Matrix!);
        foreach (var wanted in options.Setups)
            if (!definition.Setups.Any(s => s.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                throw new BenchException($"--setups: '{wanted}' is not in the matrix file. Known: {string.Join(", ", definition.Setups.Select(s => s.Id))}.");
        var setups = definition.Setups
            .Where(s => options.Setups.Count == 0 || options.Setups.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        var outDir = Path.GetFullPath(options.OutDir == Options.DefaultOutDir ? DefaultOutDir : options.OutDir);
        var reportPath = Path.Combine(outDir, "matrix.md");
        if (File.Exists(reportPath) && !options.Resume)
            throw new BenchException($"{reportPath} already exists. Use --resume to continue that run, or another --out folder. Results are never overwritten.");
        Directory.CreateDirectory(outDir);

        var seeds = options.SeedsGiven ? options.Seeds : 1;
        var baseOptions = options with { Seeds = seeds };
        Func<Options, IAgentRunner> factory = agentFactory ?? (o => CliAgentRunner.Create(o));
        var probe = factory(baseOptions); // fails early when the CLI cannot be found
        var agentVersion = probe is CliAgentRunner cli ? "aiagent " + await cli.DescribeAsync(cancellation) : "test agent";

        var started = DateTimeOffset.UtcNow;
        var rows = new List<MatrixRow>();
        var interrupted = false;
        stdout.WriteLine($"{setups.Count} setup(s) x {suite.Tasks.Count} task(s) x {configs.Count} configuration(s) x {seeds} seed(s).");
        foreach (var setup in setups)
        {
            if (cancellation.IsCancellationRequested) { interrupted = true; break; }
            var setupOut = Path.Combine(outDir, setup.Id);
            var reason = await availability.CheckAsync(setup, cancellation);
            if (reason != null)
            {
                rows.Add(new MatrixRow(setup, MatrixState.Skipped, reason, Array.Empty<Attempt>()));
                stdout.WriteLine($"{setup.Id,-18} skipped: {reason}");
                continue;
            }

            var key = availability.KeyOf(setup);
            var env = new Dictionary<string, string?>();
            if (key != null) env[$"AI_CODE_AGENT_{setup.Provider.ToUpperInvariant()}_API_KEY"] = key;
            var setupOptions = baseOptions with
            {
                Provider = setup.Provider, Model = setup.Model, OutDir = setupOut,
                AgentHome = Path.Combine(setupOut, "home"), AgentEnvironment = env
            };

            var agent = factory(setupOptions);
            if (agent is ISetupPreparer preparer)
            {
                var problem = await preparer.PrepareAsync(setup, configs, cancellation);
                if (problem != null)
                {
                    if (key != null) problem = problem.Replace(key, "***", StringComparison.Ordinal);
                    rows.Add(new MatrixRow(setup, MatrixState.SetupFailed, problem, Array.Empty<Attempt>()));
                    stdout.WriteLine($"{setup.Id,-18} setup failed: {ProcessRunner.Tail(problem, 160)}");
                    continue;
                }
            }
            if (key != null) agent = new RedactingAgentRunner(agent, key);

            stdout.WriteLine($"{setup.Id,-18} running ({setup.Provider} / {setup.Model})");
            await BenchApp.BenchmarkAsync(setupOptions, suite, configs, agent, TextWriter.Null, TextWriter.Null, cancellation);
            var attempts = AttemptStore.Load(setupOut);
            rows.Add(new MatrixRow(setup, MatrixState.Tested, null, attempts));
            stdout.WriteLine($"{setup.Id,-18} {Describe(attempts)}");
            if (cancellation.IsCancellationRequested) { interrupted = true; break; }
        }

        var finished = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(reportPath, Markdown(definition.Name, suite, configs, seeds, rows, agentVersion, started, finished, interrupted), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(outDir, "matrix.csv"), Csv(rows), CancellationToken.None);
        stdout.WriteLine();
        stdout.WriteLine($"Report: {reportPath}");

        if (interrupted)
        {
            stderr.WriteLine("Cancelled. Run again with --resume to finish.");
            return 1;
        }
        if (rows.All(r => r.State != MatrixState.Tested))
        {
            stderr.WriteLine("No setup could be tested. See the report for the reason of each.");
            return 1;
        }
        return 0;
    }

    private static string Describe(IReadOnlyList<Attempt> attempts) =>
        $"{attempts.Count(a => a.Passed)}/{attempts.Count} passed, {attempts.Count(a => !MatrixRow.IsClean(a))} need attention";

    internal static string CellText(MatrixRow row, BenchConfig config)
    {
        switch (row.State)
        {
            case MatrixState.Skipped: return "skipped";
            case MatrixState.SetupFailed: return "setup failed";
        }
        var mine = row.Attempts.Where(a => a.Config.Equals(config.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return "not run";
        var text = string.Join(", ", mine.GroupBy(a => a.Status).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => mine.Count == 1 ? g.Key : $"{g.Count()} {g.Key}"));
        var nonZero = mine.Any(a => (a.Status == AttemptStatus.Pass || a.Status == AttemptStatus.Fail) && (a.AgentExitCode ?? 0) != 0);
        return nonZero ? text + " (non-zero exit)" : text;
    }

    internal static string Verdict(MatrixRow row) => row.State switch
    {
        MatrixState.Skipped => "not tested",
        MatrixState.SetupFailed => "setup failed",
        _ => row.Clean ? "clean" : "needs attention"
    };

    internal static string Markdown(string name, TaskSuite suite, IReadOnlyList<BenchConfig> configs, int seeds, IReadOnlyList<MatrixRow> rows,
        string agentVersion, DateTimeOffset started, DateTimeOffset finished, bool interrupted)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Real-model matrix: {name}");
        sb.AppendLine();
        sb.AppendLine($"Run {started.ToString("yyyy-MM-dd HH:mm", Inv)} to {finished.ToString("HH:mm", Inv)} UTC with {agentVersion}.");
        if (interrupted) sb.AppendLine().AppendLine("> **Incomplete:** the run was cancelled. Continue it with `--resume`.");
        sb.AppendLine();
        sb.AppendLine("The question here is whether the agent WORKS against each real setup (streaming, tool calls, long context), not how well the model solves the task. " +
                      "`clean` means every attempt ended with a pass or fail verdict and the agent exited with code 0. `needs attention` means an error, a timeout or a non-zero exit, " +
                      "listed under Findings. A `fail` is a model result, not a bug. A skipped setup was not tested at all.");
        sb.AppendLine();
        sb.AppendLine("| Setup | " + string.Join(" | ", configs.Select(c => c.Name)) + " | Verdict |");
        sb.AppendLine("|---|" + string.Concat(configs.Select(_ => "---|")) + "---|");
        foreach (var row in rows)
            sb.AppendLine($"| {row.Setup.Id} | " + string.Join(" | ", configs.Select(c => CellText(row, c))) + $" | {Verdict(row)} |");

        var findings = new List<string>();
        foreach (var row in rows)
        {
            if (row.State != MatrixState.Tested)
                findings.Add($"- **{row.Setup.Id}**: {(row.State == MatrixState.Skipped ? "skipped" : "setup failed")}: {Cell(row.Reason ?? "")}");
            foreach (var a in row.Attempts.Where(a => !MatrixRow.IsClean(a)))
                findings.Add($"- **{row.Setup.Id}** · {a.Config} · seed {a.Seed}: {a.Status}" +
                             (a.AgentExitCode is { } code && code != 0 ? $", exit code {code}" : "") +
                             (string.IsNullOrWhiteSpace(a.Note) ? "" : ": " + Cell(ProcessRunner.Tail(a.Note, 300))));
        }
        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (findings.Count == 0) sb.AppendLine("None: every tested setup ran clean.");
        else foreach (var f in findings) sb.AppendLine(f);

        sb.AppendLine();
        sb.AppendLine("## Cost and time");
        sb.AppendLine();
        sb.AppendLine("| Setup | Attempts | Passed | Tokens | Cost (USD) | Agent time |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var row in rows.Where(r => r.State == MatrixState.Tested))
        {
            var cost = row.Attempts.Sum(a => a.CostUsd ?? 0m);
            var unpriced = row.Attempts.Any(a => a.Unpriced) ? " (some unpriced)" : "";
            sb.AppendLine($"| {row.Setup.Id} | {row.Attempts.Count} | {row.Attempts.Count(a => a.Passed)} | {row.Attempts.Sum(a => a.Tokens ?? 0)} | " +
                          $"{cost.ToString("0.00", Inv)}{unpriced} | {row.Attempts.Sum(a => a.AgentSeconds).ToString("0", Inv)} s |");
        }

        sb.AppendLine();
        sb.AppendLine("## Setups");
        sb.AppendLine();
        foreach (var row in rows)
        {
            var s = row.Setup;
            sb.AppendLine($"- **{s.Id}**: provider `{s.Provider}`, model `{s.Model}`" + (s.BaseUrl != null ? $", endpoint {s.BaseUrl}" : "") +
                          (s.ApiKeyEnv != null ? $", key from `{s.ApiKeyEnv}`" : "") + (string.IsNullOrWhiteSpace(s.Description) ? "" : $". {s.Description}"));
        }

        sb.AppendLine();
        sb.AppendLine("## Method");
        sb.AppendLine();
        sb.AppendLine($"Suite: {suite.Name} ({string.Join(", ", suite.Tasks.Select(t => t.Id))}). {seeds} seed(s) per task and configuration. " +
                      "Each setup runs with its own empty agent home, so nothing from the tester's own configuration leaks in. API keys are read from the environment and never stored. " +
                      "Per-setup details: `<setup>/report.md`, `<setup>/attempts.csv`.");
        return sb.ToString();
    }

    internal static string Csv(IReadOnlyList<MatrixRow> rows)
    {
        var sb = new StringBuilder("setup,provider,model,config,seed,status,agent_exit_code,tokens,cost_usd,agent_seconds,clean,note\n");
        foreach (var row in rows)
        {
            var s = row.Setup;
            if (row.State != MatrixState.Tested)
            {
                sb.Append(string.Join(",", ReportBuilder.Field(s.Id), ReportBuilder.Field(s.Provider), ReportBuilder.Field(s.Model), "", "",
                    row.State == MatrixState.Skipped ? "skipped" : "setup-failed", "", "", "", "", "false", ReportBuilder.Field(row.Reason ?? ""))).Append('\n');
                continue;
            }
            foreach (var a in row.Attempts)
                sb.Append(string.Join(",", ReportBuilder.Field(s.Id), ReportBuilder.Field(s.Provider), ReportBuilder.Field(s.Model),
                    ReportBuilder.Field(a.Config), a.Seed.ToString(Inv), a.Status, a.AgentExitCode?.ToString(Inv) ?? "", a.Tokens?.ToString(Inv) ?? "",
                    a.CostUsd?.ToString(Inv) ?? "", a.AgentSeconds.ToString(Inv), MatrixRow.IsClean(a) ? "true" : "false", ReportBuilder.Field(a.Note ?? ""))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "/");
}
