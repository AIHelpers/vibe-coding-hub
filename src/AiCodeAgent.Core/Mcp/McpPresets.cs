namespace AiCodeAgent.Core.Mcp;

/// <summary>
/// A ready-made MCP server for the "Add server" form. Secrets are never part of a preset: it names the environment variable that
/// holds them, and the entry written to mcp.json contains only the <c>${env:NAME}</c> reference.
/// </summary>
public sealed record McpPreset(
    string Id,
    string Title,
    string Description,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url = null,
    string? TokenVariable = null,
    string? Note = null)
{
    /// <summary>The variables the user has to set (they appear in the entry as references).</summary>
    public IReadOnlyList<string> RequiredVariables =>
        new[] { Command, Url, TokenVariable == null ? null : McpEnv.Reference(TokenVariable) }
            .Concat(Args).Concat(Env.Values)
            .SelectMany(v => McpEnv.Variables(v)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The entry for this preset. <c>{project}</c> in an argument becomes <paramref name="projectDirectory"/> (or ".").</summary>
    public McpServerConfig ToConfig(string? projectDirectory = null) => new()
    {
        Name = Id,
        Command = Command,
        Args = Args.Select(a => a.Replace("{project}", string.IsNullOrEmpty(projectDirectory) ? "." : projectDirectory)).ToList(),
        Env = Env.ToDictionary(kv => kv.Key, kv => kv.Value),
        Url = Url,
        Token = TokenVariable == null ? null : McpEnv.Reference(TokenVariable)
    };
}

public static class McpPresets
{
    public static IReadOnlyList<McpPreset> All { get; } = new[]
    {
        new McpPreset("github", "GitHub", "Issues, pull requests, files and code search on GitHub.",
            "npx", new[] { "-y", "@modelcontextprotocol/server-github" },
            new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = McpEnv.Reference("GITHUB_TOKEN") },
            Note: "Create a personal access token on GitHub and put it in the environment variable GITHUB_TOKEN. The token is never written to mcp.json."),
        new McpPreset("filesystem", "Filesystem", "Read and write files in one folder through MCP.",
            "npx", new[] { "-y", "@modelcontextprotocol/server-filesystem", "{project}" },
            new Dictionary<string, string>(),
            Note: "The last argument is the folder the server may access. Change it if you want another one."),
        new McpPreset("postgres", "Postgres", "Read-only SQL access to a PostgreSQL database.",
            "npx", new[] { "-y", "@modelcontextprotocol/server-postgres", McpEnv.Reference("POSTGRES_URL") },
            new Dictionary<string, string>(),
            Note: "Put the connection string (postgresql://user:password@host/db) in the environment variable POSTGRES_URL."),
        new McpPreset("playwright", "Playwright", "Drive a real browser: open pages, click, fill forms, take screenshots.",
            "npx", new[] { "-y", "@playwright/mcp@latest" },
            new Dictionary<string, string>()),
        new McpPreset("fetch", "Fetch", "Fetch a web page and return it as Markdown.",
            "uvx", new[] { "mcp-server-fetch" },
            new Dictionary<string, string>(),
            Note: "Needs uv (https://docs.astral.sh/uv/) so that the command 'uvx' is available.")
    };

    public static McpPreset? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
