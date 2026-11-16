using System.Text.Json;
using AiCodeAgent.Core.Audit;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Keeps a review beside the run it is about: <c>&lt;run&gt;.review.json</c> next to a chat run's audit file, <c>review.json</c> in a
/// flow run's folder. It is a separate file because the audit trail is sealed when the run ends and a review comes later; unlike the
/// evidence pack it cannot be rebuilt, so it is never overwritten by a pack rebuild.
/// </summary>
public static class ReviewStore
{
    public const string ChatSuffix = ".review.json";
    public const string FlowFileName = "review.json";

    public static string PathFor(string auditPath)
    {
        var full = System.IO.Path.GetFullPath(auditPath);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        return string.Equals(System.IO.Path.GetFileName(full), AuditChain.FileName, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(dir, FlowFileName)
            : System.IO.Path.Combine(dir, System.IO.Path.GetFileName(full).Replace(AuditLocator.ChatSuffix, "", StringComparison.OrdinalIgnoreCase) + ChatSuffix);
    }

    /// <summary>The stored review, or null when there is none or the file is unreadable (a damaged file never breaks the pack).</summary>
    public static IndependentReview? TryRead(string auditPath)
    {
        try
        {
            var path = PathFor(auditPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<IndependentReview>(File.ReadAllText(path), EvidencePackStore.Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static bool TryWrite(string auditPath, IndependentReview review, ILogger? logger = null)
    {
        try
        {
            File.WriteAllText(PathFor(auditPath), JsonSerializer.Serialize(review, EvidencePackStore.Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger?.LogWarning(ex, "Could not write the review for {Path}", auditPath);
            return false;
        }
    }
}
