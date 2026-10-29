using AiCodeAgent.Core.Configuration;
using System.Text.Json;
using AiCodeAgent.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Mcp;

/// <summary>
/// Loads MCP server configuration from <c>mcp.json</c> files.
/// Searches user (<c>~/.aiagent/mcp.json</c>) and project (<c>.aiagent/mcp.json</c>) locations.
/// </summary>
public static class McpConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads MCP server configs from the default locations.
    /// Project-level config overrides user-level entries with the same name.
    /// </summary>
    /// <param name="projectRoot">The project root directory (where <c>.aiagent/mcp.json</c> lives). If null, only the user config is loaded.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A list of <see cref="McpServerConfig"/> entries; empty if no config file exists.</returns>
    /// <param name="environment">Looks up the variables behind <c>${env:NAME}</c> references (default: the process environment).</param>
    /// <param name="userConfigPath">The user-level file (default: <c>~/.aiagent/mcp.json</c>).</param>
    public static List<McpServerConfig> Load(string? projectRoot = null, ILogger? logger = null,
        Func<string, string?>? environment = null, string? userConfigPath = null)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        var lookup = environment ?? Environment.GetEnvironmentVariable;

        // User-level config: ~/.aiagent/mcp.json
        userConfigPath ??= Path.Combine(AppPaths.UserRoot, "mcp.json");
        LoadFile(userConfigPath, McpScope.Global, configs, lookup, logger);

        // Project-level config: <projectRoot>/.aiagent/mcp.json
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projectConfigPath = Path.Combine(projectRoot, ".aiagent", "mcp.json");
            LoadFile(projectConfigPath, McpScope.Project, configs, lookup, logger);
        }

        return configs.Values.ToList();
    }

    private static void LoadFile(string path, McpScope scope, Dictionary<string, McpServerConfig> configs, Func<string, string?> lookup, ILogger? logger)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<McpConfigFile>(json, JsonOptions);
            if (doc?.Servers is null || doc.Servers.Count == 0)
                return;

            foreach (var (name, server) in doc.Servers)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // The config file may use "name" as the key or as a property; prefer the key.
                var cfg = server with { Name = string.IsNullOrWhiteSpace(server.Name) ? name : server.Name, Scope = scope };
                // ${env:NAME} references are replaced here, at start-up; the file keeps the reference.
                configs[cfg.Name] = McpEnv.Expand(cfg, lookup);
            }

            logger?.LogInformation("Loaded {Count} MCP server(s) from {Path}", doc.Servers.Count, path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load MCP config from {Path}", path);
        }
    }

    private sealed class McpConfigFile
    {
        public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}