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
                DefaultModel = "gpt-4o",
                TimeoutSeconds = 120
            },
            ["anthropic"] = new()
            {
                Name = "anthropic",
                BaseUrl = "https://api.anthropic.com",
                DefaultModel = "claude-3-5-sonnet-20241022",
                TimeoutSeconds = 120
            },
            ["ollama"] = new()
            {
                Name = "ollama",
                BaseUrl = "http://localhost:11434",
                DefaultModel = "llama3.1",
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
}

public class UiConfiguration
{
    public string Theme { get; set; } = "dark";
    public bool ShowTokenCount { get; set; } = true;
    public bool ShowToolOutput { get; set; } = true;
    public bool SyntaxHighlight { get; set; } = true;
}