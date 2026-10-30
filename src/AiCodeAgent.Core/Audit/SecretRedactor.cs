using System.Collections;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Audit;

/// <summary>
/// Removes secrets from text before it is written to an audit file (E7-S2). Three layers:
/// <list type="number">
/// <item>the <em>values</em> of environment variables whose name contains KEY, TOKEN, SECRET, PASSWORD or CREDENTIAL;</item>
/// <item>values registered explicitly (<c>extraSecrets</c>: e.g. the API key the provider is configured with);</item>
/// <item>text that looks like a key whatever it is called: well-known prefixes (<c>sk-</c>, <c>ghp_</c>, <c>AKIA…</c>, …),
/// <c>Bearer</c> tokens, JWTs, PEM private keys and <c>name = value</c> pairs whose name ends in key/token/secret/password.</item>
/// </list>
/// Redaction is idempotent: redacting redacted text changes nothing.
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "[REDACTED]";

    /// <summary>Env values shorter than this are not treated as secrets (a one-letter value would blank out half the log).</summary>
    public const int MinSecretLength = 8;

    private static readonly Regex EnvNamePattern = new("KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex[] KnownShapes =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant),
        new(@"sk-ant-[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant),
        new(@"sk-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant),
        new(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant),
        new(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant),
        new(@"glpat-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant),
        new(@"xox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant),
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant),
        new(@"AIza[0-9A-Za-z_\-]{30,}", RegexOptions.CultureInvariant),
        new(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant),
    };

    private static readonly Regex BearerPattern = new(@"(?<=\bBearer\s+)[A-Za-z0-9._~+/=\-]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>api_key=abc123456</c>, <c>"token": "abc123456"</c>, <c>MY_SECRET: abc123456</c> — the name stays, the value goes.</summary>
    private static readonly Regex AssignmentPattern = new(
        @"(?<name>[A-Za-z0-9_.\-]*(?:api[_\-]?key|private[_\-]?key|secret|password|passwd|token|_key)(?![A-Za-z0-9])[""']?)(?<sep>\s*[:=]\s*)(?<q>[""']?)(?<val>[^\s""',;}\]]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Command-line flags: <c>--token abc123456</c>, <c>-api-key "abc123456"</c>.</summary>
    private static readonly Regex FlagPattern = new(
        @"(?<flag>(?<![A-Za-z0-9_])--?[A-Za-z0-9\-]*(?:api-?key|token|secret|password|passwd))(?<sep>\s+)(?<q>[""']?)(?<val>[^\s""'\-][^\s""']{5,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string[] _literals;

    /// <param name="environment">Variables to scan (name → value); null = the process environment, read now.</param>
    /// <param name="extraSecrets">Further exact values to mask (e.g. configured API keys).</param>
    public SecretRedactor(IEnumerable<KeyValuePair<string, string>>? environment = null, IEnumerable<string>? extraSecrets = null)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in environment ?? ReadEnvironment())
        {
            if (!string.IsNullOrEmpty(value) && value.Length >= MinSecretLength && EnvNamePattern.IsMatch(name))
                values.Add(value.Trim());
        }
        if (extraSecrets != null)
            foreach (var s in extraSecrets)
                if (!string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 4) values.Add(s.Trim());
        // Longest first, so a secret that contains another one is masked as a whole.
        _literals = values.Where(v => v.Length > 0).OrderByDescending(v => v.Length).ToArray();
    }

    /// <summary>A redactor for the current process environment.</summary>
    public static SecretRedactor FromEnvironment(IEnumerable<string>? extraSecrets = null) => new(null, extraSecrets);

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = text;
        foreach (var secret in _literals)
            if (result.Contains(secret, StringComparison.Ordinal))
                result = result.Replace(secret, Mask, StringComparison.Ordinal);
        foreach (var shape in KnownShapes)
            result = shape.Replace(result, Mask);
        result = BearerPattern.Replace(result, Mask);
        result = FlagPattern.Replace(result, m =>
            m.Groups["val"].Value.StartsWith("[REDACTED", StringComparison.Ordinal)
                ? m.Value
                : m.Groups["flag"].Value + m.Groups["sep"].Value + m.Groups["q"].Value + Mask);
        result = AssignmentPattern.Replace(result, m =>
            m.Groups["val"].Value.StartsWith("[REDACTED", StringComparison.Ordinal)
                ? m.Value
                : m.Groups["name"].Value + m.Groups["sep"].Value + m.Groups["q"].Value + Mask);
        return result;
    }

    /// <summary>True when <see cref="Redact"/> would change the text.</summary>
    public bool ContainsSecret(string? text) => !string.IsNullOrEmpty(text) && !string.Equals(Redact(text), text, StringComparison.Ordinal);

    private static IEnumerable<KeyValuePair<string, string>> ReadEnvironment()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) list.Add(new(k, v));
        return list;
    }
}
