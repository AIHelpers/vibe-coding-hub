using System.Diagnostics;
using System.Xml.Linq;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>
/// E3-S3 CI test: the sample builds, exactly one of its tests fails, and after the reference patch none does.
/// It runs the sample's own <c>dotnet test</c> (restores xunit from NuGet, so it needs the same network as the rest of CI).
/// </summary>
public sealed class SampleProjectReferencePatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sample-patch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed record TestRun(IReadOnlyList<string> Passed, IReadOnlyList<string> Failed, string Output);

    [Fact]
    public async Task TheSample_HasExactlyOneFailingTest_UntilTheReferencePatchIsApplied()
    {
        var installer = new SampleProjectInstaller(new DirectorySampleFileSource(SampleRepo.SampleFolder()));
        var sample = await installer.InstallAsync(_dir);
        Assert.True(sample.GitReady, sample.Warning);

        var before = await RunTestsAsync(sample.Path);
        Assert.True(before.Failed.Count == 1, $"Expected exactly one failing test before the fix, got {before.Failed.Count}.\n{before.Output}");
        Assert.Equal("Overdue_ExcludesCompletedItems", before.Failed[0]);
        Assert.True(before.Passed.Count >= 5, "the rest of the suite is green");

        var git = new ProcessGitRunner();
        var apply = await git.RunAsync(sample.Path, new[] { "apply", "--ignore-whitespace", SampleRepo.ReferencePatch() }, CancellationToken.None);
        Assert.True(apply.ExitCode == 0, "git apply failed: " + apply.Output);

        var after = await RunTestsAsync(sample.Path);
        Assert.True(after.Failed.Count == 0, $"Expected no failing test after the fix, got: {string.Join(", ", after.Failed)}\n{after.Output}");
        Assert.Equal(before.Passed.Count + 1, after.Passed.Count);

        // The fix is a plain edit of one file: exactly what the Source Control step will commit.
        var status = await git.RunAsync(sample.Path, new[] { "status", "--porcelain" }, CancellationToken.None);
        Assert.Equal("M src/TodoApp/TodoService.cs", status.Output.Trim().Replace("\\", "/"));
    }

    /// <summary>Runs <c>dotnet test</c> in the folder and reads the TRX file, so the result does not depend on the console language.</summary>
    private async Task<TestRun> RunTestsAsync(string projectFolder)
    {
        var results = Path.Combine(_dir, "results-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = projectFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "test", "TodoApp.slnx", "--nologo", "--logger", "trx;LogFileName=result.trx", "--results-directory", results })
            info.ArgumentList.Add(arg);

        // Do not let the outer test run (VSTest, MSBuild node reuse) leak into the sample's run.
        foreach (var key in info.Environment.Keys.ToList())
        {
            if (key.StartsWith("VSTEST_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(key);
        }
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet did not start");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new TimeoutException("dotnet test of the sample project took longer than 6 minutes");
        }
        var output = await stdout + await stderr;

        var trx = Directory.Exists(results) ? Directory.GetFiles(results, "*.trx").FirstOrDefault() : null;
        Assert.True(trx != null, "dotnet test produced no result file (the sample did not build or restore?).\n" + output);

        var outcomes = XDocument.Load(trx!).Descendants()
            .Where(e => e.Name.LocalName == "UnitTestResult")
            .Select(e => (Name: (string?)e.Attribute("testName") ?? "", Outcome: (string?)e.Attribute("outcome") ?? ""))
            .Select(t => (Name: t.Name[(t.Name.LastIndexOf('.') + 1)..], t.Outcome))
            .ToList();

        return new TestRun(
            outcomes.Where(t => t.Outcome == "Passed").Select(t => t.Name).ToList(),
            outcomes.Where(t => t.Outcome == "Failed").Select(t => t.Name).ToList(),
            output);
    }
}
