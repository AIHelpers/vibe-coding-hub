namespace AiCodeAgent.Bench;

/// <summary>Puts a fresh copy of a task's repository snapshot into an attempt's workspace.</summary>
internal sealed class SnapshotProvider
{
    private readonly string _suiteDirectory;
    private readonly string _cacheDirectory;

    public SnapshotProvider(string suiteDirectory, string workDirectory)
    {
        _suiteDirectory = suiteDirectory;
        _cacheDirectory = Path.Combine(workDirectory, "cache");
    }

    public async Task CopyToAsync(BenchTask task, string destination, CancellationToken cancellation)
    {
        var source = !string.IsNullOrWhiteSpace(task.Repo.Path)
            ? Path.GetFullPath(Path.Combine(_suiteDirectory, task.Repo.Path))
            : await EnsureClonedAsync(task, cancellation);
        if (!Directory.Exists(source)) throw new BenchException($"Task '{task.Id}': the snapshot folder does not exist: {source}");

        FileSystemUtil.ForceDelete(destination);
        FileSystemUtil.CopyDirectory(source, destination);
        await EnsureGitAsync(destination, cancellation);
    }

    private async Task<string> EnsureClonedAsync(BenchTask task, CancellationToken cancellation)
    {
        var dir = Path.Combine(_cacheDirectory, FileSystemUtil.SafeName(task.Id));
        if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
        FileSystemUtil.ForceDelete(dir);
        Directory.CreateDirectory(_cacheDirectory);
        var clone = await ProcessRunner.GitAsync(_cacheDirectory, new[] { "clone", "--quiet", task.Repo.Git!, dir }, cancellation, TimeSpan.FromMinutes(30));
        if (clone.ExitCode != 0) throw new BenchException($"Task '{task.Id}': git clone failed: {ProcessRunner.Tail(clone.Stderr)}");
        var checkout = await ProcessRunner.GitAsync(dir, new[] { "checkout", "--quiet", "--detach", task.Repo.Commit! }, cancellation);
        if (checkout.ExitCode != 0)
        {
            FileSystemUtil.ForceDelete(dir);
            throw new BenchException($"Task '{task.Id}': cannot check out {task.Repo.Commit}: {ProcessRunner.Tail(checkout.Stderr)}");
        }
        return dir;
    }

    /// <summary>Every workspace is a git repository with the snapshot committed, so both kinds of run start from the same state.</summary>
    private static async Task EnsureGitAsync(string workspace, CancellationToken cancellation)
    {
        if (Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git"))) return;
        var init = await ProcessRunner.GitAsync(workspace, new[] { "init", "--quiet" }, cancellation);
        if (init.ExitCode != 0) throw new BenchException("git init failed in the workspace: " + ProcessRunner.Tail(init.Stderr));
        await ProcessRunner.GitAsync(workspace, new[] { "add", "-A" }, cancellation);
        var commit = await ProcessRunner.GitAsync(workspace, new[] { "commit", "--quiet", "--allow-empty", "-m", "snapshot" }, cancellation);
        if (commit.ExitCode != 0) throw new BenchException("git commit failed in the workspace: " + ProcessRunner.Tail(commit.Stderr));
    }
}

internal static class FileSystemUtil
{
    public static string SafeName(string text) =>
        new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>Deletes a folder including read-only files (git objects on Windows). Missing folders are fine.</summary>
    public static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Directory.Delete(directory, recursive: true);
    }
}
