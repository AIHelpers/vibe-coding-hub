using System.Diagnostics;

namespace AiCodeAgent.Core.Tests.Packs;

/// <summary>A throw-away git repository used as the source of a pack: a work tree plus a bare clone to install from.</summary>
internal sealed class PackTestRepo : IDisposable
{
    public string Root { get; }
    public string Work { get; }
    public string Bare { get; }

    public PackTestRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "pack-repo-" + Guid.NewGuid().ToString("N"));
        Work = Path.Combine(Root, "work");
        Bare = Path.Combine(Root, "remote.git");
        Directory.CreateDirectory(Work);
        Git(Work, "init", "--quiet", "-b", "main");
    }

    public static bool GitAvailable()
    {
        try { return Run(Path.GetTempPath(), "--version").Exit == 0; }
        catch { return false; }
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Work, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void WriteManifest(string name, string version = "1.0.0", string? requiresApp = null) =>
        Write("pack.json", "{ \"name\": \"" + name + "\", \"version\": \"" + version + "\", \"description\": \"test pack\", \"author\": \"t\", \"license\": \"MIT\"" +
              (requiresApp != null ? ", \"requires\": { \"app\": \"" + requiresApp + "\" }" : "") + " }");

    public void WriteSkill(string name, string description = "a pack skill", string body = "Do the thing.") =>
        Write($"skills/{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n{body}\n");

    public void WriteCharacter(string id, string description = "a pack character", string baseRole = "planner") =>
        Write($"characters/{id}.md", $"---\nid: {id}\ndescription: {description}\nbase-role: {baseRole}\n---\nYou are {id}.\n");

    public void WriteFlow(string name) =>
        Write($"pipelines/{name}.json", "{ \"name\": \"" + name + "\", \"description\": \"pack flow\", \"stages\": [ { \"name\": \"Plan\", \"role\": \"planner\", \"promptTemplate\": \"{task}\" } ] }");

    /// <summary>Commits everything and returns the commit hash; refreshes the bare clone.</summary>
    public string Commit(string message = "commit", string? tag = null)
    {
        Git(Work, "add", "-A");
        Git(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", message);
        if (tag != null) Git(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "tag.gpgsign=false", "tag", tag);
        var hash = Git(Work, "rev-parse", "HEAD").Trim();
        DeleteTree(Bare);
        Git(Root, "clone", "--quiet", "--bare", Work, Bare);
        return hash;
    }

    private static string Git(string dir, params string[] args)
    {
        var (exit, output, error) = Run(dir, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private static (int Exit, string Output, string Error) Run(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }

    // Git marks object files read-only; on Windows Directory.Delete refuses those.
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    public void Dispose()
    {
        try { DeleteTree(Root); }
        catch { /* best effort */ }
    }
}
