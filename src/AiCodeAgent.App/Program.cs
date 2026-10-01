using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Runtime.InteropServices;
using Serilog;

namespace AiCodeAgent.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/aiagent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

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
