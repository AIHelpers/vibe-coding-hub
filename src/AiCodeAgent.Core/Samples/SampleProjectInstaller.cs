using System.Diagnostics;
using System.Text;

namespace AiCodeAgent.Core.Samples;

/// <summary>One file of the sample project: its path inside the project (always with '/') and how to read it.</summary>
public sealed record SampleFile(string RelativePath, Func<Stream> Open);

/// <summary>Where the files of the sample project come from (embedded in the app, or a folder in tests).</summary>
public interface ISampleFileSource
{
    IReadOnlyList<SampleFile> GetFiles();
}

/// <summary>Reads the sample from a folder, e.g. <c>samples/TodoApp</c> in the repository. Skips bin/, obj/ and logs/.</summary>
public sealed class DirectorySampleFileSource : ISampleFileSource
{
    private static readonly string[] SkippedFolders = { "bin", "obj", "logs", ".git" };
    private readonly string _root;

    public DirectorySampleFileSource(string root) => _root = root;

    public IReadOnlyList<SampleFile> GetFiles()
    {
        var files = new List<SampleFile>();
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
                continue;
            var full = path;
            files.Add(new SampleFile(relative, () => File.OpenRead(full)));
        }
        return files;
    }
}

/// <summary>What <see cref="SampleProjectInstaller.InstallAsync"/> made.</summary>
/// <param name="Path">The new project folder.</param>
/// <param name="GitReady">True when the folder is a git repository with an initial commit.</param>
/// <param name="Warning">Why git is not ready (git missing, ...), or null.</param>
public sealed record SampleInstallResult(string Path, bool GitReady, string? Warning);

/// <summary>Runs one git command and returns its exit code and output. Replaceable in tests.</summary>
public interface IGitRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Runs the real <c>git</c> executable. A missing git is reported as exit code -1, never thrown.</summary>
public sealed class ProcessGitRunner : IGitRunner
{
    private readonly TimeSpan _timeout;

    public ProcessGitRunner(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("git")
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
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Never wait for a password or an editor.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_EDITOR"] = "true";

        try
        {
            using var process = Process.Start(info);
            if (process == null) return (-1, "git did not start");
            process.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (cancellationToken.IsCancellationRequested) throw;
                return (-1, $"git {string.Join(' ', arguments)} did not finish within {_timeout.TotalSeconds:0} seconds");
            }

            var output = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "git is not installed or not on PATH");
        }
    }
}

/// <summary>
/// "Try the sample project": copies the bundled sample into <c>Documents/VibeCodingHub/samples/&lt;name&gt;</c>,
/// turns it into a git repository with one commit, and tells the caller where it is. An existing folder is never
/// touched: a second copy gets <c>-2</c>, <c>-3</c>, ... so the user's own edits stay safe.
/// </summary>
public sealed class SampleProjectInstaller
{
    public const string ProjectName = "TodoApp";

    /// <summary>What the chat box is filled with after the sample opens.</summary>
    public const string FirstTaskPrompt =
        "The Overdue_ExcludesCompletedItems test is failing. Fix the overdue logic in TodoService and verify with the tests.";

    private const string InitialCommitMessage = "Initial commit: TodoApp sample";

    private readonly ISampleFileSource _source;
    private readonly IGitRunner _git;

    public SampleProjectInstaller(ISampleFileSource source, IGitRunner? git = null)
    {
        _source = source;
        _git = git ?? new ProcessGitRunner();
    }

    /// <summary><c>Documents/VibeCodingHub/samples</c>; falls back to the home folder when the system has no Documents folder.</summary>
    public static string DefaultSamplesRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "VibeCodingHub", "samples");
    }

    public async Task<SampleInstallResult> InstallAsync(string? samplesRoot = null, CancellationToken cancellationToken = default)
    {
        var files = _source.GetFiles();
        if (files.Count == 0)
            throw new InvalidOperationException("The sample project files are missing from this build of the app.");

        var root = samplesRoot ?? DefaultSamplesRoot();
        var target = FreeFolder(root, ProjectName);
        Directory.CreateDirectory(target);

        try
        {
            foreach (var file in files)
                CopyFile(target, file);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        var (ready, warning) = await InitRepositoryAsync(target, cancellationToken).ConfigureAwait(false);
        return new SampleInstallResult(target, ready, warning);
    }

    /// <summary>The first of <c>name</c>, <c>name-2</c>, <c>name-3</c> ... that does not exist yet.</summary>
    internal static string FreeFolder(string root, string name)
    {
        var path = Path.Combine(root, name);
        for (var n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(root, $"{name}-{n}");
        return path;
    }

    private static void CopyFile(string target, SampleFile file)
    {
        var relative = file.RelativePath.Replace('\\', '/').TrimStart('/');
        var destination = Path.GetFullPath(Path.Combine(target, relative));
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        // A sample file must stay inside the project folder.
        if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe sample file path: {file.RelativePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = file.Open();
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    private async Task<(bool Ready, string? Warning)> InitRepositoryAsync(string folder, CancellationToken ct)
    {
        var (code, output) = await _git.RunAsync(folder, new[] { "init", "--initial-branch=main" }, ct).ConfigureAwait(false);
        if (code != 0)
        {
            // Old git versions do not know --initial-branch.
            (code, output) = await _git.RunAsync(folder, new[] { "init" }, ct).ConfigureAwait(false);
            if (code != 0) return (false, GitWarning(output));
        }

        // A commit needs an identity. Only when the user has none do we set one, and only inside this sample repository.
        var (nameCode, name) = await _git.RunAsync(folder, new[] { "config", "user.name" }, ct).ConfigureAwait(false);
        if (nameCode != 0 || string.IsNullOrWhiteSpace(name))
            await _git.RunAsync(folder, new[] { "config", "user.name", "Vibe Coding Hub" }, ct).ConfigureAwait(false);
        var (mailCode, mail) = await _git.RunAsync(folder, new[] { "config", "user.email" }, ct).ConfigureAwait(false);
        if (mailCode != 0 || string.IsNullOrWhiteSpace(mail))
            await _git.RunAsync(folder, new[] { "config", "user.email", "sample@vibecodinghub.local" }, ct).ConfigureAwait(false);

        (code, output) = await _git.RunAsync(folder, new[] { "add", "-A" }, ct).ConfigureAwait(false);
        if (code != 0) return (false, GitWarning(output));

        (code, output) = await _git.RunAsync(folder, new[] { "commit", "--no-gpg-sign", "-m", InitialCommitMessage }, ct).ConfigureAwait(false);
        return code == 0 ? (true, null) : (false, GitWarning(output));
    }

    private static string GitWarning(string output)
    {
        var detail = string.IsNullOrWhiteSpace(output) ? "unknown error" : output.Split('\n')[0].Trim();
        return $"The sample was copied, but git could not set it up ({detail}). You can still fix the test; install git to try the commit step.";
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best effort */ }
    }
}
