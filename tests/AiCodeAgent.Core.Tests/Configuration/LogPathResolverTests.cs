using System.Runtime.InteropServices;
using AiCodeAgent.Core.Configuration;
using Xunit;

namespace AiCodeAgent.Core.Tests.Configuration;

public class LogPathResolverTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void Windows_UsesLocalAppData()
    {
        var dir = LogPathResolver.Resolve(OSPlatform.Windows,
            Env(("LOCALAPPDATA", "C:\\Users\\u\\AppData\\Local")), "C:\\Users\\u");

        Assert.Equal(Path.Combine("C:\\Users\\u\\AppData\\Local", "VibeCodingHub", "logs"), dir);
    }

    [Fact]
    public void Windows_FallsBackToHomeAppDataLocal_WhenLocalAppDataIsMissing()
    {
        var dir = LogPathResolver.Resolve(OSPlatform.Windows, Env(), "/home/u");

        Assert.Equal(Path.Combine("/home/u", "AppData", "Local", "VibeCodingHub", "logs"), dir);
    }

    [Fact]
    public void MacOS_UsesLibraryLogs()
    {
        var dir = LogPathResolver.Resolve(OSPlatform.OSX, Env(), "/Users/u");

        Assert.Equal(Path.Combine("/Users/u", "Library", "Logs", "VibeCodingHub"), dir);
    }

    [Fact]
    public void Linux_UsesXdgStateHome_WhenAbsolute()
    {
        var dir = LogPathResolver.Resolve(OSPlatform.Linux, Env(("XDG_STATE_HOME", "/var/state/u")), "/home/u");

        Assert.Equal(Path.Combine("/var/state/u", "vibe-coding-hub", "logs"), dir);
    }

    [Fact]
    public void Linux_DefaultsToLocalState_WhenXdgIsUnsetOrRelative()
    {
        var expected = Path.Combine("/home/u", ".local", "state", "vibe-coding-hub", "logs");

        Assert.Equal(expected, LogPathResolver.Resolve(OSPlatform.Linux, Env(), "/home/u"));
        Assert.Equal(expected, LogPathResolver.Resolve(OSPlatform.Linux,
            Env(("XDG_STATE_HOME", "relative/state")), "/home/u"));
    }

    [Fact]
    public void OverrideVariable_WinsOnEveryOs()
    {
        var target = Path.Combine(Path.GetTempPath(), "aiagent-logs-test");

        foreach (var os in new[] { OSPlatform.Windows, OSPlatform.OSX, OSPlatform.Linux })
            Assert.Equal(Path.GetFullPath(target),
                LogPathResolver.Resolve(os, Env(("AIAGENT_LOG_DIR", target)), "/home/u"));
    }

    [Fact]
    public void WithoutAnyHomeFolder_UsesTempFolder_NotTheWorkingDirectory()
    {
        var dir = LogPathResolver.Resolve(OSPlatform.Linux, Env(), null);

        Assert.True(Path.IsPathRooted(dir));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "vibe-coding-hub", "logs"), dir);
    }

    [Fact]
    public void ResolveDefault_IsAnAbsolutePath()
    {
        Assert.True(Path.IsPathRooted(LogPathResolver.ResolveDefault()));
    }
}
