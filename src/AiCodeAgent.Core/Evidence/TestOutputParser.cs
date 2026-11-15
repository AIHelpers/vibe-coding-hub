using System.Globalization;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Evidence;

/// <param name="Runner">Null when no known runner's output was found.</param>
public sealed record ParsedTests(string? Runner, int? Passed, int? Failed, int? Skipped, IReadOnlyList<string> FailedTests)
{
    public static readonly ParsedTests Unrecognised = new(null, null, null, null, Array.Empty<string>());
}

/// <summary>
/// Reads the console output of the common test runners: counts and the names of failing tests. Best effort by design: whatever is not
/// recognised stays null and the pack falls back to the exit code, it never guesses a number.
/// </summary>
public static class TestOutputParser
{
    private const RegexOptions Ml = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private const int MaxNames = 100;

    public static ParsedTests Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return ParsedTests.Unrecognised;
        var text = output.Replace("\r\n", "\n");
        return Dotnet(text) ?? Pytest(text) ?? Jest(text) ?? Vitest(text) ?? Cargo(text) ?? Go(text) ?? ParsedTests.Unrecognised;
    }

    private static ParsedTests? Dotnet(string text)
    {
        var summaries = Regex.Matches(text, @"(?:Passed|Failed)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)", Ml);
        // "  Failed Namespace.Class.Method [12 ms]": the line ends with the duration in brackets.
        var names = Regex.Matches(text, @"^\s*Failed\s+(\S.*?)\s+\[[^\]]*\]\s*$", Ml)
            .Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        if (summaries.Count == 0 && names.Count == 0) return null;
        if (summaries.Count == 0) return new ParsedTests("dotnet", null, null, null, names);
        int failed = 0, passed = 0, skipped = 0;
        foreach (Match m in summaries)
        {
            failed += Int(m.Groups[1].Value);
            passed += Int(m.Groups[2].Value);
            skipped += Int(m.Groups[3].Value);
        }
        return new ParsedTests("dotnet", passed, failed, skipped, names);
    }

    private static ParsedTests? Pytest(string text)
    {
        var summary = Regex.Matches(text,
            @"^=*\s*((?:\d+\s+(?:failed|passed|skipped|errors?|xfailed|xpassed|warnings?|deselected)(?:,\s*)?)+)\s+in\s+[\d.]+s", Ml);
        var names = Regex.Matches(text, @"^(?:FAILED|ERROR)\s+(\S+?)(?:\s+-\s+.*)?$", Ml)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        if (summary.Count == 0) return null;
        var line = summary[^1].Groups[1].Value;
        return new ParsedTests("pytest", Count(line, "passed"), Count(line, "failed") + Count(line, "error") + Count(line, "errors"),
            Count(line, "skipped"), names);
    }

    private static ParsedTests? Jest(string text)
    {
        var summary = Regex.Match(text, @"^Tests:\s+(.+)$", Ml);
        if (!summary.Success) return null;
        var line = summary.Groups[1].Value;
        var names = Regex.Matches(text, @"^\s*●\s+(.+?)\s*$", Ml)
            .Select(m => m.Groups[1].Value)
            .Where(n => !n.StartsWith("Console", StringComparison.Ordinal) && !n.Contains("Test suite failed to run", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        return new ParsedTests("jest", Count(line, "passed"), Count(line, "failed"), Count(line, "skipped"), names);
    }

    private static ParsedTests? Vitest(string text)
    {
        var summary = Regex.Match(text, @"^\s*Tests\s{2,}(.+)$", Ml);
        if (!summary.Success) return null;
        var line = summary.Groups[1].Value;
        var names = Regex.Matches(text, @"^\s*(?:FAIL|×)\s+(.+?>.+?)\s*$", Ml)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        return new ParsedTests("vitest", Count(line, "passed"), Count(line, "failed"), Count(line, "skipped"), names);
    }

    private static ParsedTests? Cargo(string text)
    {
        var results = Regex.Matches(text, @"^test result: (?:ok|FAILED)\.\s+(\d+) passed;\s+(\d+) failed;\s+(\d+) ignored", Ml);
        if (results.Count == 0) return null;
        int passed = 0, failed = 0, ignored = 0;
        foreach (Match m in results)
        {
            passed += Int(m.Groups[1].Value);
            failed += Int(m.Groups[2].Value);
            ignored += Int(m.Groups[3].Value);
        }
        var names = Regex.Matches(text, @"^test (\S+) \.\.\. FAILED\s*$", Ml)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        return new ParsedTests("cargo", passed, failed, ignored, names);
    }

    private static ParsedTests? Go(string text)
    {
        var failedNames = Regex.Matches(text, @"^\s*--- FAIL: (\S+)", Ml)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
        var passedTop = Regex.Matches(text, @"^--- PASS: (\S+)", Ml).Count;
        var packageLines = Regex.IsMatch(text, @"^(?:ok|FAIL|---)\s", Ml) || Regex.IsMatch(text, @"^\?\s+\S+\s+\[no test files\]", Ml);
        if (failedNames.Count == 0 && passedTop == 0 && !Regex.IsMatch(text, @"^ok\s+\S+\s+[\d.]+s", Ml) && !(packageLines && Regex.IsMatch(text, @"^FAIL\s", Ml)))
            return null;
        var topFailed = failedNames.Count(n => !n.Contains('/'));
        // Without -v Go prints no pass lines, so a pass count would be a guess.
        return new ParsedTests("go", passedTop > 0 ? passedTop : null, failedNames.Count > 0 ? topFailed : null, null, failedNames);
    }

    private static int Count(string line, string word)
    {
        var m = Regex.Match(line, @"(\d+)\s+" + word + @"\b");
        return m.Success ? Int(m.Groups[1].Value) : 0;
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
