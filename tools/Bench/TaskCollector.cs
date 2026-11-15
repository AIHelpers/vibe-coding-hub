using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Bench;

/// <summary>Decides which files of a fix commit are tests (they become the hidden test patch) and which are the fix.</summary>
internal static class TestFileClassifier
{
    private static readonly HashSet<string> TestFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "spec", "specs", "testdata", "test-data", "fixtures", "__fixtures__", "__snapshots__"
    };

    // MyApp.Tests, MyApp.UnitTests, MyApp.IntegrationTests, MyApp.Specs ...
    private static readonly Regex TestProjectFolder = new(@"\.(unit|integration|functional|e2e)?tests?$|\.specs?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // test_foo.py, foo_test.py/go, foo.test.ts, foo.spec.js, FooTests.cs, FooTest.java, conftest.py
    // (the C#-style suffix is case-sensitive, so Contest.cs and Latest.java are not tests)
    private static readonly Regex TestFileName = new(
        @"^(test_.+|.+_test\..+|.+\.(test|spec)\.[^.]+|.+(?-i:Tests?)\.(cs|fs|vb|java|kt|swift|scala)|conftest\.py)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsTestFile(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
            if (TestFolders.Contains(parts[i]) || TestProjectFolder.IsMatch(parts[i])) return true;
        return TestFileName.IsMatch(parts[^1]);
    }
}

