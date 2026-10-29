using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Mcp;

/// <summary>Where an MCP server is configured.</summary>
public enum McpScope
{
    /// <summary><c>~/.aiagent/mcp.json</c>: yours on this machine.</summary>
    Global,
    /// <summary><c>.aiagent/mcp.json</c> in the project: shared through git, so it must hold no secrets.</summary>
    Project
}

/// <summary>
/// <c>${env:NAME}</c> references: how mcp.json points at secrets without containing them. The reference is kept in the file and
/// only replaced by the variable's value when a server is started.
/// </summary>
public static partial class McpEnv
{
    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    public static string Reference(string variable) => "${env:" + variable + "}";

    public static bool IsValidVariableName(string? name) => !string.IsNullOrEmpty(name) && NameRegex().IsMatch(name);

    public static bool ContainsReference(string? value) => !string.IsNullOrEmpty(value) && ReferenceRegex().IsMatch(value);

    /// <summary>The variable names mentioned in <paramref name="value"/>.</summary>
    public static IReadOnlyList<string> Variables(string? value) =>
        string.IsNullOrEmpty(value) ? Array.Empty<string>() : ReferenceRegex().Matches(value).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Every variable a server entry refers to (command, arguments, environment, URL and token).</summary>
    public static IReadOnlyList<string> Variables(McpServerConfig config) =>
        new[] { config.Command, config.Url, config.Token }.Concat(config.Args).Concat(config.Env.Values)
            .SelectMany(v => Variables(v)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Replaces each <c>${env:NAME}</c> with the variable's value. An unset or empty variable becomes empty and is added to <paramref name="missing"/>.</summary>
    public static string Expand(string? value, Func<string, string?> lookup, ISet<string>? missing = null)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return ReferenceRegex().Replace(value, m =>
        {
            var found = lookup(m.Groups[1].Value);
            if (string.IsNullOrEmpty(found))
            {
                missing?.Add(m.Groups[1].Value);
                return string.Empty;
            }
            return found;
        });
    }

    /// <summary>A copy of <paramref name="config"/> with every reference replaced; <see cref="McpServerConfig.MissingEnv"/> lists the unset ones.</summary>
    public static McpServerConfig Expand(McpServerConfig config, Func<string, string?> lookup)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        string? One(string? v) => v == null ? null : Expand(v, lookup, missing);
        var expanded = config with
        {
            Command = One(config.Command),
            Args = config.Args.Select(a => Expand(a, lookup, missing)).ToList(),
            Env = config.Env.ToDictionary(kv => kv.Key, kv => Expand(kv.Value, lookup, missing)),
            Url = One(config.Url),
            Token = One(config.Token),
            MissingEnv = missing.ToList()
        };
        return expanded;
    }
}
