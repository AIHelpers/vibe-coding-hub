using System.Globalization;

namespace AiCodeAgent.Screenshots;

/// <summary>Which theme pass(es) to render.</summary>
internal enum ThemeSelection
{
    Dark,
    Light,
    Both
}

/// <summary>Command line of the screenshot tool.</summary>
internal sealed class Options
{
    public const string DefaultOutDirectory = "docs/content-kit/images";
    public const double DefaultThresholdPercent = 0.5;

    /// <summary>Where the dark set goes; the light set goes to its <c>light/</c> subfolder.</summary>
    public string OutDirectory { get; set; } = DefaultOutDirectory;

    public ThemeSelection Theme { get; set; } = ThemeSelection.Both;

    /// <summary>Scene filters: a number (<c>5</c> or <c>05</c>), a slug (<c>approval</c>) or a file name. Empty = all.</summary>
    public List<string> Only { get; } = new();

    public bool List { get; set; }
    public bool Help { get; set; }

    /// <summary>Render everything twice and compare the two runs (the determinism check of the acceptance criteria).</summary>
    public bool Check { get; set; }

    /// <summary>Compare two folders of images and exit.</summary>
    public string? CompareA { get; set; }
    public string? CompareB { get; set; }

    /// <summary>Largest share of differing pixels (percent) that still counts as "the same image".</summary>
    public double ThresholdPercent { get; set; } = DefaultThresholdPercent;

    /// <summary>Replace the sample's build and test commands with instant ones (offline, no .NET restore).</summary>
    public bool FastChecks { get; set; }

    /// <summary>Multiplier of the scripted provider's delays; larger = longer to catch mid-run states.</summary>
    public double DelayScale { get; set; } = 3;

    public int SceneTimeoutSeconds { get; set; } = 120;

    /// <summary>Repository root (where <c>examples/</c> lives). Default: found by walking up to <c>AiCodeAgent.slnx</c>.</summary>
    public string? RepoRoot { get; set; }

    /// <summary>Do not delete the scratch workspace afterwards (for debugging a failed scene).</summary>
    public bool KeepWorkspace { get; set; }

    /// <summary>Internal: this process renders ONE theme pass itself (the parent starts one worker per theme).</summary>
    public bool Worker { get; set; }

    public static Options Parse(IReadOnlyList<string> args, out string? error)
    {
        var options = new Options();
        error = null;

        string Next(ref int i, string name)
        {
            if (i + 1 >= args.Count) throw new ArgumentException($"{name} needs a value.");
            return args[++i];
        }

        try
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "-h": case "--help": case "/?": options.Help = true; break;
                    case "--list": options.List = true; break;
                    case "--check": options.Check = true; break;
                    case "--worker": options.Worker = true; break;
                    case "--fast-checks": options.FastChecks = true; break;
                    case "--keep-workspace": options.KeepWorkspace = true; break;
                    case "--out": options.OutDirectory = Next(ref i, arg); break;
                    case "--repo": options.RepoRoot = Next(ref i, arg); break;
                    case "--only":
                        foreach (var part in Next(ref i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            options.Only.Add(part);
                        break;
                    case "--theme":
                        options.Theme = Next(ref i, arg).ToLowerInvariant() switch
                        {
                            "dark" => ThemeSelection.Dark,
                            "light" => ThemeSelection.Light,
                            "both" => ThemeSelection.Both,
                            var other => throw new ArgumentException($"--theme must be dark, light or both (got '{other}').")
                        };
                        break;
                    case "--threshold":
                        options.ThresholdPercent = ParseNumber(Next(ref i, arg), arg, min: 0, max: 100);
                        break;
                    case "--delay-scale":
                        options.DelayScale = ParseNumber(Next(ref i, arg), arg, min: 0, max: 100);
                        break;
                    case "--timeout":
                        options.SceneTimeoutSeconds = (int)ParseNumber(Next(ref i, arg), arg, min: 5, max: 3600);
                        break;
                    case "--compare":
                        options.CompareA = Next(ref i, arg);
                        options.CompareB = Next(ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{arg}'. Use --help.");
                }
            }
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
        }

        return options;
    }

    private static double ParseNumber(string text, string name, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{name} must be a number between {min} and {max} (got '{text}').");
        return value;
    }

    public const string HelpText = """
        Vibe Coding Hub screenshot generator (E8-S2)

        dotnet run --project tools/Screenshots -- [options]

          --out <dir>           Output folder for the dark set (default: docs/content-kit/images).
                                The light set goes to <dir>/light.
          --theme <t>           dark | light | both (default: both).
          --only <list>         Only these scenes: numbers, names or file names, comma separated (e.g. 5,06,approval).
          --list                Print the scenes and exit.
          --check               Render everything twice and compare the runs; exit code 1 when any image differs
                                by more than --threshold.
          --compare <a> <b>     Compare two folders of PNG files and exit.
          --threshold <pct>     Largest share of differing pixels still counted as equal (default 0.5).
          --fast-checks         Use instant build/test commands in the sample instead of the real dotnet ones
                                (offline, no NuGet restore; the screenshots then show the stand-in commands).
          --delay-scale <x>     Multiplier of the scripted provider's delays (default 3).
          --timeout <seconds>   Time limit of one scene (default 120).
          --repo <dir>          Repository root (default: found from the tool's location or the current folder).
          --keep-workspace      Keep the scratch workspace in the temp folder.
          -h, --help            This text.
        """;
}
