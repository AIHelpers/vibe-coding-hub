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
    public static List<McpServerConfig> Load(string? projectRoot = null, ILogger? logger = null)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // User-level config: ~/.aiagent/mcp.json
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userConfigPath = Path.Combine(userHome, ".aiagent", "mcp.json");
        LoadFile(userConfigPath, configs, logger);

        // Project-level config: <projectRoot>/.aiagent/mcp.json
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projectConfigPath = Path.Combine(projectRoot, ".aiagent", "mcp.json");
            LoadFile(projectConfigPath, configs, logger);
        }

        return configs.Values.ToList();
    }

    private static void LoadFile(string path, Dictionary<string, McpServerConfig> configs, ILogger? logger)
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
                var cfg = server with { Name = string.IsNullOrWhiteSpace(server.Name) ? name : server.Name };
                configs[cfg.Name] = cfg;
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