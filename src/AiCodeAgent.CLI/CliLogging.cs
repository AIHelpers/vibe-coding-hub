using AiCodeAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AiCodeAgent.CLI;

/// <summary>
/// CLI logging. Log lines go to the per-user log folder. Console output (stderr only) appears
/// only with <c>--verbose</c>, so normal runs never print log lines into stdout.
/// </summary>
internal static class CliLogging
{
    public static void Configure(ILoggingBuilder builder, bool verbose)
    {
        var file = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(LogPathResolver.ResolveDefault(), LogPathResolver.FileNamePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: LogPathResolver.RetainedFileCount)
            .CreateLogger();
        builder.AddSerilog(file, dispose: true);

        if (verbose)
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}
