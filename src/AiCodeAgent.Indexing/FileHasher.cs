using System.Security.Cryptography;

namespace AiCodeAgent.Indexing;

/// <summary>
/// Computes a fast content hash for files so the indexer can detect
/// renames/replacements even when timestamps are unchanged.
/// </summary>
public static class FileHasher
{
    /// <summary>Compute a stable hex hash of a file's contents.</summary>
    public static string ComputeHash(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Read file metadata without content hashing.</summary>
    public static DateTime GetLastModifiedUtc(string filePath) =>
        File.GetLastWriteTimeUtc(filePath);
}