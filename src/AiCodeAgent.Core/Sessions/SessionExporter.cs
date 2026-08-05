using System.IO.Compression;
using System.Text.Json;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Serializes a <see cref="SessionBundle"/> to disk. Supports plain JSON
/// (`.agentsession.json`) and a `.zip`-based `.agentsession` bundle that
/// embeds checkpoint snapshots for cross-machine handoff.
/// </summary>
public static class SessionExporter
{
    public const string BundleEntryName = "session.json";
    public const string CheckpointsMetaEntryName = "checkpoints.meta.json";

    /// <summary>
    /// Export a bundle to a file. If the path ends with `.agentsession`,
    /// a zip bundle is written (with checkpoint snapshots embedded as files);
    /// otherwise plain JSON is written.
    /// </summary>
    public static async Task ExportAsync(
        SessionBundle bundle,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullPath = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (fullPath.EndsWith(".agentsession", StringComparison.OrdinalIgnoreCase))
        {
            await ExportZipAsync(bundle, fullPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExportJsonAsync(bundle, fullPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serialize a bundle to a JSON string.</summary>
    public static string ToJson(SessionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, JsonOptions.Pretty);
    }

    /// <summary>Deserialize a bundle from a JSON string.</summary>
    public static SessionBundle FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var bundle = JsonSerializer.Deserialize<SessionBundle>(json, JsonOptions.Default)
            ?? throw new InvalidDataException("Session bundle JSON is empty or invalid.");
        ValidateSchema(bundle);
        return bundle;
    }

    /// <summary>Validate the bundle's schema version.</summary>
    public static void ValidateSchema(SessionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.SchemaVersion != SessionBundle.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported session bundle schema version {bundle.SchemaVersion}. " +
                $"Expected {SessionBundle.CurrentSchemaVersion}.");
        }
    }

    private static async Task ExportJsonAsync(
        SessionBundle bundle,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var json = ToJson(bundle);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExportZipAsync(
        SessionBundle bundle,
        string fullPath,
        CancellationToken cancellationToken)
    {
        // Strip embedded snapshots from the JSON (they are stored as separate zip entries)
        var jsonBundle = new SessionBundle
        {
            SchemaVersion = bundle.SchemaVersion,
            SessionId = bundle.SessionId,
            CreatedAt = bundle.CreatedAt,
            Conversation = bundle.Conversation,
            ToolCallLog = bundle.ToolCallLog,
            Changeset = bundle.Changeset,
            CheckpointRefs = bundle.CheckpointRefs,
            CheckpointSnapshots = new List<CheckpointSnapshotDto>(), // embedded as files
            RolesUsed = bundle.RolesUsed,
            PromptPack = bundle.PromptPack
        };

        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        var jsonEntry = archive.CreateEntry(BundleEntryName, CompressionLevel.Optimal);
        await using (var entryStream = jsonEntry.Open())
        {
            await JsonSerializer.SerializeAsync(entryStream, jsonBundle, JsonOptions.Pretty, cancellationToken)
                .ConfigureAwait(false);
        }

        // Embed checkpoint snapshots as individual files under checkpoints/
        foreach (var snapshot in bundle.CheckpointSnapshots)
        {
            var safeId = SanitizeEntryName(snapshot.CheckpointId);
            var entry = archive.CreateEntry($"checkpoints/{safeId}.bak", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(snapshot.OriginalContent.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        // Preserve snapshot metadata (file path, turn, session) in a sidecar so
        // the snapshots can be rehydrated with full context on import.
        if (bundle.CheckpointSnapshots.Count > 0)
        {
            var metaEntry = archive.CreateEntry(CheckpointsMetaEntryName, CompressionLevel.Optimal);
            await using var metaStream = metaEntry.Open();
            await JsonSerializer.SerializeAsync(
                metaStream,
                bundle.CheckpointSnapshots,
                JsonOptions.Pretty,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SanitizeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}