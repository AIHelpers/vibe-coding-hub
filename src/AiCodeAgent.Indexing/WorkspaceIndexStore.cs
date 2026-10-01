using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using AiCodeAgent.Indexing.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Indexing;

/// <summary>
/// SQLite-backed store for the workspace index. Owns the schema and all
/// access to the database file at ~/.aiagent/index/<workspace-hash>.db.
///
/// Schema:
///   files(path TEXT PRIMARY KEY, last_modified INTEGER, hash TEXT)
///   symbols(name TEXT, kind TEXT, file_path TEXT, line INTEGER, workspace_id TEXT)
/// </summary>
public sealed class WorkspaceIndexStore : IDisposable
{
    private const string DefaultDirectoryName = ".aiagent";

    private readonly string _dbPath;
    private readonly string _workspaceId;
    private readonly ILogger<WorkspaceIndexStore> _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();

    public string WorkspaceId => _workspaceId;
    public string DatabasePath => _dbPath;

    public WorkspaceIndexStore(
        string workspaceRoot,
        ILogger<WorkspaceIndexStore> logger,
        string? indexRoot = null)
    {
        _logger = logger;
        _workspaceId = AiCodeAgent.Indexing.WorkspaceId.Compute(workspaceRoot);

        var root = indexRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultDirectoryName,
            "index");

        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, $"{_workspaceId}.db");
    }

    /// <summary>Open (creating schema if needed) the connection.</summary>
    public void Open()
    {
        lock (_sync)
        {
            if (_connection != null)
                return;

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            _connection = new SqliteConnection(csb.ToString());
            _connection.Open();
            InitializeSchema();
        }
    }

    /// <summary>Create the tables if they do not exist.</summary>
    private void InitializeSchema()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS files (
                path TEXT PRIMARY KEY,
                last_modified INTEGER NOT NULL,
                hash TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS symbols (
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                file_path TEXT NOT NULL,
                line INTEGER NOT NULL,
                workspace_id TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
            CREATE INDEX IF NOT EXISTS idx_symbols_workspace ON symbols(workspace_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upsert a batch of file records (replacing the whole set atomically).</summary>
    public async Task ReplaceAllFilesAsync(
        IReadOnlyCollection<IndexedFile> files,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

            await using var tx = await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                as SqliteTransaction ?? throw new InvalidOperationException("Failed to begin transaction.");

            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM files;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var upsert = _connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT OR REPLACE INTO files(path, last_modified, hash)
                    VALUES ($path, $lastModified, $hash);
                    """;
                var pPath = upsert.Parameters.Add("$path", SqliteType.Text);
                var pLast = upsert.Parameters.Add("$lastModified", SqliteType.Integer);
                var pHash = upsert.Parameters.Add("$hash", SqliteType.Text);

                foreach (var file in files)
                {
                    pPath.Value = file.Path;
                    pLast.Value = file.LastModified.ToUniversalTime().Ticks;
                    pHash.Value = file.Hash;
                    await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Replaced file index with {Count} entries", files.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Insert or update a single file record.</summary>
    public async Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files(path, last_modified, hash)
                VALUES ($path, $lastModified, $hash)
                ON CONFLICT(path) DO UPDATE SET
                    last_modified = excluded.last_modified,
                    hash = excluded.hash;
                """;
            cmd.Parameters.AddWithValue("$path", file.Path);
            cmd.Parameters.AddWithValue("$lastModified", file.LastModified.ToUniversalTime().Ticks);
            cmd.Parameters.AddWithValue("$hash", file.Hash);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Remove a file record and its symbols.</summary>
    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var symCmd = _connection!.CreateCommand();
            symCmd.CommandText = "DELETE FROM symbols WHERE file_path = $path;";
            symCmd.Parameters.AddWithValue("$path", path);
            await symCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Replace all symbols for a file (delete + insert).</summary>
    public async Task ReplaceSymbolsForFileAsync(
        string path,
        IReadOnlyCollection<IndexedSymbol> symbols,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

            await using var tx = await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                as SqliteTransaction ?? throw new InvalidOperationException("Failed to begin transaction.");

            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM symbols WHERE file_path = $path;";
                clear.Parameters.AddWithValue("$path", path);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO symbols(name, kind, file_path, line, workspace_id)
                    VALUES ($name, $kind, $filePath, $line, $workspaceId);
                    """;
                var pName = insert.Parameters.Add("$name", SqliteType.Text);
                var pKind = insert.Parameters.Add("$kind", SqliteType.Text);
                var pFile = insert.Parameters.Add("$filePath", SqliteType.Text);
                var pLine = insert.Parameters.Add("$line", SqliteType.Integer);
                var pWs = insert.Parameters.Add("$workspaceId", SqliteType.Text);

                foreach (var sym in symbols)
                {
                    pName.Value = sym.Name;
                    pKind.Value = sym.Kind;
                    pFile.Value = sym.FilePath;
                    pLine.Value = sym.Line;
                    pWs.Value = sym.WorkspaceId;
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Query files by fuzzy substring on path / name.</summary>
    public async Task<List<FileMatch>> QueryFilesAsync(
        FileQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        var filter = options.Filter ?? string.Empty;
        var limit = Math.Clamp(options.Limit, 1, 500);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT path,
                   CASE WHEN instr(lower(path), lower($filter)) > 0 THEN 2 ELSE 1 END AS score
            FROM files
            WHERE lower(path) LIKE '%' || lower($filter) || '%'
               OR lower(path) LIKE '%/' || lower($filter) || '%'
            ORDER BY score DESC, path ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$filter", filter);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<FileMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            var score = reader.GetInt32(1);
            results.Add(new FileMatch(
                Path: path,
                FileName: Path.GetFileName(path),
                Score: score));
        }

        return results;
    }

    /// <summary>Query symbols by fuzzy name match.</summary>
    public async Task<List<SymbolMatch>> QuerySymbolsAsync(
        SymbolQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        var filter = options.Filter ?? string.Empty;
        var limit = Math.Clamp(options.Limit, 1, 500);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT name, kind, file_path, line,
                   CASE WHEN name = $filter THEN 3
                        WHEN lower(name) LIKE $prefix THEN 1
                        ELSE 2 END AS score
            FROM symbols
            WHERE workspace_id = $workspaceId
              AND ($filter = '' OR lower(name) LIKE '%' || lower($filter) || '%')
              AND ($filePath = '' OR file_path = $filePath)
              AND ($kind = '' OR kind = $kind)
            ORDER BY score ASC, name ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$filter", filter);
        cmd.Parameters.AddWithValue("$prefix", filter + "%");
        cmd.Parameters.AddWithValue("$workspaceId", _workspaceId);
        cmd.Parameters.AddWithValue("$filePath", options.FilePath ?? string.Empty);
        cmd.Parameters.AddWithValue("$kind", options.Kind ?? string.Empty);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<SymbolMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SymbolMatch(
                Name: reader.GetString(0),
                Kind: reader.GetString(1),
                FilePath: reader.GetString(2),
                Line: reader.GetInt32(3),
                Score: reader.GetInt32(4)));
        }

        return results;
    }

    /// <summary>Get all indexed files.</summary>
    public async Task<List<IndexedFile>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT path, last_modified, hash FROM files ORDER BY path ASC;";

        var results = new List<IndexedFile>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedFile(
                Path: reader.GetString(0),
                LastModified: new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                Hash: reader.GetString(2)));
        }

        return results;
    }

    /// <summary>Get a single file record by path, or null.</summary>
    public async Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT path, last_modified, hash FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new IndexedFile(
                Path: reader.GetString(0),
                LastModified: new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                Hash: reader.GetString(2));
        }

        return null;
    }

    /// <summary>Total number of indexed files.</summary>
    public async Task<int> GetFileCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    /// <summary>Total number of indexed symbols.</summary>
    public async Task<int> GetSymbolCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_connection != null)
                return;
        }
        await Task.Run(Open, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        lock (_sync)
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }
    }
}