using System.Globalization;

namespace AiCodeAgent.Bench;

internal sealed record Options
{
    public string TasksPath { get; init; } = Path.Combine("bench", "tasks.json");
    public IReadOnlyList<string> Configs { get; init; } = new[] { "single", "bug-fix-flow" };
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public int Seeds { get; init; } = 3;
    public string OutDir { get; init; } = Path.Combine("bench", "results");
    public string? WorkDir { get; init; }
    public string? AgentCommand { get; init; }
    public string? AgentHome { get; init; }
    public int TimeoutSeconds { get; init; } = 1800;
    public int TestTimeoutSeconds { get; init; } = 900;
    public decimal? MaxBudgetUsd { get; init; }
    public IReadOnlyList<string> Only { get; init; } = Array.Empty<string>();
    public bool Resume { get; init; }
    public bool Validate { get; init; }
    public bool List { get; init; }
    public bool KeepWorkspaces { get; init; }

    public string ResolvedWorkDir => Path.GetFullPath(WorkDir ?? Path.Combine(Path.GetTempPath(), "vch-bench"));

    public const string HelpText = """
        Benchmark harness: the same tasks solved by a single agent and by flows (E8-S3).

        Usage: dotnet run --project tools/Bench -- --tasks bench/tasks.json --configs single,bug-fix-flow --model <id> [options]

          --tasks <file>          Task list (default bench/tasks.json).
          --configs <a,b,...>     What to compare: single, <pipeline>-flow (bug-fix-flow, feature-team-flow) or flow:<pipeline>.
                                  Default single,bug-fix-flow.
          --model <id>            Model for every configuration (required, except with --list).
          --provider <name>       Provider for every configuration (default: the agent's configured one).
          --seeds <n>             Attempts per task and configuration (default 3).
          --out <dir>             Where attempts.jsonl, attempts.csv and report.md go (default bench/results).
          --resume                Continue a stopped run: attempts already in <out>/attempts.jsonl are kept, not repeated.
          --only <id,id,...>      Run only these task ids (a subset is stated in the report).
          --max-budget <usd>      Spend cap per flow attempt (flows only; a single agent has no cap, only the timeout).
          --timeout <seconds>     Wall-clock limit for the agent in one attempt (default 1800).
          --test-timeout <sec>    Limit for the hidden test command (default 900).
          --agent <path>          The aiagent executable or aiagent.dll (default: the build under src/AiCodeAgent.CLI/bin).
          --agent-home <dir>      AIAGENT_HOME for the agent (settings, API keys, run reports). Default: the current one.
          --work-dir <dir>        Scratch folder for workspaces and git clones (default %TEMP%/vch-bench).
          --keep-workspaces       Keep each attempt's workspace for inspection.
          --validate              Check the tasks only: the hidden test must FAIL on the untouched snapshot. No agent, no cost.
          --list                  Print the tasks and exit.
          --help                  This text.
        """;
}

internal sealed record OptionsResult(Options? Options, string? Error, bool ShowHelp);

internal static class OptionsParser
{
    public static OptionsResult Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0) { inline = arg[(eq + 1)..]; arg = arg[..eq]; }

            string? Value(out string? error)
            {
                error = null;
                if (inline != null) return inline;
                if (i + 1 >= args.Length) { error = $"{arg} needs a value."; return null; }
                return args[++i];
            }

            string? err;
            switch (arg)
            {
                case "--help" or "-h" or "-?":
                    return new OptionsResult(null, null, true);
                case "--resume": o = o with { Resume = true }; break;
                case "--validate": o = o with { Validate = true }; break;
                case "--list": o = o with { List = true }; break;
                case "--keep-workspaces": o = o with { KeepWorkspaces = true }; break;
                case "--tasks": { var v = Value(out err); if (err != null) return Fail(err); o = o with { TasksPath = v! }; break; }
                case "--configs": { var v = Value(out err); if (err != null) return Fail(err); o = o with { Configs = Split(v!) }; break; }
                case "--only": { var v = Value(out err); if (err != null) return Fail(err); o = o with { Only = Split(v!) }; break; }
                case "--model": { var v = Value(out err); if (err != null) return Fail(err); o = o with { Model = v }; break; }
                case "--provider": { var v = Value(out err); if (err != null) return Fail(err); o = o with { Provider = v }; break; }
                case "--out": { var v = Value(out err); if (err != null) return Fail(err); o = o with { OutDir = v! }; break; }
                case "--work-dir": { var v = Value(out err); if (err != null) return Fail(err); o = o with { WorkDir = v }; break; }
                case "--agent": { var v = Value(out err); if (err != null) return Fail(err); o = o with { AgentCommand = v }; break; }
                case "--agent-home": { var v = Value(out err); if (err != null) return Fail(err); o = o with { AgentHome = v }; break; }
                case "--seeds": { var v = Value(out err); if (err != null) return Fail(err); if (!TryInt(v, 1, 20, out var n)) return Fail("--seeds must be a whole number from 1 to 20."); o = o with { Seeds = n }; break; }
                case "--timeout": { var v = Value(out err); if (err != null) return Fail(err); if (!TryInt(v, 10, 86_400, out var n)) return Fail("--timeout must be a number of seconds from 10 to 86400."); o = o with { TimeoutSeconds = n }; break; }
                case "--test-timeout": { var v = Value(out err); if (err != null) return Fail(err); if (!TryInt(v, 10, 86_400, out var n)) return Fail("--test-timeout must be a number of seconds from 10 to 86400."); o = o with { TestTimeoutSeconds = n }; break; }
                case "--max-budget":
                {
                    var v = Value(out err); if (err != null) return Fail(err);
                    if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var usd) || usd <= 0) return Fail("--max-budget must be a positive number of US dollars.");
                    o = o with { MaxBudgetUsd = usd };
                    break;
                }
                default:
                    return Fail($"Unknown option '{arg}'. Use --help.");
            }
        }

        if (!o.List && !o.Validate && string.IsNullOrWhiteSpace(o.Model))
            return Fail("--model is required: every configuration must run on the same model.");
        return new OptionsResult(o, null, false);

        static OptionsResult Fail(string message) => new(null, message, false);
        static IReadOnlyList<string> Split(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static bool TryInt(string? text, int min, int max, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }
}
