using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Ranks a changed file by how much a human should look at it: size, public API, security-sensitive paths, build or deploy
/// configuration, deletion. A transparent point system, not a model: every point has a reason that is shown next to the file.
/// </summary>
public static class FileRiskScorer
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // The Foo(Tests|Test|Spec).cs suffix is case-sensitive on purpose: "Latest.cs" and "Contest.cs" are not tests.
    private static readonly Regex TestPath = new(
        @"(^|[/\\])(tests?|__tests__|spec|specs|testdata)([/\\]|$)|(?-i:(Tests?|Spec)\.(cs|java|kt|ts|tsx|js|jsx)$)|\.(test|spec)\.(ts|tsx|js|jsx|mjs)$|_test\.(go|py|rs)$|(^|[/\\])test_[^/\\]+\.py$", Opts);

    // A word ends at a separator, the end, or a capital letter (AuthService.cs), so "Authority.cs" is not "auth".
    private static readonly Regex SecurityPath = new(
        @"(^|[/\\._-])(auth(entication|orization|n|z)?|login|logout|passwords?|passwd|secrets?|credentials?|crypto(graphy)?|encrypt(ion)?|certs?|certificates?|permissions?|oauth|jwt|security|sandbox|acl|rbac|firewall|\.env\w*|id_rsa|api[-_]?keys?)($|[/\\._-]|(?-i:(?=[A-Z])))|\.(pem|key|pfx|p12)$", Opts);

    private static readonly Regex ConfigPath = new(
        @"(^|[/\\])\.github[/\\]workflows[/\\]|(^|[/\\])(Dockerfile[^/\\]*|docker-compose[^/\\]*|package\.json|go\.mod|go\.sum|Cargo\.toml|pom\.xml|pyproject\.toml|requirements[^/\\]*\.txt|Directory\.(Build|Packages)\.(props|targets)|build\.gradle(\.kts)?|Makefile|\.gitlab-ci\.yml)$|\.(csproj|fsproj|vbproj|sln|slnx)$|(^|[/\\])(migrations?|db[/\\]migrate)[/\\]|\.sql$", Opts);

    private static readonly Regex PublicApiCs = new(@"^\s*(public|protected)\s+", Opts);
    private static readonly Regex PublicApiTs = new(@"^\s*export\s+", Opts);
    private static readonly Regex PublicApiGo = new(@"^(func\s+(\([^)]*\)\s*)?[A-Z]|type\s+[A-Z]|var\s+[A-Z]|const\s+[A-Z])", RegexOptions.CultureInvariant);
    private static readonly Regex PublicApiPy = new(@"^(def|class)\s+[A-Za-z]", Opts);
    private static readonly Regex PublicApiRust = new(@"^\s*pub(\([^)]*\))?\s+", Opts);

    public static bool IsTestPath(string path) => TestPath.IsMatch(path);

    /// <summary>Added and removed line counts of a unified diff (headers excluded).</summary>
    public static (int Added, int Removed) CountLines(string? diff)
    {
        int added = 0, removed = 0;
        if (string.IsNullOrEmpty(diff)) return (0, 0);
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal)) continue;
            if (raw.StartsWith('+')) added++;
            else if (raw.StartsWith('-')) removed++;
        }
        return (added, removed);
    }

    /// <summary>Whether any added or removed line of the diff touches the public surface of the language the path suggests.</summary>
    public static bool TouchesPublicApi(string path, string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return false;
        var regex = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".java" or ".kt" or ".fs" => PublicApiCs,
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" => PublicApiTs,
            ".go" => PublicApiGo,
            ".py" => PublicApiPy,
            ".rs" => PublicApiRust,
            _ => null
        };
        if (regex == null) return false;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.Length < 2 || raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal)) continue;
            if ((raw[0] == '+' || raw[0] == '-') && regex.IsMatch(raw[1..].TrimEnd('\r'))) return true;
        }
        return false;
    }

    public static (int Score, EvidenceRisk Risk, IReadOnlyList<string> Reasons) Score(
        string path, int added, int removed, bool created, bool deleted, bool touchesPublicApi, bool isTest)
    {
        var reasons = new List<string>();
        var score = 0;
        var lines = added + removed;
        if (lines >= 200) { score += 3; reasons.Add($"large change ({lines} lines)"); }
        else if (lines >= 50) { score += 2; reasons.Add($"sizeable change ({lines} lines)"); }
        else if (lines >= 10) score += 1;

        if (isTest)
        {
            reasons.Add("test file");
            return (score, score >= 3 ? EvidenceRisk.Medium : EvidenceRisk.Low, reasons);
        }

        if (touchesPublicApi) { score += 3; reasons.Add("public API"); }
        if (SecurityPath.IsMatch(path)) { score += 4; reasons.Add("security-sensitive path"); }
        if (ConfigPath.IsMatch(path)) { score += 2; reasons.Add("build, deploy or data config"); }
        if (deleted) { score += 2; reasons.Add("file removed"); }
        else if (created && lines >= 50) { score += 1; reasons.Add("new file"); }

        var risk = score >= 5 ? EvidenceRisk.High : score >= 3 ? EvidenceRisk.Medium : EvidenceRisk.Low;
        return (score, risk, reasons);
    }
}
