using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Evidence;

/// <summary>Tells whether a shell command builds, tests or lints, so the pack can show build and test results without the agent saying so.</summary>
public static class CommandClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Test =
    {
        new(@"\bdotnet\s+test\b", Opts),
        new(@"\b(py\.?test|python[\d.]*\s+-m\s+(pytest|unittest))\b", Opts),
        new(@"\bgo\s+test\b", Opts),
        new(@"\b(npm|pnpm|yarn)\s+(run\s+)?test\b", Opts),
        new(@"\b(npx\s+)?(jest|vitest|mocha|playwright\s+test)\b", Opts),
        new(@"\bcargo\s+(nextest\s+run|test)\b", Opts),
        new(@"\b(mvn|mvnw|\./mvnw)\s+.*\b(test|verify)\b", Opts),
        new(@"\b(gradle|gradlew|\./gradlew)\s+.*\btest\b", Opts),
        new(@"\b(rspec|phpunit|ctest)\b", Opts),
        new(@"\bmake\s+test\b", Opts)
    };

    private static readonly Regex[] Lint =
    {
        new(@"\b(eslint|stylelint|rubocop|golangci-lint|ruff|flake8|pylint|mypy|pyright|shellcheck|hadolint|markdownlint)\b", Opts),
        new(@"\b(npm|pnpm|yarn)\s+(run\s+)?lint\b", Opts),
        new(@"\bdotnet\s+format\b", Opts),
        new(@"\bprettier\b.*--check", Opts),
        new(@"\bblack\b.*--check", Opts),
        new(@"\bcargo\s+clippy\b", Opts),
        new(@"\bgo\s+vet\b", Opts),
        new(@"\bgofmt\s+-l\b", Opts)
    };

    private static readonly Regex[] Build =
    {
        new(@"\bdotnet\s+(build|publish|msbuild)\b", Opts),
        new(@"\bmsbuild\b", Opts),
        new(@"\b(npm|pnpm|yarn)\s+(run\s+)?build\b", Opts),
        new(@"\btsc\b", Opts),
        new(@"\bgo\s+build\b", Opts),
        new(@"\bcargo\s+(build|check)\b", Opts),
        new(@"\b(mvn|mvnw|\./mvnw)\s+.*\b(compile|package|install)\b", Opts),
        new(@"\b(gradle|gradlew|\./gradlew)\s+.*\b(build|assemble|compile\w*)\b", Opts),
        new(@"\bmake\s+(build|all)?\s*$", Opts),
        new(@"\bcmake\s+--build\b", Opts)
    };

    /// <summary>
    /// A command line may chain steps (<c>cd app &amp;&amp; dotnet build &amp;&amp; dotnet test</c>); the most meaningful kind wins:
    /// test, then lint, then build. Nothing recognised means <see cref="CommandKind.Other"/>.
    /// </summary>
    public static CommandKind Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return CommandKind.Other;
        var segments = Regex.Split(command, @"&&|\|\||;|\r?\n");
        var kinds = segments.Select(ClassifySegment).ToList();
        if (kinds.Contains(CommandKind.Test)) return CommandKind.Test;
        if (kinds.Contains(CommandKind.Lint)) return CommandKind.Lint;
        if (kinds.Contains(CommandKind.Build)) return CommandKind.Build;
        return CommandKind.Other;
    }

    private static CommandKind ClassifySegment(string segment)
    {
        // Text after a pipe is a filter (| tail, | grep), not the command being run.
        var head = segment.Split('|')[0];
        if (Test.Any(r => r.IsMatch(head))) return CommandKind.Test;
        if (Lint.Any(r => r.IsMatch(head))) return CommandKind.Lint;
        if (Build.Any(r => r.IsMatch(head))) return CommandKind.Build;
        return CommandKind.Other;
    }
}
