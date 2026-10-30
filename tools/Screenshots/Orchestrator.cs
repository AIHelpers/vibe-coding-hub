using System.Diagnostics;
using System.Reflection;

namespace AiCodeAgent.Screenshots;

/// <summary>
/// The default command: starts one worker process per theme (dark -> the output folder, light -> its light/ subfolder)
/// and, with --check, runs everything twice and compares the two runs.
/// </summary>
internal static class Orchestrator
{
    public static int Generate(Options options)
    {
        var outDirectory = Path.GetFullPath(options.OutDirectory);
        var code = 0;
        foreach (var (theme, folder) in Passes(options, outDirectory))
            code |= RunWorker(options, theme, folder);

        if (code == 0)
            Console.WriteLine("Done. Next: git diff --stat docs/content-kit/images, then commit the images you want to publish.");
        return code;
    }

    public static int Check(Options options)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "vch-screenshots-check");
        Workspace.DeleteDirectory(baseDirectory);
        var first = Path.Combine(baseDirectory, "run1");
        var second = Path.Combine(baseDirectory, "run2");

        Console.WriteLine("Determinism check: rendering twice (this takes a few minutes).");
        foreach (var folder in new[] { first, second })
        {
            Console.WriteLine($"--- run {(folder == first ? 1 : 2)} of 2");
            var run = Generate(Clone(options, folder));
            if (run != 0)
            {
                Console.Error.WriteLine("A run failed, so the two runs cannot be compared.");
                return 1;
            }
        }

        var failed = false;
        foreach (var (theme, _) in Passes(options, first))
        {
            var sub = theme == "light" ? "light" : string.Empty;
            var results = ImageComparer.CompareFolders(Path.Combine(first, sub), Path.Combine(second, sub), options.ThresholdPercent);
            Console.WriteLine();
            Console.WriteLine($"{theme} set (limit {options.ThresholdPercent:0.###} % of pixels):");
            foreach (var result in results)
            {
                Console.WriteLine($"  {(result.Passed ? "ok  " : "FAIL")} {result.Name,-32} {result.Verdict}");
                failed |= !result.Passed;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed ? "Not deterministic enough: see the FAIL lines above." : "Deterministic: both runs match within the limit.");
        return failed ? 1 : 0;
    }

    /// <summary>(theme, output folder) of every pass the options ask for.</summary>
    internal static IReadOnlyList<(string Theme, string Folder)> Passes(Options options, string outDirectory)
    {
        var passes = new List<(string, string)>();
        if (options.Theme is ThemeSelection.Dark or ThemeSelection.Both) passes.Add(("dark", outDirectory));
        if (options.Theme is ThemeSelection.Light or ThemeSelection.Both) passes.Add(("light", Path.Combine(outDirectory, "light")));
        return passes;
    }

    private static Options Clone(Options options, string outDirectory)
    {
        var copy = new Options
        {
            OutDirectory = outDirectory,
            Theme = options.Theme,
            FastChecks = options.FastChecks,
            DelayScale = options.DelayScale,
            SceneTimeoutSeconds = options.SceneTimeoutSeconds,
            RepoRoot = options.RepoRoot,
            KeepWorkspace = options.KeepWorkspace,
            ThresholdPercent = options.ThresholdPercent,
        };
        copy.Only.AddRange(options.Only);
        return copy;
    }

    private static int RunWorker(Options options, string theme, string folder)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find this program's own path."))
        {
            UseShellExecute = false,
        };

        // `dotnet run` starts the dll through the dotnet host: the dll path must come first.
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot find the tool's dll."));

        info.ArgumentList.Add("--worker");
        info.ArgumentList.Add("--theme"); info.ArgumentList.Add(theme);
        info.ArgumentList.Add("--out"); info.ArgumentList.Add(folder);
        info.ArgumentList.Add("--delay-scale"); info.ArgumentList.Add(options.DelayScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--timeout"); info.ArgumentList.Add(options.SceneTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.FastChecks) info.ArgumentList.Add("--fast-checks");
        if (options.KeepWorkspace) info.ArgumentList.Add("--keep-workspace");
        if (!string.IsNullOrWhiteSpace(options.RepoRoot)) { info.ArgumentList.Add("--repo"); info.ArgumentList.Add(options.RepoRoot); }
        if (options.Only.Count > 0) { info.ArgumentList.Add("--only"); info.ArgumentList.Add(string.Join(',', options.Only)); }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start the worker process.");
        process.WaitForExit();
        return process.ExitCode == 0 ? 0 : 1;
    }
}
