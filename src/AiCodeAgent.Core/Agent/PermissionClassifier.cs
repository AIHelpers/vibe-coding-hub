using System.Collections.Generic;
using System.Text;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Classifies tool actions as safe or risky for Auto mode (Feature 10).
/// In Auto mode, safe actions run without prompting; risky ones are blocked.
/// </summary>
public class PermissionClassifier
{
    /// <summary>
    /// Commands considered safe in Auto mode (read-only filesystem/VCS/build
    /// invocations). Each entry is matched token-by-token against the actual
    /// command, not as a string prefix — "cat" must be the program, not
    /// merely a prefix of it ("catastrophe" no longer matches "cat"), and
    /// "git status" requires "status" as the very next token.
    /// </summary>
    private static readonly string[] SafeCommands =
    {
        "ls", "dir", "cat", "head", "tail", "grep", "find", "which", "where",
        "echo", "pwd", "git status", "git log", "git diff", "git branch",
        "npm test", "npm run test", "dotnet test", "dotnet build",
        "node --version", "npm --version", "python --version"
    };

    /// <summary>Common filesystem commands that are safe in Auto mode.</summary>
    private static readonly HashSet<string> CommonFilesystemCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkdir", "mv", "cp", "touch", "echo"
    };

    /// <summary>
    /// Characters/sequences that let one shell command chain into another —
    /// a command using any of these needs a human to look at it, however
    /// innocuous its own first word looks. This is what makes
    /// "cat file && rm -fr /" (which used to pass because the *whole string*
    /// merely started with "cat") get rejected instead of silently allowed.
    /// </summary>
    private static readonly char[] ShellControlChars = ['&', '|', ';', '>', '<', '`', '\n', '\r'];

    /// <summary>
    /// Classify a tool call as safe (Allow) or risky (Deny). In Auto mode,
    /// risky actions are blocked rather than prompted.
    /// </summary>
    public PermissionDecision Classify(ToolCall call, RiskLevel risk)
    {
        // Reads are always safe.
        if (risk == RiskLevel.Read)
            return PermissionDecision.Allow;

        // In Auto mode, only allow listed safe commands and common filesystem commands.
        if (risk == RiskLevel.Execute)
        {
            var command = GetCommand(call);
            if (string.IsNullOrWhiteSpace(command))
                return PermissionDecision.Deny;

            if (ContainsShellControlOperator(command))
                return PermissionDecision.Deny;

            var tokens = Tokenize(command);
            if (tokens.Count == 0)
                return PermissionDecision.Deny;

            // Arguments that leave the workspace or expand at the shell
            // level (absolute paths, "..", "~", $VARS, %VARS%) need a human.
            if (!ArgumentsStayInWorkspace(tokens))
                return PermissionDecision.Deny;

            // Verbs that turn an otherwise read-only program destructive.
            if (HasDangerousFlags(tokens))
                return PermissionDecision.Deny;

            foreach (var safe in SafeCommands)
            {
                if (MatchesSafeCommand(tokens, safe))
                    return PermissionDecision.Allow;
            }

            var program = StripPathAndExtension(tokens[0]);
            if (CommonFilesystemCommands.Contains(program))
                return PermissionDecision.Allow;

            return PermissionDecision.Deny;
        }

        // Writes in Auto mode: allow edits to files, deny dangerous patterns.
        if (risk == RiskLevel.Write)
            return PermissionDecision.Allow;

        return PermissionDecision.Deny;
    }

    private static readonly HashSet<string> FindDangerousFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls"
    };

    private static readonly HashSet<string> GitBranchMutatingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy", "-f", "--force",
        "--set-upstream-to", "-u", "--unset-upstream", "--edit-description"
    };

    private static bool HasDangerousFlags(List<string> tokens)
    {
        var program = StripPathAndExtension(tokens[0]);
        if (program.Equals("find", StringComparison.OrdinalIgnoreCase))
            return tokens.Skip(1).Any(t => FindDangerousFlags.Contains(t));

        if (program.Equals("git", StringComparison.OrdinalIgnoreCase) && tokens.Count > 1 &&
            tokens[1].Equals("branch", StringComparison.OrdinalIgnoreCase))
            return tokens.Skip(2).Any(t => GitBranchMutatingFlags.Contains(t));

        // "git diff --output=file" and "git log --output=file" write files.
        if (program.Equals("git", StringComparison.OrdinalIgnoreCase))
            return tokens.Skip(1).Any(t => t.StartsWith("--output", StringComparison.OrdinalIgnoreCase));

        return false;
    }

    private static bool ArgumentsStayInWorkspace(List<string> tokens)
    {
        foreach (var token in tokens.Skip(1))
        {
            if (token.StartsWith('-') && !token.Contains('/') && !token.Contains('\\'))
                continue; // plain option flag
            if (token.Contains("..", StringComparison.Ordinal) ||
                token.StartsWith('~') ||
                token.Contains('$') || token.Contains('%') ||
                Path.IsPathRooted(token) ||
                token.StartsWith('/') || token.StartsWith('\\') ||
                (token.Length >= 2 && char.IsLetter(token[0]) && token[1] == ':'))
                return false;
        }
        return true;
    }

    internal static bool ContainsShellControlOperator(string command)
    {
        if (command.IndexOfAny(ShellControlChars) >= 0)
            return true;
        // Command substitution — "$( ... )" — lets one command's output feed
        // another's arguments, so it gets the same treatment as chaining.
        if (command.Contains("$(", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// Checks whether the tokenized command matches a safe-command entry —
    /// every word of the entry against the same-position token, so "git
    /// status" only matches when the second token is literally "status"
    /// (not "git status-like-thing" or "git statusfoo").
    /// </summary>
    private static bool MatchesSafeCommand(List<string> tokens, string safe)
    {
        var safeTokens = safe.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (safeTokens.Length == 0 || tokens.Count < safeTokens.Length)
            return false;

        var program = StripPathAndExtension(tokens[0]);
        var safeProgram = StripPathAndExtension(safeTokens[0]);
        if (!string.Equals(program, safeProgram, StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 1; i < safeTokens.Length; i++)
        {
            if (!string.Equals(tokens[i], safeTokens[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Minimal shell-style tokenizer: splits on whitespace, honoring single
    /// and double quotes (so quoted arguments containing spaces stay whole).
    /// Good enough for classification — we only need the leading words, not
    /// a full shell grammar.
    /// </summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;

        foreach (var c in command)
        {
            if (quote != null)
            {
                if (c == quote)
                    quote = null;
                else
                    sb.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        return tokens;
    }

    private static string StripPathAndExtension(string token)
    {
        var idx = token.LastIndexOfAny(['/', '\\']);
        if (idx >= 0)
            token = token[(idx + 1)..];

        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
        {
            if (token.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                token = token[..^ext.Length];
                break;
            }
        }

        return token;
    }

    private static string GetCommand(ToolCall call)
    {
        if (call.Arguments.TryGetValue("command", out var cmd) && cmd != null)
            return cmd.ToString() ?? string.Empty;
        return string.Empty;
    }
}
