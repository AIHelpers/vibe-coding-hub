using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Avalonia.Threading;

namespace AiCodeAgent.Screenshots;

/// <summary>
/// Starts the REAL application (<see cref="AiCodeAgent.App.App"/>, its DI container, main window and view models) on
/// Avalonia's headless platform with Skia drawing, so frames look like the desktop app and can be captured.
/// </summary>
internal static class HeadlessHost
{
    /// <summary>Runs <paramref name="body"/> on the UI thread once the application is up; returns its exit code.</summary>
    public static int Run(Func<Task<int>> body)
    {
        var builder = AppBuilder.Configure<AiCodeAgent.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .UseReactiveUI()
            .AfterSetup(setup => Dispatcher.UIThread.Post(() => { _ = Execute(body); }, DispatcherPriority.Background));

        return builder.StartWithClassicDesktopLifetime(Array.Empty<string>());
    }

    private static async Task Execute(Func<Task<int>> body)
    {
        var code = 1;
        try
        {
            code = await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("The screenshot run failed: " + ex);
            code = 1;
        }
        finally
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(code);
        }
    }
}
