using System.Globalization;
using System.IO;
using System.Text;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Indexing.Semantic;

/// <summary>
/// Retrieves top-K relevant chunks for a query and formats them into a
/// citation-bearing context block suitable for LLM prompting.
/// </summary>
public sealed class Retriever
{
    private readonly SemanticIndex _index;
    private readonly int _defaultTopK;

    public Retriever(SemanticIndex index, int defaultTopK = 8)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _defaultTopK = defaultTopK;
    }

    public record RetrievalResult(IReadOnlyList<SemanticSearchResult> Chunks, string ContextBlock);

    public async Task<RetrievalResult> RetrieveAsync(string query, int? topK = null, CancellationToken cancellationToken = default)
    {
        var k = topK ?? _defaultTopK;
        var chunks = await _index.SearchAsync(query, k, cancellationToken).ConfigureAwait(false);
        var block = FormatContext(chunks);
        return new RetrievalResult(chunks, block);
    }

    /// <summary>
    /// Builds the prompt context block with explicit file/line headers and
    /// citation indices ([1], [2], ...) that the LLM is instructed to use.
    /// </summary>
    public static string FormatContext(IReadOnlyList<SemanticSearchResult> chunks)
    {
        if (chunks.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("The following code chunks are relevant to the question. Each is tagged with a citation index you should reference.");
        sb.AppendLine();

        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i].Chunk;
            var header = $"[{i + 1}] file: {c.FilePath}, lines {c.StartLine}-{c.EndLine}";
            if (!string.IsNullOrEmpty(c.Symbol))
                header += $" ({c.Symbol})";
            sb.AppendLine(header);
            sb.AppendLine(c.Content);
            sb.AppendLine();
        }

        sb.AppendLine("When answering, cite sources using [index] markers (e.g. [1], [2]) corresponding to the chunks above. Only cite chunks that support your answer.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the final user prompt combining the question and retrieved context.
    /// </summary>
    public static string BuildPrompt(string question, string contextBlock)
    {
        if (string.IsNullOrEmpty(contextBlock))
        {
            return $"Question: {question}\n\n(No indexed code context was available; answer from general knowledge if possible.)";
        }

        return $"""
            Answer the following question about the codebase using ONLY the provided code chunks as ground truth.
            If the answer is not contained in the chunks, say so explicitly.

            {contextBlock}

            Question: {question}
            """;
    }

    /// <summary>
    /// Maps citation indices found in LLM output back to chunk references.
    /// Recognizes [1], [2], and file:line forms.
    /// </summary>
    public static IReadOnlyList<Citation> ParseCitations(string output, IReadOnlyList<SemanticSearchResult> chunks)
    {
        var citations = new List<Citation>();
        if (chunks.Count == 0) return citations;

        // [n] numeric citations
        var numeric = System.Text.RegularExpressions.Regex.Matches(output, @"\[(\d+)\]");
        var seen = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in numeric)
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
            {
                if (idx >= 1 && idx <= chunks.Count && seen.Add(idx))
                {
                    citations.Add(new Citation(idx, chunks[idx - 1].Chunk));
                }
            }
        }

        // [file.ext:lines] or [file.ext lines a-b]
        var filePattern = System.Text.RegularExpressions.Regex.Matches(output, @"\[([^\]\[]+?)(?::|\s+lines\s+)(\d+)(?:-(\d+))?\]");
        foreach (System.Text.RegularExpressions.Match m in filePattern)
        {
            var file = m.Groups[1].Value.Trim();
            var start = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var end = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : start;

            var matchIndex = -1;
            for (var i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                if (c.Chunk.FilePath.EndsWith(file, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(c.Chunk.FilePath), file, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                var idx = matchIndex + 1;
                if (seen.Add(idx))
                    citations.Add(new Citation(idx, chunks[matchIndex].Chunk));
            }
        }

        return citations;
    }

    public sealed record Citation(int Index, CodeChunk Chunk);
}