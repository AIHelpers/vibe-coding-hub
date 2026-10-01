using System.CommandLine;
using System.Diagnostics;
using System.Text;

namespace AiCodeAgent.CLI;

/// <summary>
/// CLI commands for viewing files in the working directory and viewing diffs.
/// </summary>
public static class FileViewCommands
{
    public static void AddCommands(Command rootCommand)
    {
        // files view >file<
        var filesCommand = new Command("files", "View files in the working directory");
        var dirOption = new Option<string>("--dir", "Working directory (defaults to current)");
        var viewCommand = new Command("view", "View the contents of a file with line numbers");
        var fileArg = new Argument<string>("file", "Path to the file to view");
        viewCommand.AddArgument(fileArg);
        viewCommand.AddOption(dirOption);
        viewCommand.SetHandler((file, dir) =>
        {
            var fullPath = ResolvePath(file, dir);
            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"File not found: {fullPath}");
                return;
            }

            var lines = File.ReadAllLines(fullPath);
            var padWidth = lines.Length.ToString().Length;
            Console.WriteLine($"File: {fullPath}  ({lines.Length} lines)");
            Console.WriteLine(new string('=', 80));
            for (int i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"{(i + 1).ToString().PadLeft(padWidth)} | {lines[i]}");
            }
            if (lines.Length == 0)
                Console.WriteLine("(file is empty)");
        }, fileArg, dirOption);

        // files list [--dir] [--pattern]
        var listCommand = new Command("list", "List files in a directory (tree or flat)");
        var listDirOption = new Option<string>("--dir", "Directory to list (defaults to current)");
        var patternOption = new Option<string>("--pattern", () => "*", "File glob pattern");
        var recursiveOption = new Option<bool>("--recursive", () => false, "List recursively");
        listCommand.AddOption(listDirOption);
        listCommand.AddOption(patternOption);
        listCommand.AddOption(recursiveOption);
        listCommand.SetHandler((dir, pattern, recursive) =>
        {
            var fullPath = ResolveDirectory(dir);
            if (!Directory.Exists(fullPath))
            {
                Console.Error.WriteLine($"Directory not found: {fullPath}");
                return;
            }

            var enumeration = new EnumerationOptions
            {
                MatchType = MatchType.Simple,
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var files = Directory.EnumerateFiles(fullPath, pattern, enumeration)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"Directory: {fullPath}");
            Console.WriteLine($"Files: {files.Count}" + (recursive ? " (recursive)" : ""));
            Console.WriteLine(new string('=', 80));
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(fullPath, f);
                var info = new FileInfo(f);
                Console.WriteLine($"  {rel,-60} {info.Length,10:N0}  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }, listDirOption, patternOption, recursiveOption);

        filesCommand.AddCommand(viewCommand);
        filesCommand.AddCommand(listCommand);

        // diff command - show git diff or diff between two files
        var diffCommand = new Command("diff", "View diffs (git working tree or between two files)");

        // diff git [--dir] [--cached]
        var gitDiffCommand = new Command("git", "Show git diff for the working tree");
        var gitDirOption = new Option<string>("--dir", "Working directory (git repo)");
        var cachedOption = new Option<bool>("--cached", () => false, "Show staged (cached) changes instead of working tree");
        gitDiffCommand.AddOption(gitDirOption);
        gitDiffCommand.AddOption(cachedOption);
        gitDiffCommand.SetHandler((dir, cached) =>
        {
            var fullPath = ResolveDirectory(dir);
            if (!Directory.Exists(fullPath))
            {
                Console.Error.WriteLine($"Directory not found: {fullPath}");
                return;
            }

            var args = cached ? "diff --cached" : "diff";
            var output = RunGit(fullPath, args);
            if (string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(cached ? "No staged changes." : "No uncommitted changes.");
                return;
            }
            PrintColoredDiff(output);
        }, gitDirOption, cachedOption);

        // diff files <file1> <file2>
        var filesDiffCommand = new Command("files", "Diff two files side by side");
        var file1Arg = new Argument<string>("file1", "First file");
        var file2Arg = new Argument<string>("file2", "Second file");
        filesDiffCommand.AddArgument(file1Arg);
        filesDiffCommand.AddArgument(file2Arg);
        filesDiffCommand.SetHandler((file1, file2) =>
        {
            var p1 = Path.GetFullPath(file1);
            var p2 = Path.GetFullPath(file2);
            if (!File.Exists(p1)) { Console.Error.WriteLine($"File not found: {p1}"); return; }
            if (!File.Exists(p2)) { Console.Error.WriteLine($"File not found: {p2}"); return; }

            var diff = ComputeLineDiff(p1, p2);
            PrintColoredDiff(diff);
        }, file1Arg, file2Arg);

        // diff commit <ref> [--dir] - show diff of a commit vs its parent
        var commitDiffCommand = new Command("commit", "Show diff for a git commit (vs its parent)");
        var refArg = new Argument<string>("ref", "Commit hash, branch, or tag");
        var commitDirOption = new Option<string>("--dir", "Working directory (git repo)");
        commitDiffCommand.AddArgument(refArg);
        commitDiffCommand.AddOption(commitDirOption);
        commitDiffCommand.SetHandler((refName, dir) =>
        {
            var fullPath = ResolveDirectory(dir);
            if (!Directory.Exists(fullPath))
            {
                Console.Error.WriteLine($"Directory not found: {fullPath}");
                return;
            }
            var output = RunGit(fullPath, $"diff {refName}~1 {refName}");
            if (string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine($"No diff for {refName} (or ref not found).");
                return;
            }
            PrintColoredDiff(output);
        }, refArg, commitDirOption);

        diffCommand.AddCommand(gitDiffCommand);
        diffCommand.AddCommand(filesDiffCommand);
        diffCommand.AddCommand(commitDiffCommand);

        rootCommand.AddCommand(filesCommand);
        rootCommand.AddCommand(diffCommand);
    }

    private static string ResolvePath(string file, string? dir)
    {
        return Path.IsPathRooted(file)
            ? file
            : Path.GetFullPath(file, ResolveDirectory(dir));
    }

    private static string ResolveDirectory(string? dir)
    {
        return string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
    }

    private static string RunGit(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine($"git: {stderr.Trim()}");
            return stdout;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to run git: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Compute a unified-diff-style string between two files.</summary>
    private static string ComputeLineDiff(string path1, string path2)
    {
        var a = File.ReadAllLines(path1);
        var b = File.ReadAllLines(path2);
        var sb = new StringBuilder();
        sb.AppendLine($"--- {path1}");
        sb.AppendLine($"+++ {path2}");

        int max = Math.Max(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            var la = i < a.Length ? a[i] : null;
            var lb = i < b.Length ? b[i] : null;
            if (la == lb) continue;
            if (la != null && lb != null)
            {
                sb.AppendLine($"- {la}");
                sb.AppendLine($"+ {lb}");
            }
            else if (la != null)
            {
                sb.AppendLine($"- {la}");
            }
            else if (lb != null)
            {
                sb.AppendLine($"+ {lb}");
            }
        }
        return sb.ToString();
    }

    private static void PrintColoredDiff(string diff)
    {
        var lines = diff.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++") || line.StartsWith("---"))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else if (line.StartsWith("+"))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else if (line.StartsWith("-"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else if (line.StartsWith("@@"))
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }
}