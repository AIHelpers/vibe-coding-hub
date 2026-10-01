using System.Security.Cryptography;
using System.Text;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Indexing.Semantic;

/// <summary>
/// Splits source files into embedding-friendly chunks using a line-window
/// strategy with overlap. Attempts to break on blank lines / braces when
/// possible to keep related code together.
/// </summary>
public sealed class CodeChunker
{
    private readonly int _targetLines;
    private readonly int _overlapLines;
    private readonly int _maxChars;

    /// <param name="targetLines">Approximate lines per chunk.</param>
    /// <param name="overlapLines">Lines of overlap between consecutive chunks.</param>
    /// <param name="maxChars">Hard char limit per chunk (safety cap).</param>
    public CodeChunker(int targetLines = 80, int overlapLines = 10, int maxChars = 4000)
    {
        if (targetLines <= 0) throw new ArgumentOutOfRangeException(nameof(targetLines));
        if (overlapLines < 0 || overlapLines >= targetLines) throw new ArgumentOutOfRangeException(nameof(overlapLines));
        _targetLines = targetLines;
        _overlapLines = overlapLines;
        _maxChars = maxChars;
    }

    /// <summary>Chunk a file's contents into <see cref="CodeChunk"/> records.</summary>
    public IReadOnlyList<CodeChunk> Chunk(
        string filePath,
        string content,
        string language,
        long fileWriteTimeUtcTicks)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<CodeChunk>();

        var lines = content.Split('\n');
        var chunks = new List<CodeChunk>();
        var i = 0;
        var n = lines.Length;

        while (i < n)
        {
            var start = i;
            var end = Math.Min(i + _targetLines - 1, n - 1);

            // Try to extend to a natural boundary (blank line or closing brace)
            var preferredEnd = end;
            for (var j = end; j > start + _targetLines / 2 && j < n - 1; j--)
            {
                var t = lines[j].Trim();
                if (t.Length == 0 || t == "}" || t == "});" || t == "}" || t == "end")
                {
                    preferredEnd = j;
                    break;
                }
            }
            end = preferredEnd;

            var slice = new StringBuilder();
            var charCount = 0;
            for (var j = start; j <= end; j++)
            {
                slice.Append(lines[j]);
                if (j < end) slice.Append('\n');
                charCount += lines[j].Length + 1;
                if (charCount >= _maxChars) { end = j; break; }
            }

            var chunkText = slice.ToString();
            var id = ChunkId(filePath, start + 1);
            chunks.Add(new CodeChunk
            {
                Id = id,
                FilePath = filePath,
                StartLine = start + 1,
                EndLine = end + 1,
                Language = language,
                Content = chunkText,
                FileWriteTimeUtcTicks = fileWriteTimeUtcTicks
            });

            // If we've consumed the whole file, stop; otherwise advance with overlap
            if (end >= n - 1) break;
            i = end + 1 - _overlapLines;
            if (i <= start) i = start + 1; // safety
        }

        return chunks;
    }

    private static string ChunkId(string filePath, int startLine)
    {
        var key = $"{filePath}:{startLine}";
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}