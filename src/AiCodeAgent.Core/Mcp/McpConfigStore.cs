using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Mcp;

/// <summary>Thrown when an entry for the project file would contain a plain-text secret.</summary>
public sealed class McpSecretException : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public McpSecretException(IReadOnlyList<string> problems)
        : base("Not saved: " + string.Join(" ", problems)) => Problems = problems;
}

/// <summary>Thrown when an mcp.json file cannot be read or an entry is not valid.</summary>
public sealed class McpConfigException : Exception
{
    public McpConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Reads and writes the two <c>mcp.json</c> files (global and project) for the MCP manager. The files keep the format the loader
/// reads (<c>{ "servers": { "name": { command, args, env, url, token, enabled } } }</c>); entries are changed one at a time and
/// anything else in the file (other servers, unknown keys) stays as it was. The project file never receives a plain-text secret.
/// </summary>
public sealed partial class McpConfigStore
{
    private static readonly string[] KnownKeys = { "name", "command", "args", "env", "url", "token", "enabled" };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex NameRegex();

    private readonly string _userFile;
    private readonly string? _projectFile;
    private readonly object _gate = new();

    public McpConfigStore(string userFile, string? projectFile)
    {
        _userFile = userFile;
        _projectFile = projectFile;
    }

    /// <summary>The user's <c>~/.aiagent/mcp.json</c> plus <c>.aiagent/mcp.json</c> of <paramref name="projectDirectory"/> (null = no project).</summary>
    public static McpConfigStore For(string? projectDirectory) =>
        new(Path.Combine(AppPaths.UserRoot, "mcp.json"),
            string.IsNullOrEmpty(projectDirectory) ? null : Path.Combine(projectDirectory, ".aiagent", "mcp.json"));

    public bool HasProject => _projectFile != null;

    public string? FileOf(McpScope scope) => scope == McpScope.Global ? _userFile : _projectFile;

    /// <summary>Error for a server name that cannot be used, or null. Tool names are <c>server__tool</c>, so <c>__</c> is not allowed.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name is required.";
        if (name.Length > 40) return "The name is longer than 40 characters.";
        if (!NameRegex().IsMatch(name)) return "Use letters, digits, '-' and '_' (for example 'github').";
        if (name.Contains("__", StringComparison.Ordinal)) return "The name cannot contain '__' (it separates the server from the tool name).";
        return null;
    }

    // ------------------------------------------------------------------ read

    /// <summary>The entries of one file exactly as written (references are not expanded). Missing file = empty.</summary>
    public IReadOnlyList<McpServerConfig> Read(McpScope scope)
    {
        var file = FileOf(scope);
        if (file == null) return Array.Empty<McpServerConfig>();
        lock (_gate)
        {
            var servers = LoadServers(file, createIfMissing: false, out _);
            var result = new List<McpServerConfig>();
            if (servers == null) return result;
            foreach (var (name, node) in servers)
            {
                if (node is not JsonObject) continue;
                var config = node.Deserialize<McpServerConfig>(ReadOptions) ?? new McpServerConfig();
                result.Add(config with { Name = string.IsNullOrWhiteSpace(config.Name) ? name : config.Name, Scope = scope });
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Adds or replaces an entry. <paramref name="replacingName"/> is the old name when the entry is renamed. A project entry with a
    /// plain-text secret is refused with <see cref="McpSecretException"/>.
    /// </summary>
    public void Upsert(McpScope scope, McpServerConfig config, string? replacingName = null)
    {
        var file = FileOf(scope) ?? throw new McpConfigException("There is no project folder to hold a project mcp.json.");
        if (ValidateName(config.Name) is { } nameError) throw new McpConfigException(nameError);
        if (string.IsNullOrWhiteSpace(config.Command) && string.IsNullOrWhiteSpace(config.Url))
            throw new McpConfigException("A server needs a command (local) or a URL (remote).");
        if (scope == McpScope.Project && McpSecrets.Find(config) is { Count: > 0 } problems)
            throw new McpSecretException(problems);

        lock (_gate)
        {
            var servers = LoadServers(file, createIfMissing: true, out var root)!;
            var oldKey = replacingName ?? config.Name;
            if (!string.Equals(oldKey, config.Name, StringComparison.OrdinalIgnoreCase) &&
                servers.Any(kv => string.Equals(kv.Key, config.Name, StringComparison.OrdinalIgnoreCase)))
                throw new McpConfigException($"A server named '{config.Name}' already exists in this file.");

            var rebuilt = new JsonObject();
            var placed = false;
            foreach (var (key, node) in servers)
            {
                if (string.Equals(key, oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    rebuilt[config.Name] = ToNode(config, node as JsonObject);
                    placed = true;
                }
                else
                {
                    rebuilt[key] = node?.DeepClone();
                }
            }
            if (!placed) rebuilt[config.Name] = ToNode(config, null);
            root!["servers"] = rebuilt;
            Save(file, root);
        }
    }

    public bool Remove(McpScope scope, string name)
    {
        var file = FileOf(scope);
        if (file == null || !File.Exists(file)) return false;
        lock (_gate)
        {
            var servers = LoadServers(file, createIfMissing: false, out var root);
            if (servers == null) return false;
            var key = servers.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key == null) return false;
            servers.Remove(key);
            Save(file, root!);
            return true;
        }
    }

    public bool SetEnabled(McpScope scope, string name, bool enabled)
    {
        var file = FileOf(scope);
        if (file == null || !File.Exists(file)) return false;
        lock (_gate)
        {
            var servers = LoadServers(file, createIfMissing: false, out var root);
            var key = servers?.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (servers == null || key == null || servers[key] is not JsonObject node) return false;
            if (enabled) node.Remove("enabled");
            else node["enabled"] = false;
            Save(file, root!);
            return true;
        }
    }

    // ------------------------------------------------------------------ files

    private static JsonObject? LoadServers(string file, bool createIfMissing, out JsonObject? root)
    {
        root = null;
        JsonNode? parsed = null;
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                catch (JsonException ex) { throw new McpConfigException($"{file} is not valid JSON: {ex.Message}", ex); }
            }
        }
        root = parsed as JsonObject;
        if (root == null)
        {
            if (parsed != null) throw new McpConfigException($"{file} must hold a JSON object.");
            if (!createIfMissing) return null;
            root = new JsonObject();
        }
        var key = root.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "servers", StringComparison.OrdinalIgnoreCase));
        if (key != null && root[key] is JsonObject existing)
        {
            if (key != "servers") { root.Remove(key); root["servers"] = existing; }
            return existing;
        }
        if (!createIfMissing) return null;
        var created = new JsonObject();
        root["servers"] = created;
        return created;
    }

    private static void Save(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = root.ToJsonString(WriteOptions) + "\n";
        var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static JsonObject ToNode(McpServerConfig config, JsonObject? previous)
    {
        var node = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.Command)) node["command"] = config.Command;
        if (config.Args.Count > 0) node["args"] = new JsonArray(config.Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (config.Env.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (k, v) in config.Env) env[k] = v;
            node["env"] = env;
        }
        if (!string.IsNullOrWhiteSpace(config.Url)) node["url"] = config.Url;
        if (!string.IsNullOrWhiteSpace(config.Token)) node["token"] = config.Token;
        if (!config.Enabled) node["enabled"] = false;
        if (previous != null)
            foreach (var (key, value) in previous)
                if (!KnownKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) node[key] = value?.DeepClone();
        return node;
    }
}
