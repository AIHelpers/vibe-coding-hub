using System.Runtime.InteropServices;

namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Where the app writes its log files: a per-user folder, never a folder relative to the
/// working directory, so starting the app in a repository no longer creates <c>logs/</c> there.
/// </summary>
public static class LogPathResolver
{
    /// <summary>Environment variable that overrides the log folder.</summary>
    public const string LogDirEnvironmentVariable = "AIAGENT_LOG_DIR";

    /// <summary>Log file name pattern; the file sink inserts the date.</summary>
    public const string FileNamePattern = "aiagent-.log";

    /// <summary>Daily log files kept; the file sink deletes older ones.</summary>
    public const int RetainedFileCount = 14;

    /// <summary>The log folder for the operating system this process runs on.</summary>
    public static string ResolveDefault()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
               : OSPlatform.Linux;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Resolve(os, name => Environment.GetEnvironmentVariable(name), home);
    }

    /// <summary>
    /// The resolution rule behind <see cref="ResolveDefault"/>. The OS and the environment are
    /// parameters so each platform can be tested on any machine.
    /// </summary>
    public static string Resolve(OSPlatform os, Func<string, string?> env, string? userHome)
    {
        var overrideDir = env(LogDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(overrideDir.Trim());

        var home = NonBlank(userHome);
        if (os == OSPlatform.Windows)
        {
            var localAppData = NonBlank(env("LOCALAPPDATA"))
                ?? (home == null ? null : Path.Combine(home, "AppData", "Local"));
            if (localAppData != null)
                return Path.Combine(localAppData, "VibeCodingHub", "logs");
        }
        else if (os == OSPlatform.OSX)
        {
            if (home != null)
                return Path.Combine(home, "Library", "Logs", "VibeCodingHub");
        }
        else
        {
            // XDG says XDG_STATE_HOME must be absolute; a relative value is ignored.
            var xdgState = NonBlank(env("XDG_STATE_HOME"));
            if (xdgState != null && Path.IsPathRooted(xdgState))
                return Path.Combine(xdgState, "vibe-coding-hub", "logs");
            if (home != null)
                return Path.Combine(home, ".local", "state", "vibe-coding-hub", "logs");
        }

        // No home folder at all: a temp folder still keeps logs out of the project.
        return Path.Combine(Path.GetTempPath(), "vibe-coding-hub", "logs");
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
