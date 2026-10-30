namespace AiCodeAgent.Screenshots;

/// <summary>Renders ONE theme pass in this process (Avalonia can only be started once per process).</summary>
internal static class Worker
{
    public static int Run(Options options, IReadOnlyList<SceneInfo> selected)
    {
        var theme = options.Theme == ThemeSelection.Light ? "light" : "dark";
        var outDirectory = Path.GetFullPath(options.OutDirectory);
        Directory.CreateDirectory(outDirectory);

        Workspace workspace;
        try
        {
            workspace = Workspace.CreateAsync(options, theme).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{theme}] cannot prepare the scratch workspace: {ex.Message}");
            return 1;
        }

        var exitCode = 1;
        try
        {
            Directory.SetCurrentDirectory(workspace.Project);
            exitCode = HeadlessHost.Run(() => SceneRunner.RunAsync(options, workspace, theme, outDirectory, selected));
        }
        finally
        {
            if (exitCode == 0 && !options.KeepWorkspace)
                workspace.Delete();
            else
                Console.Error.WriteLine($"[{theme}] scratch workspace kept at {workspace.Root}");
        }

        return exitCode;
    }
}
