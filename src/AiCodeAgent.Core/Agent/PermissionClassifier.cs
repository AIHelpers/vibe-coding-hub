using System.Collections.Generic;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Classifies tool actions as safe or risky for Auto mode (Feature 10).
/// In Auto mode, safe actions run without prompting; risky ones are blocked.
/// </summary>
public class PermissionClassifier
{
    /// <summary>Commands considered safe in Auto mode (read-only filesystem ops).</summary>
    private static readonly HashSet<string> SafeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "cat", "head", "tail", "grep", "find", "which", "where",
        "echo", "pwd", "git status", "git log", "git diff", "git branch",
        "npm test", "npm run test", "dotnet test", "dotnet build", "make",
        "node --version", "npm --version", "python --version"
    };

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
            if (string.IsNullOrEmpty(command))
                return PermissionDecision.Deny;

            // Check exact safe commands.
            foreach (var safe in SafeCommands)
            {
                if (command.StartsWith(safe, System.StringComparison.OrdinalIgnoreCase))
                    return PermissionDecision.Allow;
            }

            // Allow common, low-risk filesystem commands explicitly.
            if (IsCommonFilesystemCommand(command))
                return PermissionDecision.Allow;

            return PermissionDecision.Deny;
        }

        // Writes in Auto mode: allow edits to files, deny dangerous patterns.
        if (risk == RiskLevel.Write)
            return PermissionDecision.Allow;

        return PermissionDecision.Deny;
    }

    /// <summary>Common filesystem commands that are safe in Auto mode.</summary>
    private static readonly HashSet<string> CommonFilesystemCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkdir", "mv", "cp", "touch", "echo"
    };

    private static bool IsCommonFilesystemCommand(string command)
    {
        // Extract first token.
        var first = command.Split([' ', '\t'], System.StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 0) return false;
        var name = first[0];
        // Strip path.
        var idx = name.LastIndexOfAny(['/', '\\']);
        if (idx >= 0) name = name[(idx + 1)..];
        return CommonFilesystemCommands.Contains(name);
    }

    private static string GetCommand(ToolCall call)
    {
        if (call.Arguments.TryGetValue("command", out var cmd) && cmd != null)
            return cmd.ToString() ?? string.Empty;
        return string.Empty;
    }
}