using System.Diagnostics;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiCodeAgent.Screenshots;

/// <summary>Waits for the app to come up in the scratch workspace, then renders the selected scenes one by one.</summary>
internal static class SceneRunner
{
    /// <returns>0 when every scene produced its image, 1 otherwise.</returns>
    public static async Task<int> RunAsync(Options options, Workspace workspace, string theme, string outDirectory, IReadOnlyList<SceneInfo> selected)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                       ?? throw new InvalidOperationException("The application did not start with a desktop lifetime.");

        var (window, main) = await WaitForWindowAsync(lifetime, workspace).ConfigureAwait(true);
        var context = new SceneContext(options, workspace, theme, outDirectory, window, main);
        await context.SettleAsync(500).ConfigureAwait(true);

        var plan = Scenes.Plan(selected);
        var failures = new List<string>();
        Console.WriteLine($"[{theme}] {plan.Count} scene(s) -> {outDirectory}");

        foreach (var (info, run) in plan)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                await run(context).WaitAsync(TimeSpan.FromSeconds(options.SceneTimeoutSeconds)).ConfigureAwait(true);
                Console.WriteLine($"[{theme}] {info.FileName,-30} ok      {clock.Elapsed.TotalSeconds,5:0.0} s");
            }
            catch (Exception ex)
            {
                failures.Add(info.FileName);
                Console.Error.WriteLine($"[{theme}] {info.FileName,-30} FAILED  {ex.Message}");
                context.SaveFailureFrame(info);
                await TryRecoverAsync(context).ConfigureAwait(true);
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"[{theme}] all {plan.Count} scene(s) rendered.");
            return 0;
        }

        Console.Error.WriteLine($"[{theme}] {failures.Count} scene(s) failed: {string.Join(", ", failures)}");
        Console.Error.WriteLine($"[{theme}] what the window showed at each failure: {Path.Combine(workspace.Root, "failures")}");
        return 1;
    }

    private static async Task<(Window Window, MainViewModel Main)> WaitForWindowAsync(IClassicDesktopStyleApplicationLifetime lifetime, Workspace workspace)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var window = lifetime.MainWindow;
            if (window?.DataContext is MainViewModel main
                && string.Equals(Path.GetFullPath(main.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(workspace.Project).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && main.SampleGuide != null)
            {
                window.SizeToContent = SizeToContent.Manual;
                window.WindowState = WindowState.Normal;
                window.Width = SceneCatalog.Width;
                window.Height = SceneCatalog.Height;
                return (window, main);
            }

            if (clock.Elapsed > TimeSpan.FromSeconds(90))
                throw new TimeoutException("The main window did not open the sample project in time.");
            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    /// <summary>After a failed scene: back to the plain chat so the next scene starts from a known place.</summary>
    private static async Task TryRecoverAsync(SceneContext context)
    {
        try
        {
            if (context.Chat.ShowApprovalDialog) context.Chat.DenyToolCommand.Execute(null);
            await context.ShowChatAsync().ConfigureAwait(true);
        }
        catch
        {
            // the next scene reports its own problem
        }
    }
}
