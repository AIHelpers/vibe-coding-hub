using System.Diagnostics;
using System.Text;

namespace AiCodeAgent.Bench;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration, bool TimedOut);

internal static class ProcessRunner
{
    /// <summary>Runs a program, captures its output and kills the whole process tree when the timeout passes.</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (key, value) in environment)
            {
                if (value == null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;
            }

        using var process = new Process { StartInfo = psi };
        var clock = Stopwatch.StartNew();
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BenchException($"Cannot start '{fileName}': {ex.Message}", ex);
        }
        try { process.StandardInput.Close(); } catch (IOException) { }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellation.IsCancellationRequested) throw;
            timedOut = true;
        }
        if (timedOut) await process.WaitForExitAsync(CancellationToken.None);

        // A grandchild that kept the pipes open must not hang the harness.
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        clock.Stop();
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            stdout.IsCompletedSuccessfully ? stdout.Result : "",
            stderr.IsCompletedSuccessfully ? stderr.Result : "",
            clock.Elapsed,
            timedOut);
    }

    /// <summary>Runs a shell command line: <c>cmd /c</c> on Windows, <c>bash -c</c> elsewhere.</summary>
    public static Task<ProcessResult> RunShellAsync(
        string command, string workingDirectory, IReadOnlyDictionary<string, string?>? environment, TimeSpan timeout, CancellationToken cancellation)
    {
        return OperatingSystem.IsWindows()
            ? RunAsync("cmd.exe", new[] { "/d", "/s", "/c", command }, workingDirectory, environment, timeout, cancellation)
            : RunAsync("bash", new[] { "-c", command }, workingDirectory, environment, timeout, cancellation);
    }

    public static Task<ProcessResult> GitAsync(string workingDirectory, IEnumerable<string> args, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var all = new List<string> { "-c", "user.name=bench", "-c", "user.email=bench@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" };
        all.AddRange(args);
        return RunAsync("git", all, workingDirectory, null, timeout ?? TimeSpan.FromMinutes(10), cancellation);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public static string Tail(string text, int max = 600)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }
}
