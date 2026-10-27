using System.Reflection;

namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Loads the model catalog: the built-in <c>models.json</c> (embedded from <c>AiCodeAgent.Providers</c>), overlaid
/// with the user's <c>~/.aiagent/models.json</c> when that file exists. A broken user file is ignored.
/// </summary>
public static class ModelCatalogLoader
{
    /// <summary>Logical name of the embedded copy of <c>src/AiCodeAgent.Providers/models.json</c>.</summary>
    public const string ResourceName = "AiCodeAgent.Providers.models.json";

    private static readonly object Gate = new();
    private static ModelCatalog? _default;

    /// <summary>Where the user can override or extend the built-in list.</summary>
    public static string UserFilePath => AppPaths.Models;

    /// <summary>The catalog the app uses: built-in plus the user's file. Loaded once; <see cref="Reload"/> re-reads it.</summary>
    public static ModelCatalog Default
    {
        get
        {
            lock (Gate) return _default ??= Load(UserFilePath);
        }
    }

    /// <summary>Re-reads the user's file (used by the refresh button).</summary>
    public static ModelCatalog Reload()
    {
        lock (Gate)
        {
            _default = Load(UserFilePath);
            return _default;
        }
    }

    /// <summary>Test hook: use this catalog as the default (null restores loading from disk).</summary>
    public static void SetDefaultForTests(ModelCatalog? catalog)
    {
        lock (Gate) _default = catalog;
    }

    /// <summary>The built-in list only; empty if the resource is missing.</summary>
    public static ModelCatalog LoadBuiltIn()
    {
        try
        {
            using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return ModelCatalog.Empty;
            using var reader = new StreamReader(stream);
            return ModelCatalog.Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return ModelCatalog.Empty;
        }
    }

    /// <summary>The built-in list overlaid with <paramref name="userFile"/> (when it exists and parses).</summary>
    public static ModelCatalog Load(string? userFile)
    {
        var builtIn = LoadBuiltIn();
        if (string.IsNullOrWhiteSpace(userFile) || !File.Exists(userFile)) return builtIn;
        try
        {
            return builtIn.Overlay(ModelCatalog.Parse(File.ReadAllText(userFile)));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return builtIn;
        }
    }
}
