using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiCodeAgent.Core.Audit;

/// <summary>
/// The line format of <c>audit.jsonl</c> (E7-S2). One entry per line, always exactly:
/// <code>{"seq":N,"prev":"&lt;hash of line N-1&gt;","hash":"&lt;this line's hash&gt;","entry":{...}}</code>
/// where <c>hash = SHA-256(prev "\n" seq "\n" entry-json)</c> in lower-case hex and the first line's <c>prev</c> is 64 zeros.
/// Because every hash covers the one before it, changing, removing, inserting or reordering any line breaks the chain from that
/// point on, and <see cref="AuditChainVerifier"/> names the line.
/// </summary>
public static class AuditChain
{
    public const string FileName = "audit.jsonl";
    public static readonly string GenesisHash = new('0', 64);

    public static string ComputeHash(string prev, long seq, string entryJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{prev}\n{seq}\n{entryJson}"))).ToLowerInvariant();

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The exact text of one line (without the newline). <paramref name="entryJson"/> must be compact JSON on one line.</summary>
    public static string BuildLine(long seq, string prev, string entryJson) =>
        $"{{\"seq\":{seq},\"prev\":\"{prev}\",\"hash\":\"{ComputeHash(prev, seq, entryJson)}\",\"entry\":{entryJson}}}";
}

/// <summary>Appends entries to an audit file. Append-only: the file is opened for append and every line is flushed as it is written.</summary>
public sealed class AuditChainWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private long _seq;
    private string _head;
    private bool _disposed;

    /// <summary>Opens (creating, or continuing) the file at <paramref name="path"/>.</summary>
    public AuditChainWriter(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        (_seq, _head) = ReadTail(Path);
        _stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public string Path { get; }

    /// <summary>Hash of the last line written (the genesis hash while the file is empty).</summary>
    public string HeadHash { get { lock (_gate) return _head; } }

    public long Count { get { lock (_gate) return _seq; } }

    /// <summary>Appends one entry and returns the line's hash.</summary>
    public string Append(string entryJson)
    {
        if (entryJson.Contains('\n') || entryJson.Contains('\r'))
            throw new ArgumentException("An audit entry must be compact JSON on one line.", nameof(entryJson));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var seq = _seq + 1;
            var line = AuditChain.BuildLine(seq, _head, entryJson);
            var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _seq = seq;
            _head = AuditChain.ComputeHash(_head, seq, entryJson);
            return _head;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }

    private static (long Seq, string Head) ReadTail(string path)
    {
        if (!File.Exists(path)) return (0, AuditChain.GenesisHash);
        string? last = null;
        long count = 0;
        foreach (var line in AuditFiles.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            last = line;
            count++;
        }
        if (last == null) return (0, AuditChain.GenesisHash);
        try
        {
            using var doc = JsonDocument.Parse(last);
            return (doc.RootElement.GetProperty("seq").GetInt64(), doc.RootElement.GetProperty("hash").GetString() ?? AuditChain.GenesisHash);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"Cannot continue '{path}': its last line is damaged (line {count}). Run 'aiagent audit verify' on it.");
        }
    }
}

/// <summary>Why a chain check failed: the 1-based line to look at and what is wrong with it.</summary>
public sealed record AuditFailure(int Line, string Problem);

/// <summary>Result of <see cref="AuditChainVerifier.Verify(IEnumerable{string})"/>.</summary>
public sealed record AuditVerification(
    int Lines,
    string HeadHash,
    AuditFailure? Failure,
    bool HasEndRecord,
    IReadOnlyList<string> Warnings)
{
    public bool Ok => Failure == null;
}

/// <summary>Checks the hash chain of an audit file.</summary>
public static class AuditChainVerifier
{
    public static AuditVerification Verify(string path)
    {
        if (!File.Exists(path))
            return new AuditVerification(0, AuditChain.GenesisHash, new AuditFailure(0, $"File not found: {path}"), false, Array.Empty<string>());
        if (string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return Verify(AuditFiles.ReadZipLines(path));
        return Verify(AuditFiles.ReadLines(path));
    }

    /// <summary>
    /// Verifies the lines of a file. A trailing carriage return is ignored (a Windows editor may add it); anything else
    /// — a changed character, a re-saved hash, a removed, added or swapped line — fails and names the line.
    /// </summary>
    public static AuditVerification Verify(IEnumerable<string> lines)
    {
        var prevHash = AuditChain.GenesisHash;
        var n = 0;
        var entries = 0;
        var hasEnd = false;
        var warnings = new List<string>();
        var pendingBlank = false;

        AuditVerification Fail(int line, string problem) => new(n, prevHash, new AuditFailure(line, problem), hasEnd, warnings);

        foreach (var rawLine in lines)
        {
            var raw = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            n++;
            if (raw.Length == 0)
            {
                // Blank lines are only fine at the very end of the file.
                pendingBlank = true;
                continue;
            }
            if (pendingBlank)
                return Fail(n - 1, "empty line inside the log");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { return Fail(n, "not valid JSON (the line was edited)"); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("seq", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number || !seqEl.TryGetInt64(out var seq)
                    || !root.TryGetProperty("prev", out var prevEl) || prevEl.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("hash", out var hashEl) || hashEl.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("entry", out var entryEl) || entryEl.ValueKind != JsonValueKind.Object)
                    return Fail(n, "is not an audit line (seq, prev, hash and entry are required)");

                var prev = prevEl.GetString()!;
                var entryRaw = entryEl.GetRawText();

                // 1. The line must be byte-for-byte what the writer produced (this also catches an edited "hash" field).
                var rebuilt = AuditChain.BuildLine(seq, prev, entryRaw);
                if (!string.Equals(rebuilt, raw, StringComparison.Ordinal))
                    return Fail(n, "was modified (its content does not match its hash)");

                // 2. Numbering: a gap means a line was removed, a repeat or a drop back means lines were added or reordered.
                if (seq != n)
                    return Fail(n, $"has sequence number {seq} but is line {n}: a line was removed, added or reordered before it");

                // 3. The chain: this line must point at the line before it.
                if (!string.Equals(prev, prevHash, StringComparison.Ordinal))
                    return n == 1
                        ? Fail(1, "does not start the chain (the log was cut at the front, or the first line was replaced)")
                        : Fail(n - 1, $"was modified or replaced: line {n} no longer chains to it");

                prevHash = hashEl.GetString()!;
                entries++;
                if (entryEl.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "run.end")
                    hasEnd = true;
                else if (hasEnd)
                    return Fail(n, "follows the closing run.end entry: entries were appended after the run ended");
            }
        }

        var count = entries;
        if (count == 0) warnings.Add("The file has no entries.");
        else if (!hasEnd) warnings.Add("No closing run.end entry: the run is still active, was interrupted, or the end of the log was cut off.");
        return new AuditVerification(count, prevHash, null, hasEnd, warnings);
    }
}

/// <summary>Reading helpers shared by the verifier, the exporter and the CLI.</summary>
public static class AuditFiles
{
    /// <summary>
    /// The lines of a file that may still be open for writing by the run that produces it (so an active run can be verified).
    /// </summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The lines of <c>audit.jsonl</c> inside an exported zip.</summary>
    public static IEnumerable<string> ReadZipLines(string zipPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(AuditChain.FileName)
            ?? throw new InvalidDataException($"'{zipPath}' has no {AuditChain.FileName}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
