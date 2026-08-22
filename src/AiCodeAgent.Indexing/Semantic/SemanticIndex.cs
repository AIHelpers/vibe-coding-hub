using System.Collections.Concurrent;
using AiCodeAgent.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Indexing.Semantic;

/// <summary>
/// Builds and maintains a persistent semantic index over a workspace:
/// chunks files, embeds them, persists vectors to SQLite, and exposes
/// search via <see cref="VectorStore"/>.
/// </summary>
public sealed class SemanticIndex : IAsyncDisposable
{
    private readonly CodeChunker _chunker;
    private readonly EmbeddingService _embeddings;
    private readonly VectorStore _store;
    private readonly ILogger<SemanticIndex> _logger;
    private readonly string _dbPath;
    private readonly ConcurrentDictionary<string, long> _fileTimestamps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs", ".java",
        ".kt", ".swift", ".rb", ".php", ".c", ".h", ".cpp", ".hpp", ".cc",
        ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".xml", ".sql",
        ".ps1", ".sh", ".bash"
    };

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "target", "venv", "__pycache__", ".venv"
    };

    public SemanticIndex(
        string dbDirectory,
        CodeChunker chunker,
        EmbeddingService embeddings,
        VectorStore store,
        ILogger<SemanticIndex> logger)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(dbDirectory);
        _dbPath = Path.Combine(dbDirectory, "semantic.db");
        EnsureSchema();
        LoadAllIntoMemory();
    }

    public int ChunkCount => _store.Count;
    public VectorStore Store => _store;

    public async Task<int> IndexWorkspaceAsync(string workspaceRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = DiscoverFiles(workspaceRoot);
        var processed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            var ticks = info.LastWriteTimeUtc.Ticks;

            if (_fileTimestamps.TryGetValue(file, out var existing) && existing == ticks)
            {
                processed++;
                continue;
            }

            progress?.Report($"Indexing: {Path.GetFileName(file)}");
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var lang = InferLanguage(file);
                var chunks = _chunker.Chunk(file, content, lang, ticks);

                if (chunks.Count > 0)
                {
                    await _embeddings.EmbedChunksAsync(chunks, cancellationToken).ConfigureAwait(false);
                    _store.RemoveFile(file);
                    _store.UpsertRange(chunks);
                    PersistChunks(chunks);
                }
                else
                {
                    _store.RemoveFile(file);
                    DeleteFileChunks(file);
                }

                _fileTimestamps[file] = ticks;
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to index {File}", file);
            }
        }

        _logger.LogInformation("Indexed {Count} files, {Chunks} chunks total", processed, _store.Count);
        return processed;
    }

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string query, int topK = 8, CancellationToken cancellationToken = default)
    {
        if (_store.Count == 0) return Array.Empty<SemanticSearchResult>();
        var qv = await _embeddings.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        return _store.Search(qv, topK);
    }

    private static IEnumerable<string> DiscoverFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> enumeration;
        try
        {
            enumeration = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var path in enumeration)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => IgnoredDirs.Contains(s))) continue;

            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext)) continue;

            if (Path.GetFileName(path).StartsWith('.')) continue;

            yield return path;
        }
    }

    private static string InferLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".kt" => "kotlin",
            ".swift" => "swift",
            ".rb" => "ruby",
            ".php" => "php",
            ".c" or ".h" => "c",
            ".cpp" or ".hpp" or ".cc" => "cpp",
            ".md" or ".txt" => "text",
            ".json" or ".yaml" or ".yml" or ".toml" or ".xml" => "config",
            ".sql" => "sql",
            ".ps1" => "powershell",
            ".sh" or ".bash" => "bash",
            _ => "text"
        };
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                language TEXT NOT NULL,
                symbol TEXT,
                content TEXT NOT NULL,
                file_write_ticks INTEGER NOT NULL,
                embedding BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_file ON chunks(file_path);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_dbPath}");

    private void LoadAllIntoMemory()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, file_path, start_line, end_line, language, symbol, content, file_write_ticks, embedding FROM chunks";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var filePath = reader.GetString(1);
                var start = reader.GetInt32(2);
                var end = reader.GetInt32(3);
                var lang = reader.GetString(4);
                var symbol = reader.IsDBNull(5) ? null : reader.GetString(5);
                var content = reader.GetString(6);
                var ticks = reader.GetInt64(7);
                var blob = ReadAllBytes(reader, 8);

                var chunk = new CodeChunk
                {
                    Id = id,
                    FilePath = filePath,
                    StartLine = start,
                    EndLine = end,
                    Language = lang,
                    Symbol = symbol,
                    Content = content,
                    FileWriteTimeUtcTicks = ticks,
                    Embedding = DecodeFloats(blob)
                };
                _store.Upsert(chunk);
                _fileTimestamps[filePath] = ticks;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load semantic index from {Path}", _dbPath);
        }
    }

    private static byte[] ReadAllBytes(SqliteDataReader reader, int ordinal)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        long offset = 0;
        while (true)
        {
            var read = (int)reader.GetBytes(ordinal, offset, buffer, 0, buffer.Length);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            offset += read;
        }
        return ms.ToArray();
    }

    private void PersistChunks(IReadOnlyList<CodeChunk> chunks)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var c in chunks)
        {
            if (c.Embedding is null) continue;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO chunks (id, file_path, start_line, end_line, language, symbol, content, file_write_ticks, embedding)
                VALUES (@id, @fp, @sl, @el, @lang, @sym, @content, @fwt, @emb)
                """;
            cmd.Parameters.AddWithValue("@id", c.Id);
            cmd.Parameters.AddWithValue("@fp", c.FilePath);
            cmd.Parameters.AddWithValue("@sl", c.StartLine);
            cmd.Parameters.AddWithValue("@el", c.EndLine);
            cmd.Parameters.AddWithValue("@lang", c.Language);
            cmd.Parameters.AddWithValue("@sym", (object?)c.Symbol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@content", c.Content);
            cmd.Parameters.AddWithValue("@fwt", c.FileWriteTimeUtcTicks);
            cmd.Parameters.AddWithValue("@emb", EncodeFloats(c.Embedding));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void DeleteFileChunks(string filePath)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE file_path = @fp";
        cmd.Parameters.AddWithValue("@fp", filePath);
        cmd.ExecuteNonQuery();
    }

    private static byte[] EncodeFloats(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DecodeFloats(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
    }
}