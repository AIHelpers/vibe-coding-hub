using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Packs;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs git. A seam so the installer can be tested, and so a missing git gives one clear message.</summary>
public interface IGitClient
{
    Task<GitResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken);
}

/// <summary>Runs the <c>git</c> executable. Never asks for credentials on the terminal: a private repository must be reachable with the credentials git already has.</summary>
public sealed class ProcessGitClient : IGitClient
{
    public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        // Never run a transport that executes a command, whatever the user's config says.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("protocol.ext.allow=never");
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("git could not be started.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }
            return new GitResult(process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitResult(127, string.Empty, "git was not found. Install git and make sure it is on the PATH.");
        }
    }
}

public sealed record PackFileEntry(string Path, long Size);

/// <summary>
/// What a pack would install, shown before anything is installed: every file, what the app will read from it, and
/// anything that could run code (hooks, MCP servers, scripts). Hooks and MCP entries in a pack are never enabled.
/// </summary>
public sealed record PackPreview
{
    public string Url { get; init; } = string.Empty;
    public string? RequestedRef { get; init; }
    public string Commit { get; init; } = string.Empty;
    public bool Pinned { get; init; }
    public PackManifest? Manifest { get; init; }
    public string StagingDirectory { get; init; } = string.Empty;
    public IReadOnlyList<PackFileEntry> Files { get; init; } = Array.Empty<PackFileEntry>();
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Flows { get; init; } = Array.Empty<string>();
    /// <summary>Things to look at before installing: hooks, MCP entries, scripts and binaries. None of them are enabled or run by the app.</summary>
    public IReadOnlyList<string> Attention { get; init; } = Array.Empty<string>();
    /// <summary>Why the pack cannot be installed (no manifest, symlinks, too big). Empty = installable.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    /// <summary>Set when <c>requires.app</c> is not met by this app version.</summary>
    public string? Incompatible { get; init; }

    public bool CanInstall => Problems.Count == 0 && Manifest != null;

