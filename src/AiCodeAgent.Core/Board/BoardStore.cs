using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Board;

/// <summary>
/// Keeps the cards of one project's board in a JSON file. The file lives in the per-user folder
/// (<c>~/.aiagent/boards/&lt;project&gt;-&lt;hash&gt;/board.json</c>), not in the repository: it holds machine-local paths and
/// branch names that must never be committed. Writes are atomic, and a damaged file reads as an empty board instead of
/// breaking the app.
/// </summary>
public sealed class BoardStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "board.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ILogger? _logger;

    public BoardStore(string path, ILogger? logger = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _logger = logger;
    }

    public string Path { get; }

    /// <summary>The store of the board of <paramref name="projectDirectory"/> under the per-user folder.</summary>
    public static BoardStore ForProject(string projectDirectory, ILogger? logger = null) =>
        new(System.IO.Path.Combine(AppPaths.UserRoot, "boards", FolderFor(projectDirectory), FileName), logger);

    /// <summary>A stable folder name per project: its name plus a short hash of the full path, so two projects named alike stay apart.</summary>
    public static string FolderFor(string projectDirectory)
    {
        var full = System.IO.Path.GetFullPath(projectDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = new string(System.IO.Path.GetFileName(full).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        if (name.Length == 0) name = "project";
        if (name.Length > 40) name = name[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..10].ToLowerInvariant();
        return $"{name}-{hash}";
    }

    public IReadOnlyList<BoardCard> Load()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<BoardCard>();
            var file = JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(Path), Json);
            return file?.Cards?.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToList() ?? new List<BoardCard>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not read the board {Path}; starting with an empty board", Path);
            return Array.Empty<BoardCard>();
        }
    }

    /// <summary>Writes the cards; false (and a log line) when the file cannot be written. The board keeps working in memory.</summary>
    public bool TrySave(IEnumerable<BoardCard> cards)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new BoardFile { Version = CurrentVersion, Cards = cards.ToList() }, Json));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not save the board {Path}", Path);
            return false;
        }
    }

    private sealed class BoardFile
    {
        public int Version { get; set; }
        public List<BoardCard>? Cards { get; set; }
    }
}
