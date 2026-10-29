using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Mcp;

/// <summary>
/// Finds plain-text secrets in an MCP server entry. A project <c>.aiagent/mcp.json</c> is committed to git, so the manager refuses to
/// write a secret into it: tokens, passwords and keys must be <c>${env:NAME}</c> references.
/// </summary>
public static partial class McpSecrets
{
    [GeneratedRegex(@"(token|secret|passw(or)?d|\bpwd\b|api[_-]?key|access[_-]?key|private|credential|auth|(^|[_-])pat($|[_-])|(^|[_-])key$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(ghp_|gho_|ghu_|ghs_|github_pat_|glpat-|sk-|xox[abprs]-|AKIA[0-9A-Z]{4}|AIza[0-9A-Za-z_-]{10}|eyJ[A-Za-z0-9_-]{10,}\.)[A-Za-z0-9_\-.]{8,}")]
    private static partial Regex TokenShapeRegex();

    [GeneratedRegex(@"://[^/\s:@]+:([^/\s@]+)@")]
    private static partial Regex UserInfoPasswordRegex();

    [GeneratedRegex(@"[?&](access_)?(token|key|api_?key|secret|password|auth)=([^&\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"^--?[A-Za-z-]*(token|key|secret|passw(or)?d)[A-Za-z-]*[=:](.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlagSecretRegex();

    public static bool LooksLikeSecretName(string name) => SecretNameRegex().IsMatch(name);

    /// <summary>What is wrong with <paramref name="config"/> as far as secrets go; empty = fine to commit.</summary>
    public static IReadOnlyList<string> Find(McpServerConfig config)
    {
        var problems = new List<string>();

        if (!string.IsNullOrEmpty(config.Token) && !McpEnv.ContainsReference(config.Token))
            problems.Add("The token must be an environment reference such as ${env:GITHUB_TOKEN}, not the token itself.");

        foreach (var (key, value) in config.Env)
        {
            if (string.IsNullOrEmpty(value) || McpEnv.ContainsReference(value)) continue;
            if (LooksLikeSecretName(key) || TokenShapeRegex().IsMatch(value))
                problems.Add($"Environment variable {key} holds a plain value; use ${{env:NAME}} to read it from your environment.");
        }

        foreach (var arg in config.Args)
        {
            if (string.IsNullOrEmpty(arg)) continue;
            if (HasPlainSecret(arg))
                problems.Add($"The argument '{Shorten(arg)}' contains a secret; put it in an environment variable and use ${{env:NAME}}.");
        }

        if (!string.IsNullOrEmpty(config.Url) && HasPlainSecret(config.Url))
            problems.Add("The URL contains a secret (password or token); use an environment reference or the token field.");

        return problems;
    }

    private static bool HasPlainSecret(string text)
    {
        if (TokenShapeRegex().IsMatch(text)) return true;
        foreach (Match m in UserInfoPasswordRegex().Matches(text))
            if (!McpEnv.ContainsReference(m.Groups[1].Value)) return true;
        foreach (Match m in QuerySecretRegex().Matches(text))
            if (!McpEnv.ContainsReference(m.Groups[3].Value)) return true;
        var flag = FlagSecretRegex().Match(text);
        return flag.Success && !McpEnv.ContainsReference(flag.Groups[3].Value);
    }

    private static string Shorten(string text) => text.Length <= 24 ? text[..Math.Min(text.Length, 12)] + "…" : text[..12] + "…";
}