    /// <summary>Plain text for the terminal and for the dialog.</summary>
    public string Render(bool listFiles = true)
    {
        var sb = new StringBuilder();
        if (Manifest != null)
        {
            sb.Append(Manifest.Name);
            if (!string.IsNullOrWhiteSpace(Manifest.Version)) sb.Append(' ').Append(Manifest.Version);
            sb.Append("  (").Append(Pinned && !string.IsNullOrEmpty(RequestedRef) ? RequestedRef : Commit[..Math.Min(12, Commit.Length)]).AppendLine(")");
            if (!string.IsNullOrWhiteSpace(Manifest.Description)) sb.AppendLine(Manifest.Description);
            var meta = new[] { Manifest.Author, Manifest.License }.Where(x => !string.IsNullOrWhiteSpace(x));
            if (meta.Any()) sb.AppendLine(string.Join(" · ", meta));
        }
        sb.AppendLine($"from {Url} @ {Commit[..Math.Min(12, Commit.Length)]}");
        sb.AppendLine($"Adds {Skills.Count} skill(s), {Characters.Count} character(s), {Flows.Count} flow(s).");
        void List(string title, IReadOnlyList<string> names)
        {
            if (names.Count > 0) sb.AppendLine($"  {title}: {string.Join(", ", names)}");
        }
        List("skills", Skills);
        List("characters", Characters);
        List("flows", Flows);
        if (Incompatible != null) sb.AppendLine().AppendLine("! " + Incompatible);
        if (Attention.Count > 0)
        {
            sb.AppendLine().AppendLine("Check before installing (packs never enable hooks or MCP servers, and the app never runs these files):");
            foreach (var a in Attention) sb.AppendLine("  ! " + a);
        }
        foreach (var p in Problems) sb.AppendLine().AppendLine("✕ " + p);
        if (listFiles)
        {
            sb.AppendLine().AppendLine($"Files ({Files.Count}):");
            foreach (var f in Files) sb.AppendLine($"  {f.Path}  ({FormatSize(f.Size)})");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB" : (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

public enum PackUpdateState { UpToDate, Updated, Pinned, Failed }

public sealed record PackUpdateResult(string Name, PackUpdateState State, string Message, InstalledPack? Pack = null);

public sealed record PackSyncResult(string Name, string? Ref, bool Installed, string Message);

public class PackException : Exception
{
    public PackException(string message) : base(message) { }
}

/// <summary>
/// Installs packs from git (<c>pack add</c>, <c>pack sync</c>, <c>pack update</c>, <c>pack remove</c>). A pack is cloned at a ref
/// into a staging folder, inspected, and only then moved to <c>~/.aiagent/packs/&lt;name&gt;@&lt;ref&gt;</c>.
/// </summary>
public sealed class PackInstaller
{
    public const int MaxFiles = 5_000;
    public const long MaxTotalBytes = 100L * 1024 * 1024;

    private static readonly Regex CommitLike = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);
    private static readonly string[] ScriptExtensions = { ".sh", ".bash", ".ps1", ".bat", ".cmd", ".exe", ".dll", ".so", ".dylib", ".js", ".mjs", ".py" };

    private readonly PackStore _store;
    private readonly IGitClient _git;
    private readonly SemanticVersion? _appVersion;
    private readonly TimeSpan _timeout;

    public PackInstaller(PackStore store, IGitClient? git = null, SemanticVersion? appVersion = null, TimeSpan? timeout = null)
    {
        _store = store;
        _git = git ?? new ProcessGitClient();
        _appVersion = appVersion;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public PackStore Store => _store;

    // ------------------------------------------------------------------ add

    /// <summary>
    /// Clones <paramref name="url"/> at <paramref name="gitRef"/> (a tag, a commit or a branch; null = the default branch) into the staging
    /// folder and inspects it. Nothing is installed until <see cref="Commit"/>; call <see cref="Discard"/> to drop it.
    /// </summary>
    public async Task<PackPreview> PrepareAsync(string url, string? gitRef = null, CancellationToken cancellationToken = default)
    {
        url = url.Trim();
        gitRef = string.IsNullOrWhiteSpace(gitRef) ? null : gitRef.Trim();
        if (url.Length == 0 || url.StartsWith('-') || url.Contains('\n') || url.Contains('\0'))
            throw new PackException($"'{url}' is not a git URL.");
        if (gitRef != null && (gitRef.StartsWith('-') || gitRef.Contains(' ') || gitRef.Contains("..")))
            throw new PackException($"'{gitRef}' is not a tag, commit or branch name.");

        Directory.CreateDirectory(_store.StagingRoot);
        var staging = Path.Combine(_store.StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var clone = await _git.RunAsync(new[] { "clone", "--quiet", "--no-checkout", "--no-recurse-submodules", "--", url, staging }, null, cts.Token).ConfigureAwait(false);
            if (!clone.Ok) throw new PackException($"Could not clone {url}: {FirstLine(clone.Error)}");

            if (gitRef != null)
            {
                var checkout = await _git.RunAsync(new[] { "checkout", "--quiet", "--detach", gitRef, "--" }, staging, cts.Token).ConfigureAwait(false);
                if (!checkout.Ok)
                    checkout = await _git.RunAsync(new[] { "checkout", "--quiet", "--detach", "origin/" + gitRef, "--" }, staging, cts.Token).ConfigureAwait(false);
                if (!checkout.Ok) throw new PackException($"'{gitRef}' is not a tag, commit or branch of {url}.");
            }
            else
            {
                var checkout = await _git.RunAsync(new[] { "checkout", "--quiet", "--detach", "HEAD", "--" }, staging, cts.Token).ConfigureAwait(false);
                if (!checkout.Ok) throw new PackException($"Could not check out {url}: {FirstLine(checkout.Error)}");
            }

            var head = await _git.RunAsync(new[] { "rev-parse", "HEAD" }, staging, cts.Token).ConfigureAwait(false);
            if (!head.Ok || head.Output.Length < 7) throw new PackException($"Could not read the commit of {url}.");
            var commit = head.Output.Trim();

            var pinned = false;
            if (gitRef != null)
            {
                var tag = await _git.RunAsync(new[] { "show-ref", "--verify", "--quiet", "refs/tags/" + gitRef }, staging, cts.Token).ConfigureAwait(false);
                pinned = tag.Ok || (CommitLike.IsMatch(gitRef) && commit.StartsWith(gitRef, StringComparison.OrdinalIgnoreCase));
            }

            // The checkout is done; the history is not needed (and updates clone again).
            PackStore.DeleteDirectory(Path.Combine(staging, ".git"));

            return Inspect(staging, url, gitRef, commit, pinned);
        }
        catch
        {
            PackStore.DeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>Moves a prepared pack into <c>~/.aiagent/packs/&lt;name&gt;@&lt;ref&gt;</c> and records it. <paramref name="makeDefault"/>: other projects use this copy.</summary>
    public InstalledPack Commit(PackPreview preview, bool makeDefault = true, bool allowIncompatible = false)
    {
        if (!preview.CanInstall) throw new PackException(string.Join(" ", preview.Problems.DefaultIfEmpty("The pack cannot be installed.")));
        if (preview.Incompatible != null && !allowIncompatible) throw new PackException(preview.Incompatible);
        var manifest = preview.Manifest!;

        var label = preview.Pinned && preview.RequestedRef != null ? SafeFolderPart(preview.RequestedRef) : preview.Commit[..Math.Min(12, preview.Commit.Length)];
        var folder = $"{manifest.Name}@{label}";
        var target = Path.Combine(_store.Root, folder);
        if (Directory.Exists(target)) PackStore.DeleteDirectory(target);
        Directory.Move(preview.StagingDirectory, target);

        var pack = new InstalledPack
        {
            Name = manifest.Name,
            Url = preview.Url,
            Ref = preview.RequestedRef,
            Commit = preview.Commit,
            Pinned = preview.Pinned,
            Version = manifest.Version,
            Directory = folder,
            InstalledAt = DateTimeOffset.UtcNow
        };
        _store.Add(pack, makeDefault);
        return pack;
    }

    public void Discard(PackPreview preview) => PackStore.DeleteDirectory(preview.StagingDirectory);

    /// <summary>Prepare and commit in one go (when the caller has already shown the preview).</summary>
    public async Task<InstalledPack> InstallAsync(string url, string? gitRef = null, bool makeDefault = true, bool allowIncompatible = false, CancellationToken cancellationToken = default)
    {
        var preview = await PrepareAsync(url, gitRef, cancellationToken).ConfigureAwait(false);
        try { return Commit(preview, makeDefault, allowIncompatible); }
        catch { Discard(preview); throw; }
    }

    // ------------------------------------------------------------------ update / remove / sync

    /// <summary>
    /// Moves packs that follow a branch (installed without a ref, or with a branch) to the branch's newest commit. A pack pinned to
    /// a tag or commit stays put. <paramref name="name"/> null = every installed pack.
    /// </summary>
    public async Task<IReadOnlyList<PackUpdateResult>> UpdateAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        var results = new List<PackUpdateResult>();
        var targets = _store.List().Where(p => name == null || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            results.Add(new PackUpdateResult(name ?? "(none)", PackUpdateState.Failed, name == null ? "No packs are installed." : $"Pack '{name}' is not installed."));
            return results;
        }
        foreach (var installed in targets)
        {
            if (installed.Pinned)
            {
                results.Add(new PackUpdateResult(installed.Name, PackUpdateState.Pinned, $"{installed.Name} is pinned to {installed.RefLabel}; install another ref with 'pack add --ref'.", installed));
                continue;
            }
            try
            {
                var preview = await PrepareAsync(installed.Url, installed.Ref, cancellationToken).ConfigureAwait(false);
                if (string.Equals(preview.Commit, installed.Commit, StringComparison.OrdinalIgnoreCase))
                {
                    Discard(preview);
                    results.Add(new PackUpdateResult(installed.Name, PackUpdateState.UpToDate, $"{installed.Name} is up to date ({installed.ShortCommit}).", installed));
                    continue;
                }
                if (preview.Manifest != null && !string.Equals(preview.Manifest.Name, installed.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Discard(preview);
                    results.Add(new PackUpdateResult(installed.Name, PackUpdateState.Failed, $"The repository now calls itself '{preview.Manifest.Name}'; not updating."));
                    continue;
                }
                var wasDefault = installed.IsDefault;
                var updated = Commit(preview, makeDefault: wasDefault);
                _store.Remove(installed.Name, installed.RefLabel);
                results.Add(new PackUpdateResult(installed.Name, PackUpdateState.Updated, $"{installed.Name} {installed.ShortCommit} → {updated.ShortCommit}.", updated));
            }
            catch (PackException ex)
            {
                results.Add(new PackUpdateResult(installed.Name, PackUpdateState.Failed, ex.Message));
            }
        }
        return results;
    }

    public IReadOnlyList<InstalledPack> Remove(string name, string? gitRef = null) => _store.Remove(name, gitRef);

    /// <summary>
    /// Installs exactly what the project's <c>.aiagent/packs.json</c> declares: every pack at the ref it names, unless that ref is already
    /// installed. <paramref name="approve"/> sees each preview first (hooks, MCP entries, scripts) and can refuse it.
    /// </summary>
    public async Task<IReadOnlyList<PackSyncResult>> SyncAsync(string projectDirectory, Func<PackPreview, bool>? approve = null, bool allowIncompatible = false, CancellationToken cancellationToken = default)
    {
        var results = new List<PackSyncResult>();
        foreach (var reference in PackStore.ReadProjectFile(projectDirectory))
        {
            var existing = _store.List().FirstOrDefault(p => string.Equals(p.Name, reference.Name, StringComparison.OrdinalIgnoreCase) &&
                (reference.Ref == null || string.Equals(p.Ref ?? string.Empty, reference.Ref, StringComparison.OrdinalIgnoreCase) ||
                 p.Commit.StartsWith(reference.Ref, StringComparison.OrdinalIgnoreCase)));
            if (existing != null)
            {
                results.Add(new PackSyncResult(reference.Name, reference.Ref, false, $"{reference.Name} {existing.RefLabel} is already installed."));
                continue;
            }
            try
            {
                var preview = await PrepareAsync(reference.Url, reference.Ref, cancellationToken).ConfigureAwait(false);
                if (preview.Manifest != null && !string.Equals(preview.Manifest.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Discard(preview);
                    results.Add(new PackSyncResult(reference.Name, reference.Ref, false, $"{reference.Url} is the pack '{preview.Manifest.Name}', not '{reference.Name}'."));
                    continue;
                }
                if (approve != null && !approve(preview))
                {
                    Discard(preview);
                    results.Add(new PackSyncResult(reference.Name, reference.Ref, false, $"{reference.Name} was not approved; nothing installed."));
                    continue;
                }
                var installed = Commit(preview, makeDefault: false, allowIncompatible);
                results.Add(new PackSyncResult(reference.Name, reference.Ref, true,
                    $"Installed {installed.Name} {installed.RefLabel}." + (installed.Pinned ? "" : " (the ref is a branch, so this is not reproducible; pin a tag or commit in packs.json)")));
            }
            catch (PackException ex)
            {
                results.Add(new PackSyncResult(reference.Name, reference.Ref, false, ex.Message));
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ inspect

    internal PackPreview Inspect(string directory, string url, string? requestedRef, string commit, bool pinned)
    {
        var problems = new List<string>();
        var attention = new List<string>();
        var files = new List<PackFileEntry>();
        long total = 0;

        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); } catch (IOException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problems.Add($"{relative} is a symbolic link; packs may not contain links.");
                continue;
            }
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var dirName = Path.GetFileName(path);
                if (dirName.Equals("hooks", StringComparison.OrdinalIgnoreCase)) attention.Add($"{relative}/ holds hooks (never enabled by a pack)");
                continue;
            }
            var size = new FileInfo(path).Length;
            files.Add(new PackFileEntry(relative, size));
            total += size;
            var name = Path.GetFileName(path);
            if (name.Equals("hooks.json", StringComparison.OrdinalIgnoreCase)) attention.Add($"{relative} declares hooks (never enabled by a pack)");
            else if (name.Equals("mcp.json", StringComparison.OrdinalIgnoreCase) || name.Equals(".mcp.json", StringComparison.OrdinalIgnoreCase))
                attention.Add($"{relative} declares MCP servers: {DescribeMcp(path)} (never enabled by a pack)");
            else if (ScriptExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                attention.Add($"{relative} is a script or binary");
        }
        if (files.Count > MaxFiles) problems.Add($"The pack has {files.Count} files; the limit is {MaxFiles}.");
        if (total > MaxTotalBytes) problems.Add($"The pack is {total / 1048576} MB; the limit is {MaxTotalBytes / 1048576} MB.");
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var manifestErrors = new List<string>();
        var manifest = PackManifest.TryRead(directory, manifestErrors);
        problems.AddRange(manifestErrors);

        string? incompatible = null;
        // A development build (0.0.0-dev.N) has no real version, so it is never refused.
        if (manifest?.Requires?.App is { Length: > 0 } range && _appVersion != null && !(_appVersion.Major == 0 && _appVersion.IsPreRelease) &&
            !PackVersionRange.Satisfies(range, _appVersion))
            incompatible = $"{manifest.Name} needs app {range}; this is {_appVersion}.";

        var skills = ListDirs(Path.Combine(directory, "skills")).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var characters = ListFiles(Path.Combine(directory, "characters"), "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var flows = ListFiles(Path.Combine(directory, "pipelines"), "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (manifest != null && skills.Count + characters.Count + flows.Count == 0)
            problems.Add("The pack has no skills/, characters/ or pipelines/ content.");

        return new PackPreview
        {
            Url = url,
            RequestedRef = requestedRef,
            Commit = commit,
            Pinned = pinned,
            Manifest = manifest,
            StagingDirectory = directory,
            Files = files,
            Skills = skills,
            Characters = characters,
            Flows = flows,
            Attention = attention,
            Problems = problems,
            Incompatible = incompatible
        };
    }

    private static IEnumerable<string> ListDirs(string dir) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : Enumerable.Empty<string>();
    private static IEnumerable<string> ListFiles(string dir, string pattern) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : Enumerable.Empty<string>();

    private static string DescribeMcp(string file)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("mcpServers", out var servers) || doc.RootElement.TryGetProperty("servers", out servers)) &&
                servers.ValueKind == System.Text.Json.JsonValueKind.Object)
                return string.Join(", ", servers.EnumerateObject().Select(p => p.Name));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { /* fall through */ }
        return "unreadable";
    }

    private static string SafeFolderPart(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";
}
