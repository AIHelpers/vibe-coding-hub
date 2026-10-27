namespace AiCodeAgent.Core.Configuration;

public class AppConfiguration
{
    public string DefaultProvider { get; set; } = "openai";
    public Dictionary<string, ProviderConfiguration> Providers { get; set; } = new();
    public AgentConfiguration Agent { get; set; } = new();
    public UiConfiguration Ui { get; set; } = new();
    
    public static AppConfiguration GetDefaults() => new()
    {
        Providers = new()
        {
            ["openai"] = new()
            {
                Name = "openai",
                BaseUrl = "https://api.openai.com",
                DefaultModel = ModelCatalogLoader.Default.DefaultModel("openai") ?? "gpt-5.6-terra",
                TimeoutSeconds = 120
            },
            ["anthropic"] = new()
            {
                Name = "anthropic",
                BaseUrl = "https://api.anthropic.com",
                DefaultModel = ModelCatalogLoader.Default.DefaultModel("anthropic") ?? "claude-sonnet-5-5",
                TimeoutSeconds = 120
            },
            ["ollama"] = new()
            {
                Name = "ollama",
                BaseUrl = "http://localhost:11434",
                DefaultModel = ModelCatalogLoader.Default.DefaultModel("ollama") ?? "llama3.1",
                TimeoutSeconds = 300
            },
            ["lmstudio"] = new()
            {
                Name = "lmstudio",
                BaseUrl = "http://localhost:1234",
                DefaultModel = "local-model",
                TimeoutSeconds = 300
            }
        }
    };
}

public class AgentConfiguration
{
    public int MaxIterations { get; set; } = 50;
    public int MaxTokens { get; set; } = 150_000;
    public bool AutoApprove { get; set; } = false;
    public bool EnableMemory { get; set; } = true;
    public List<string> DefaultTools { get; set; } = new();
    /// <summary>
    /// Working directory the agent operates in (project root). When null/empty,
    /// the current process directory is used. Persisted in app settings so the
    /// user can change the project work dir without re-launching from a path.
    /// </summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>
    /// Extra folders the user added to the task besides <see cref="WorkingDirectory"/>. They show in the explorer
    /// and are added to the agent's allowed paths, so it can read and edit files there too (by absolute path).
    /// </summary>
    public List<string> AdditionalFolders { get; set; } = new();
    /// <summary>
    /// Per-skill visibility overrides (Feature 06 — Skills). Keys are skill
    /// names; values are "hidden" or "visible". When "hidden", the skill's
    /// description is kept out of context until the user invokes it.
    /// </summary>
    public Dictionary<string, string> SkillOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When the requirements clarifier asks questions before a task (Off, Auto or Always).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public RequirementsClarifierMode RequirementsClarifier { get; set; } = RequirementsClarifierMode.Auto;

    /// <summary>
    /// Permission mode a new chat starts in: Ask, AutoEdit, FullAuto or Plan. Chosen in the setup wizard.
    /// </summary>
    public string DefaultPermissionMode { get; set; } = "Ask";
}

public class UiConfiguration
{
    public string Theme { get; set; } = "dark";
    public bool ShowTokenCount { get; set; } = true;
    public bool ShowToolOutput { get; set; } = true;
    public bool SyntaxHighlight { get; set; } = true;
    /// <summary>
    /// The last model the user picked in the chat model selector. Persisted so
    /// the selection survives application restarts (e.g. "Auto", "Fast", "Smart",
    /// or a concrete provider model id).
    /// </summary>
    public string? SelectedModel { get; set; }

    /// <summary>What to do on start when the last session of this working directory has messages: Ask, Always or Never resume.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public ResumeLastSessionMode ResumeLastSession { get; set; } = ResumeLastSessionMode.Ask;

    /// <summary>True once the first-run setup wizard was finished or skipped; it then never opens by itself again.</summary>
    public bool SetupWizardDone { get; set; }

    /// <summary>Project folders opened lately, newest first (see <see cref="RecentFolderList"/>).</summary>
    public List<string> RecentFolders { get; set; } = new();

    /// <summary>Progress of the sample project's "Fix this test" tour (null until the sample was tried).</summary>
    public AiCodeAgent.Core.Samples.SampleGuideState? SampleGuide { get; set; }

    /// <summary>Look for a newer release when the app starts (at most once a day). Settings → "Check for updates automatically".</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check got an answer from the server (UTC); null if it never did.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>The release version whose banner the user dismissed; that version is not announced again.</summary>
    public string? DismissedUpdateVersion { get; set; }
}
