using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Context.Persistence;

/// <summary>The messages and metadata read back from a stored chat session.</summary>
public sealed class StoredChatSession
{
    public List<Message> Messages { get; } = new();
    public string? Title { get; set; }
    public decimal CostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Keeps each chat session as an append-only JSONL file under
/// <c>~/.aiagent/sessions/chats/&lt;working-directory-hash&gt;/&lt;session-id&gt;.jsonl</c>. Every message is appended the moment it
/// is added, so a crash loses nothing that was already stored; a half-written last line is skipped on read.
/// Line kinds: <c>msg</c> (a message), <c>reset</c> (drop everything before), <c>title</c> and <c>cost</c> (metadata).
/// </summary>
public sealed class ChatSessionStore
{
    private sealed class Line
    {
        public string Op { get; set; } = string.Empty;
        public DateTime At { get; set; } = DateTime.UtcNow;
        public Message? Message { get; set; }
        public string? Text { get; set; }
        public decimal? Cost { get; set; }
    }

    private const string Msg = "msg", Reset = "reset", TitleOp = "title", CostOp = "cost";
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _root;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public ChatSessionStore(string? rootDirectory = null, ILogger? logger = null)
    {
        _root = rootDirectory ?? Path.Combine(AppPaths.Sessions, "chats");
        _logger = logger;
    }

    /// <summary>Only plain ids (letters, digits, '-', '_') get a file; agent sub-session ids such as "abc/coder" stay in memory.</summary>
    public static bool IsPersistableId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    public string DirectoryFor(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return Path.Combine(_root, Convert.ToHexString(bytes).ToLowerInvariant()[..12]);
    }

    public string FileFor(string workingDirectory, string sessionId) =>
        Path.Combine(DirectoryFor(workingDirectory), sessionId + ".jsonl");

    public bool Exists(string workingDirectory, string sessionId) => File.Exists(FileFor(workingDirectory, sessionId));

    // ----- writing -----

    public Task AppendMessageAsync(string workingDirectory, string sessionId, Message message, CancellationToken ct = default) =>
        AppendAsync(FileFor(workingDirectory, sessionId), new Line { Op = Msg, Message = message }, createIfMissing: true, ct);

    /// <summary>Marks everything before this point as cleared (rewind / compaction rewrite the session this way). No file is created for an unknown session.</summary>
    public Task AppendResetAsync(string workingDirectory, string sessionId, CancellationToken ct = default) =>
        AppendAsync(FileFor(workingDirectory, sessionId), new Line { Op = Reset }, createIfMissing: false, ct);

    public Task SetTitleAsync(string workingDirectory, string sessionId, string title, CancellationToken ct = default) =>
        AppendAsync(FileFor(workingDirectory, sessionId), new Line { Op = TitleOp, Text = title }, createIfMissing: false, ct);

    public Task SetCostAsync(string workingDirectory, string sessionId, decimal costUsd, CancellationToken ct = default) =>
        AppendAsync(FileFor(workingDirectory, sessionId), new Line { Op = CostOp, Cost = costUsd }, createIfMissing: false, ct);

    /// <summary>Replaces the whole message list (after trimming), keeping title and cost. Written to a temp file and swapped in.</summary>
    public async Task RewriteAsync(string workingDirectory, string sessionId, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var path = FileFor(workingDirectory, sessionId);
        var gate = Gate(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = File.Exists(path) ? ReadFile(path) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(existing?.Title))
                sb.Append(JsonSerializer.Serialize(new Line { Op = TitleOp, Text = existing!.Title }, JsonOptions.Default)).Append('\n');
            if (existing is { CostUsd: > 0 })
                sb.Append(JsonSerializer.Serialize(new Line { Op = CostOp, Cost = existing.CostUsd }, JsonOptions.Default)).Append('\n');
            foreach (var m in messages)
                sb.Append(JsonSerializer.Serialize(new Line { Op = Msg, Message = m }, JsonOptions.Default)).Append('\n');
            await File.WriteAllTextAsync(tmp, sb.ToString(), Utf8, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally { gate.Release(); }
    }

    public void Delete(string workingDirectory, string sessionId)
    {
        var path = FileFor(workingDirectory, sessionId);
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task AppendAsync(string path, Line line, bool createIfMissing, CancellationToken ct)
    {
        if (!createIfMissing && !File.Exists(path)) return;

        var gate = Gate(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = JsonSerializer.Serialize(line, JsonOptions.Default) + "\n";
            // After a crash the last line may lack its newline; start a fresh line so two records never fuse.
            if (EndsWithoutNewline(path)) text = "\n" + text;
            await File.AppendAllTextAsync(path, text, Utf8, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static bool EndsWithoutNewline(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return false;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() != '\n';
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // ----- reading -----

    public async Task<StoredChatSession?> LoadAsync(string workingDirectory, string sessionId, CancellationToken ct = default)
    {
        var path = FileFor(workingDirectory, sessionId);
        if (!File.Exists(path)) return null;

        var gate = Gate(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return ReadFile(path); }
        finally { gate.Release(); }
    }

    /// <summary>Every stored session of the working directory (including empty ones), newest first.</summary>
    public async Task<IReadOnlyList<ChatSessionInfo>> ListAsync(string workingDirectory, CancellationToken ct = default)
    {
        var dir = DirectoryFor(workingDirectory);
        var list = new List<ChatSessionInfo>();
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var gate = Gate(file);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                StoredChatSession session;
                try { session = ReadFile(file); }
                finally { gate.Release(); }
                list.Add(ToInfo(id, session));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Skipping unreadable session file {File}", file); }
        }

        list.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return list;
    }

    public static ChatSessionInfo ToInfo(string sessionId, StoredChatSession s)
    {
        var count = s.Messages.Count(m => m.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content));
        var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title!.Trim() : TitleFromMessages(s.Messages);
        return new ChatSessionInfo(sessionId, title, s.CreatedAt, s.UpdatedAt, count, s.CostUsd);
    }

    /// <summary>The first line of the first user message, shortened; "Untitled session" when there is none.</summary>
    public static string TitleFromMessages(IEnumerable<Message> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
        if (first == null) return "Untitled session";
        var line = first.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Untitled session";
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    private StoredChatSession ReadFile(string path)
    {
        var session = new StoredChatSession
        {
            CreatedAt = File.GetCreationTimeUtc(path),
            UpdatedAt = File.GetLastWriteTimeUtc(path)
        };
        var sawFirst = false;
        var sawMessage = false;

        // Share-read so a session being written (the app is still running) can be listed.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            Line? line;
            try { line = JsonSerializer.Deserialize<Line>(raw, JsonOptions.Default); }
            catch (JsonException) { continue; } // a line cut short by a crash
            if (line == null) continue;

            if (!sawFirst) { session.CreatedAt = line.At; sawFirst = true; }
            switch (line.Op)
            {
                case Msg when line.Message != null:
                    session.Messages.Add(line.Message);
                    session.UpdatedAt = line.At;
                    sawMessage = true;
                    break;
                case Reset:
                    session.Messages.Clear();
                    break;
                case TitleOp:
                    session.Title = string.IsNullOrWhiteSpace(line.Text) ? null : line.Text!.Trim();
                    break;
                case CostOp when line.Cost.HasValue:
                    session.CostUsd = line.Cost.Value;
                    break;
            }
        }

        if (!sawMessage && sawFirst) session.UpdatedAt = session.CreatedAt;
        return session;
    }

    private SemaphoreSlim Gate(string path) => _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
}