/// <summary>What the collector found in a fix commit.</summary>
internal sealed record CollectedTask(
    BenchTask Task, string PatchRelativePath, string PatchText,
    IReadOnlyList<string> TestFiles, IReadOnlyList<string> SourceFiles, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a bug-fix commit of an open-source repository into a benchmark task (<c>--collect</c>): the snapshot is the commit
/// before the fix, the hidden test patch is everything the fix changed in test files, and the prompt is the issue text
/// from a file. The fix itself is never stored. Run <c>--validate</c> afterwards: it proves the test fails before the fix.
/// </summary>
internal static class TaskCollector
{
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions EntryJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> RunAsync(Options options, TextWriter stdout, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(options.Id)) throw new BenchException("--collect needs --id <task id>.");
        if (string.IsNullOrWhiteSpace(options.Fix)) throw new BenchException("--collect needs --fix <commit that fixed the bug>.");
        if (string.IsNullOrWhiteSpace(options.PromptFile)) throw new BenchException("--collect needs --prompt-file <file with the task text>.");
        if (!options.Id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new BenchException("The id may contain only letters, digits, '-', '_' and '.'.");
        if (!File.Exists(options.PromptFile)) throw new BenchException($"Prompt file not found: {Path.GetFullPath(options.PromptFile)}");

        var tasksPath = Path.GetFullPath(options.TasksPath);
        var tasksDir = Path.GetDirectoryName(tasksPath)!;
        var patchRelative = "patches/" + options.Id + ".tests.patch";
        var patchFull = Path.Combine(tasksDir, "patches", options.Id + ".tests.patch");
        if (File.Exists(patchFull))
            throw new BenchException($"{patchFull} already exists. Results and tasks are never overwritten: pick another --id or remove the file yourself.");

        var repoDir = await EnsureRepositoryAsync(options, cancellation);
        var gitUrl = options.GitUrl ?? await OriginUrlAsync(repoDir, cancellation)
            ?? throw new BenchException("The repository has no 'origin' remote. Pass --git-url <url> so that the task can be cloned by others.");

        var collected = await CollectAsync(options, repoDir, gitUrl, patchRelative, cancellation);

        Directory.CreateDirectory(Path.GetDirectoryName(patchFull)!);
        await File.WriteAllTextAsync(patchFull, collected.PatchText, new UTF8Encoding(false), cancellation);

        stdout.WriteLine($"Task '{options.Id}': snapshot {collected.Task.Repo.Commit![..12]} (the commit before {options.Fix![..Math.Min(12, options.Fix.Length)]}).");
        stdout.WriteLine($"  hidden tests ({collected.TestFiles.Count}): {string.Join(", ", collected.TestFiles.Take(6))}{(collected.TestFiles.Count > 6 ? ", ..." : "")}");
        stdout.WriteLine($"  the fix touches ({collected.SourceFiles.Count}): {string.Join(", ", collected.SourceFiles.Take(6))}{(collected.SourceFiles.Count > 6 ? ", ..." : "")}");
        stdout.WriteLine($"  wrote {patchFull}");
        foreach (var w in collected.Warnings) stdout.WriteLine("  warning: " + w);

        var entry = ToJson(collected.Task);
        if (options.Append)
        {
            AppendToFile(tasksPath, options.SuiteName, entry);
            stdout.WriteLine($"Added the task to {tasksPath} (comments in that file, if any, are not kept when it is rewritten).");
        }
        else
        {
            stdout.WriteLine();
            stdout.WriteLine("Add this entry to the \"tasks\" array of your task file (or run again with --append):");
            stdout.WriteLine(entry.ToJsonString(EntryJson));
        }
        stdout.WriteLine();
        stdout.WriteLine($"Next: dotnet run --project tools/Bench -- --tasks {options.TasksPath} --only {options.Id} --validate");
        return 0;
    }

    /// <summary>Reads a fix commit from a git repository and builds the task. Writes nothing.</summary>
    internal static async Task<CollectedTask> CollectAsync(Options options, string repoDir, string gitUrl, string patchRelative, CancellationToken cancellation)
    {
        var prompt = (await File.ReadAllTextAsync(options.PromptFile!, cancellation)).Trim();
        if (prompt.Length == 0) throw new BenchException("The prompt file is empty.");

        var fix = await RevParseAsync(repoDir, options.Fix!, cancellation);
        var parents = await ParentsAsync(repoDir, fix, cancellation);
        string baseCommit;
        if (options.Parent != null)
        {
            baseCommit = await RevParseAsync(repoDir, options.Parent, cancellation);
            if (!parents.Contains(baseCommit)) throw new BenchException($"{options.Parent} is not a parent of {fix[..12]}.");
        }
        else if (parents.Count == 1) baseCommit = parents[0];
        else if (parents.Count == 0) throw new BenchException($"{fix[..12]} is the first commit: there is no state before it.");
        else throw new BenchException($"{fix[..12]} is a merge commit with {parents.Count} parents. Pass --parent <sha> to say which state to start from, or use the commit that contains the actual fix.");

        var names = await GitTextAsync(repoDir, new[] { "-c", "core.quotepath=false", "diff", "--no-renames", "--name-only", "-z", baseCommit, fix }, cancellation);
        var files = names.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var tests = files.Where(TestFileClassifier.IsTestFile).ToList();
        var sources = files.Where(f => !TestFileClassifier.IsTestFile(f)).ToList();
        if (tests.Count == 0)
            throw new BenchException($"{fix[..12]} changes no test files, so there is no hidden test to take from it. Pick a fix commit that adds or changes a test.");
        if (sources.Count == 0)
            throw new BenchException($"{fix[..12]} changes only test files: it contains no fix for the agent to find.");

        var patchArgs = new List<string> { "-c", "core.quotepath=false", "diff", "--no-renames", "--binary", baseCommit, fix, "--" };
        patchArgs.AddRange(tests);
        var patch = await GitTextAsync(repoDir, patchArgs, cancellation);
        if (!patch.EndsWith('\n')) patch += "\n";

        var warnings = new List<string>();
        var language = options.Language;
        var testCommand = options.TestCommand;
        var setup = options.SetupCommand;
        if (testCommand == null)
        {
            testCommand = language?.ToLowerInvariant() switch
            {
                "csharp" or "c#" or "dotnet" => "dotnet test --nologo",
                "python" => "python -m pytest -q",
                _ => throw new BenchException("Pass --test-command: the command that runs the hidden tests (exit code 0 = solved). With --language csharp or python a whole-suite default is used.")
            };
            warnings.Add($"--test-command was not given; '{testCommand}' runs the whole suite. Narrow it to the new tests (for example a --filter) so unrelated failures do not hide a fix.");
        }
        if (setup == null && language?.ToLowerInvariant() is "csharp" or "c#" or "dotnet") setup = "dotnet restore";

        foreach (var s in sources)
        {
            var name = Path.GetFileName(s);
            if (name.Length >= 4 && prompt.Contains(name, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"The prompt names '{name}', a file the fix changes. That is fine when the issue names it; check that it does not give away the solution.");
        }
        if (prompt.Contains("```diff", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(prompt, @"^(\+\+\+|---) [ab]/", RegexOptions.Multiline))
            warnings.Add("The prompt contains a diff. The agent must not be handed the solution: remove it.");

        var task = new BenchTask
        {
            Id = options.Id!,
            Language = language,
            Source = options.Source,
            Prompt = prompt,
            Repo = new RepoSpec { Git = gitUrl, Commit = baseCommit },
            SetupCommand = setup,
            TestPatch = patchRelative,
            TestCommand = testCommand
        };
        return new CollectedTask(task, patchRelative, patch, tests, sources, warnings);
    }

    internal static JsonNode ToJson(BenchTask task)
    {
        var node = JsonSerializer.SerializeToNode(task, EntryJson)!.AsObject();
        if (node["repo"] is JsonObject repo)
        {
            // Only the fields that apply; an empty "path" would make the entry invalid.
            if (repo["path"] == null) repo.Remove("path");
        }
        return node;
    }

    private static void AppendToFile(string tasksPath, string? suiteName, JsonNode entry)
    {
        JsonObject root;
        if (File.Exists(tasksPath))
        {
            var parsed = JsonNode.Parse(
                File.ReadAllText(tasksPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = parsed as JsonObject ?? throw new BenchException("The task file is not a JSON object.");
        }
        else
        {
            root = new JsonObject { ["name"] = suiteName ?? "bugfix-v1", ["tasks"] = new JsonArray() };
        }
        var tasks = root["tasks"] as JsonArray ?? new JsonArray();
        root["tasks"] = tasks;
        var id = entry["id"]!.GetValue<string>();
        if (tasks.Any(t => string.Equals(t?["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)))
            throw new BenchException($"The task file already has a task '{id}'.");
        tasks.Add(entry.DeepClone());
        Directory.CreateDirectory(Path.GetDirectoryName(tasksPath)!);
        File.WriteAllText(tasksPath, root.ToJsonString(EntryJson) + Environment.NewLine, new UTF8Encoding(false));
    }

    /// <summary>A folder is used as it is; a URL is fetched into the work folder with just the two commits that matter.</summary>
    private static async Task<string> EnsureRepositoryAsync(Options options, CancellationToken cancellation)
    {
        var source = options.Collect!;
        if (Directory.Exists(source))
        {
            var dir = Path.GetFullPath(source);
            var inside = await ProcessRunner.GitAsync(dir, new[] { "rev-parse", "--git-dir" }, cancellation);
            if (inside.ExitCode != 0) throw new BenchException($"{dir} is not a git repository.");
            return dir;
        }

        if (!FullSha.IsMatch(options.Fix!))
            throw new BenchException("With a repository URL, --fix must be the full 40-character commit hash (GitHub does not fetch abbreviated ones).");
        var work = Path.Combine(options.ResolvedWorkDir, "collect", FileSystemUtil.SafeName(options.Id!));
        FileSystemUtil.ForceDelete(work);
        Directory.CreateDirectory(work);
        foreach (var args in new[] { new[] { "init", "--quiet" }, new[] { "remote", "add", "origin", source } })
        {
            var r = await ProcessRunner.GitAsync(work, args, cancellation);
            if (r.ExitCode != 0) throw new BenchException($"git {args[0]} failed: {ProcessRunner.Tail(r.Stderr)}");
        }
        var fetch = await ProcessRunner.GitAsync(work, new[] { "fetch", "--quiet", "--depth", "2", "origin", options.Fix! }, cancellation, TimeSpan.FromMinutes(15));
        if (fetch.ExitCode != 0) throw new BenchException($"Cannot fetch {options.Fix} from {source}: {ProcessRunner.Tail(fetch.Stderr)}");
        return work;
    }

    private static async Task<string?> OriginUrlAsync(string repoDir, CancellationToken cancellation)
    {
        var r = await ProcessRunner.GitAsync(repoDir, new[] { "config", "--get", "remote.origin.url" }, cancellation);
        var url = r.Stdout.Trim();
        return r.ExitCode == 0 && url.Length > 0 ? url : null;
    }

    private static async Task<string> RevParseAsync(string repoDir, string rev, CancellationToken cancellation)
    {
        var r = await ProcessRunner.GitAsync(repoDir, new[] { "rev-parse", "--verify", "--quiet", rev + "^{commit}" }, cancellation);
        var sha = r.Stdout.Trim();
        if (r.ExitCode != 0 || sha.Length != 40) throw new BenchException($"'{rev}' is not a commit in this repository.");
        return sha;
    }

    private static async Task<List<string>> ParentsAsync(string repoDir, string commit, CancellationToken cancellation)
    {
        var line = await GitTextAsync(repoDir, new[] { "rev-list", "--parents", "-n", "1", commit }, cancellation);
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToList();
    }

    private static async Task<string> GitTextAsync(string repoDir, IEnumerable<string> args, CancellationToken cancellation)
    {
        var r = await ProcessRunner.GitAsync(repoDir, args, cancellation);
        if (r.ExitCode != 0) throw new BenchException($"git {args.First(a => !a.StartsWith('-') && a != "core.quotepath=false")} failed: {ProcessRunner.Tail(r.Stderr)}");
        return r.Stdout;
    }
}
