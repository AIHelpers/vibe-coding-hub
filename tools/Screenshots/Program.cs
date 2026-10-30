namespace AiCodeAgent.Screenshots;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args, out var error);
        if (error != null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.HelpText);
            return 0;
        }

        if (options.List)
        {
            PrintScenes();
            return 0;
        }

        if (options.CompareA != null && options.CompareB != null)
            return Compare(options);

        var selected = SceneCatalog.Select(options.Only, out var unknown);
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown scene(s): {string.Join(", ", unknown)}. Use --list to see them.");
            return 2;
        }

        try
        {
            if (options.Check) return Orchestrator.Check(options);
            return options.Worker ? Worker.Run(options, selected) : Orchestrator.Generate(options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintScenes()
    {
        Console.WriteLine($"{SceneCatalog.All.Count} screenshots, {SceneCatalog.Width}x{SceneCatalog.Height}:");
        foreach (var scene in SceneCatalog.All)
            Console.WriteLine($"  {scene.FileName,-30} {scene.Title}");
    }

    private static int Compare(Options options)
    {
        var results = ImageComparer.CompareFolders(options.CompareA!, options.CompareB!, options.ThresholdPercent);
        if (results.Count == 0)
        {
            Console.Error.WriteLine("No PNG files found in either folder.");
            return 1;
        }

        foreach (var result in results)
            Console.WriteLine($"{(result.Passed ? "ok  " : "FAIL")} {result.Name,-32} {result.Verdict}");
        return results.All(r => r.Passed) ? 0 : 1;
    }
}
