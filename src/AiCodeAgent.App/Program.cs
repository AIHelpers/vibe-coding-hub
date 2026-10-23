using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Runtime.InteropServices;
using Serilog;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The one Serilog configuration for the desktop app. Logs go to the per-user log folder,
        // never to a relative "logs/" folder in whatever directory the app was started from.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(System.IO.Path.Combine(LogPathResolver.ResolveDefault(), LogPathResolver.FileNamePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: LogPathResolver.RetainedFileCount)
            .CreateLogger();
        CrashReporter.InstallProcessHandlers();

        try
        {
            Log.Information("Starting AiCodeAgent.App");
            Log.Information("OS: {OS}", RuntimeInformation.OSDescription);
            Log.Information("Arch: {Arch}", RuntimeInformation.ProcessArchitecture);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI();
}
