using System.Text.Json;
using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Loads/saves prompt packs (section 5.4): a `.promptpack.json` file containing
/// role presets + reusable prompt templates, versionable in git alongside code.
/// </summary>
public class PromptPackService
{
    private readonly ILogger<PromptPackService> _logger;

    public PromptPackService(ILogger<PromptPackService> logger)
    {
        _logger = logger;
    }

    /// <summary>Serialize a prompt pack to JSON.</summary>
    public static string ToJson(PromptPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return JsonSerializer.Serialize(pack, JsonOptions.Pretty);
    }

    /// <summary>Deserialize a prompt pack from JSON.</summary>
    public static PromptPack FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var pack = JsonSerializer.Deserialize<PromptPack>(json, JsonOptions.Default)
            ?? throw new InvalidDataException("Prompt pack JSON is empty or invalid.");

        if (pack.SchemaVersion != PromptPack.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported prompt pack schema version {pack.SchemaVersion}. " +
                $"Expected {PromptPack.CurrentSchemaVersion}.");
        }

        return pack;
    }

    /// <summary>Save a prompt pack to disk.</summary>
    public async Task SaveAsync(PromptPack pack, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = ToJson(pack);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved prompt pack '{Name}' to {Path}", pack.Name, fullPath);
    }

    /// <summary>Load a prompt pack from disk.</summary>
    public async Task<PromptPack> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prompt pack not found: {fullPath}");

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var pack = FromJson(json);
        _logger.LogInformation("Loaded prompt pack '{Name}' from {Path}", pack.Name, fullPath);
        return pack;
    }

    /// <summary>
    /// Create a prompt pack from the current role presets + templates. Used to
    /// export the built-in roles into a versionable file.
    /// </summary>
    public static PromptPack CreateFromPresets(
        RolePresetLoader loader,
        string name,
        string? description = null,
        IEnumerable<PromptTemplate>? templates = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new PromptPack
        {
            SchemaVersion = PromptPack.CurrentSchemaVersion,
            Name = name,
            Description = description,
            Roles = loader.GetAllPresets().ToList(),
            Templates = templates?.ToList() ?? new List<PromptTemplate>()
        };
    }
}