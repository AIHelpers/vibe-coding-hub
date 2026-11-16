using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Audit;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Builds the pack of a finished run and keeps it next to the audit trail it came from: <c>&lt;run&gt;.evidence.json</c> beside a chat
/// run's audit file, <c>evidence.json</c> in a flow run's folder. The audit trail stays the source of truth; the stored file is a
/// convenience that can be rebuilt at any time.
/// </summary>
public static class EvidencePackStore
{
    public const string ChatSuffix = ".evidence.json";
    public const string FlowFileName = "evidence.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string PathFor(string auditPath)
    {
        var full = System.IO.Path.GetFullPath(auditPath);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        return string.Equals(System.IO.Path.GetFileName(full), AuditChain.FileName, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(dir, FlowFileName)
            : System.IO.Path.Combine(dir, System.IO.Path.GetFileName(full).Replace(AuditLocator.ChatSuffix, "", StringComparison.OrdinalIgnoreCase) + ChatSuffix);
    }

    /// <summary>Reads, verifies and summarises an audit file (or an exported zip).</summary>
    public static EvidencePack Build(string auditPath)
    {
        var verification = AuditChainVerifier.Verify(auditPath);
        var pack = EvidencePackBuilder.Build(AuditExporter.Read(auditPath), verification);
        pack.AuditPath = System.IO.Path.GetFullPath(auditPath);
        pack.Review = ReviewStore.TryRead(auditPath);
        return pack;
    }

    /// <summary>Builds and stores the pack. Never throws: a task does not fail because its evidence could not be written.</summary>
    public static EvidencePack? TryWrite(string auditPath, ILogger? logger = null)
    {
        try
        {
            var pack = Build(auditPath);
            File.WriteAllText(PathFor(auditPath), JsonSerializer.Serialize(pack, Json));
            // The review lives in its own file; the stored pack repeats it for readers that only open this one.
            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            logger?.LogWarning(ex, "Could not write the evidence pack for {Path}", auditPath);
            return null;
        }
    }

    public static string ToJson(EvidencePack pack) => JsonSerializer.Serialize(pack, Json);
}
