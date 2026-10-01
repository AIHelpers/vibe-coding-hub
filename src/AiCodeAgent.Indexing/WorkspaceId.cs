using System.Security.Cryptography;
using System.Text;

namespace AiCodeAgent.Indexing;

/// <summary>
/// Computes a stable workspace hash used for the index database filename
/// and for the symbols table's workspace_id column.
/// </summary>
public static class WorkspaceId
{
    /// <summary>Compute a stable 12-hex-digit hash for a workspace root path.</summary>
    public static string Compute(string workspaceRoot)
    {
        var full = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = OperatingSystem.IsWindows()
            ? full.ToLowerInvariant()
            : full;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}