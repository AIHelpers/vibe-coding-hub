using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// JSONL file-backed <see cref="ISessionStore"/>. Files live under
/// <c>~/.aiagent/sessions/<worktree-hash>/<session-id>.jsonl</c>.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _rootDirectory;
    private readonly ILogger<JsonlSessionStore>? _logger;

    /// <param name="rootDirectory">
    /// Optional override for the sessions root (defaults to <c>~/.aiagent/sessions</c>).
    /// </param>
    public JsonlSessionStore(string? rootDirectory = null, ILogger<JsonlSessionStore>? logger = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent", "sessions");
        _logger = logger;
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task AppendAsync(string sessionId, SessionEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId required", nameof(sessionId));

        var path = ResolveSessionFile(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";
        await using var writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8);
        await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SessionEntry> ReadAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolveSessionFile(sessionId);
        if (!File.Exists(path))
            yield break;

        using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            SessionEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOpts); }
            catch (JsonException ex) { _logger?.LogWarning(ex, "Skipping malformed session line"); }
            if (entry != null) yield return entry;
        }
    }

    public Task<IReadOnlyList<SessionMetadata>> ListAsync(string worktree, CancellationToken ct = default)
    {
        var dir = ResolveWorktreeDir(worktree);
        var list = new List<SessionMetadata>();
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<SessionMetadata>>(list);

        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                var fi = new FileInfo(file);
                var id = Path.GetFileNameWithoutExtension(file);
                int count = 0;
                DateTime created = fi.CreationTimeUtc, updated = fi.LastWriteTimeUtc;
                using (var reader = new StreamReader(file, System.Text.Encoding.UTF8))
                {
                    string? l;
                    while ((l = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        count++;
                        var e = JsonSerializer.Deserialize<SessionEntry>(l, JsonOpts);
                        if (e != null)
                        {
                            if (e.Timestamp < created) created = e.Timestamp;
                            if (e.Timestamp > updated) updated = e.Timestamp;
                        }
                    }
                }
                list.Add(new SessionMetadata
                {
                    SessionId = id,
                    CreatedAt = created,
                    UpdatedAt = updated,
                    SizeBytes = fi.Length,
                    EntryCount = count
                });
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to read session file {File}", file); }
        }

        list.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return Task.FromResult<IReadOnlyList<SessionMetadata>>(list);
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        var path = ResolveSessionFile(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolveWorktreeDir(string worktree)
    {
        var hash = ComputeWorktreeHash(worktree);
        return Path.Combine(_rootDirectory, hash);
    }

    private string ResolveSessionFile(string sessionId)
    {
        var worktree = Environment.CurrentDirectory;
        return Path.Combine(ResolveWorktreeDir(worktree), sessionId + ".jsonl");
    }

    private static string ComputeWorktreeHash(string worktree)
    {
        var normalized = Path.GetFullPath(worktree.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }
}