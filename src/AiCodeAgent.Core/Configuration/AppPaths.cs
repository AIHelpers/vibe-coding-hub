namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Locations of the per-user data folder (<c>~/.aiagent</c> by default). Set the <c>AIAGENT_HOME</c>
/// environment variable to use another folder, for portable installs and for tests. Every property is
/// evaluated on read, so a change to the variable takes effect immediately.
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable that replaces <c>~/.aiagent</c> as the per-user root.</summary>
    public const string HomeEnvironmentVariable = "AIAGENT_HOME";

    /// <summary>Name of the per-user folder inside the home directory.</summary>
    public const string FolderName = ".aiagent";

    /// <summary>The per-user root: <c>$AIAGENT_HOME</c> when set, otherwise <c>~/.aiagent</c>.</summary>
    public static string UserRoot =>
        ResolveUserRoot(Environment.GetEnvironmentVariable(HomeEnvironmentVariable), UserHome);

    public static string Presets => Path.Combine(UserRoot, "presets");
    public static string Pipelines => Path.Combine(UserRoot, "pipelines");
    public static string Skills => Path.Combine(UserRoot, "skills");
    public static string Characters => Path.Combine(UserRoot, "characters");
    /// <summary>Installed packs: <c>packs/&lt;name&gt;@&lt;ref&gt;/</c> plus <c>packs/installed.json</c>.</summary>
    public static string Packs => Path.Combine(UserRoot, "packs");
    public static string Runs => Path.Combine(UserRoot, "runs");
    /// <summary>Saved flow runs: <c>runs/flows/&lt;flow&gt;/&lt;runId&gt;/</c>.</summary>
    public static string FlowRuns => Path.Combine(Runs, "flows");
    public static string Checkpoints => Path.Combine(UserRoot, "checkpoints");
    public static string Logs => Path.Combine(UserRoot, "logs");
    public static string Sessions => Path.Combine(UserRoot, "sessions");
    public static string Index => Path.Combine(UserRoot, "index");
    public static string Pricing => Path.Combine(UserRoot, "pricing.json");
    /// <summary>Optional overrides for the built-in model list (<c>models.json</c>).</summary>
    public static string Models => Path.Combine(UserRoot, "models.json");

    /// <summary>
    /// The resolution rule behind <see cref="UserRoot"/>: a non-blank override wins; otherwise
    /// <c>&lt;home&gt;/.aiagent</c>; without a home directory the folder is relative to the working directory.
    /// </summary>
    public static string ResolveUserRoot(string? overrideDirectory, string? userHome)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory.Trim());

        return string.IsNullOrEmpty(userHome) ? FolderName : Path.Combine(userHome, FolderName);
    }

    private static string? UserHome
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : home;
        }
    }
}
