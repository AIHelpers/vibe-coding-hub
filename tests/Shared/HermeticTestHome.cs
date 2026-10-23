using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace AiCodeAgent.Tests.Hermetic;

/// <summary>
/// Keeps a test process away from the developer's real <c>~/.aiagent</c>. This file is linked (not copied)
/// into every test project. Its module initializer runs before any test: it points <c>AIAGENT_HOME</c> at a
/// throwaway folder and records what the real folder contains, so <see cref="HermeticityGuardTests"/> can
/// detect writes to it.
/// </summary>
internal static class HermeticTestHome
{
    internal const string EnvironmentVariable = "AIAGENT_HOME";

    private static string _baseline = "<not captured>";

    /// <summary>Throwaway per-process folder that tests use as their <c>~/.aiagent</c>.</summary>
    internal static string TempRoot { get; private set; } = string.Empty;

    /// <summary>The developer's real <c>~/.aiagent</c>, as the platform reports it (ignores <c>AIAGENT_HOME</c>).</summary>
    internal static string RealRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aiagent");

    /// <summary>Listing of the real folder taken before any test ran.</summary>
    internal static string Baseline => _baseline;

    [ModuleInitializer]
    internal static void Isolate()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "aiagent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);
        // Always override, even if the developer set AIAGENT_HOME: tests must never use a real folder.
        Environment.SetEnvironmentVariable(EnvironmentVariable, TempRoot);
        _baseline = Snapshot(RealRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(TempRoot);
    }

    /// <summary>Sorted listing of every file and folder under <paramref name="root"/> with size and write time.</summary>
    internal static string Snapshot(string root)
    {
        if (!Directory.Exists(root))
            return "<absent>";

        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderBy(e => e, StringComparer.Ordinal))
        {
            FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
            var length = info is FileInfo file ? file.Length : 0;
            sb.Append(Path.GetRelativePath(root, entry)).Append('|')
              .Append(length).Append('|')
              .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return sb.ToString();
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best effort: the OS temp folder is cleaned up eventually */ }
    }
}

public class HermeticityGuardTests
{
    [Fact]
    public void TestRun_LeavesTheRealAiagentFolderUnchanged()
    {
        Assert.Equal(HermeticTestHome.Baseline, HermeticTestHome.Snapshot(HermeticTestHome.RealRoot));
    }

    [Fact]
    public void Tests_UseTheTemporaryHome()
    {
        Assert.Equal(
            Path.GetFullPath(HermeticTestHome.TempRoot),
            Path.GetFullPath(Environment.GetEnvironmentVariable(HermeticTestHome.EnvironmentVariable)!));
    }
}
