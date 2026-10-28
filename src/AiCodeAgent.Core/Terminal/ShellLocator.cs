using System.Text;

namespace AiCodeAgent.Core.Terminal;

/// <summary>A program and its arguments, ready for <see cref="PtyStartOptions"/>.</summary>
public sealed record ShellCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string Display => Arguments.Count == 0 ? FileName : FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Chooses the shell for the interactive terminal and finds programs on PATH.</summary>
public static class ShellLocator
{
    /// <summary>
    /// The shell to start. A configured command line wins (when its program exists); otherwise PowerShell 7, Windows PowerShell
    /// or cmd on Windows, and <c>$SHELL</c>, bash or sh elsewhere.
    /// </summary>
    public static ShellCommand Resolve(string? configured) =>
        Resolve(configured, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable, File.Exists);

    public static ShellCommand Resolve(string? configured, bool windows, bool macOs, Func<string, string?> getEnv, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parts = SplitCommandLine(configured);
            if (parts.Count > 0)
            {
                var file = Find(parts[0], windows, getEnv, exists);
                if (file != null) return new ShellCommand(file, parts.Skip(1).ToList());
            }
        }

        if (windows)
        {
            var pwsh = Find("pwsh.exe", true, getEnv, exists);
            if (pwsh != null) return new ShellCommand(pwsh, new[] { "-NoLogo" });

            var systemRoot = getEnv("SystemRoot") ?? @"C:\Windows";
            var powershell = Join(systemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe", true);
            if (exists(powershell)) return new ShellCommand(powershell, new[] { "-NoLogo" });
            var onPath = Find("powershell.exe", true, getEnv, exists);
            if (onPath != null) return new ShellCommand(onPath, new[] { "-NoLogo" });

            var comSpec = getEnv("ComSpec");
            if (!string.IsNullOrEmpty(comSpec) && exists(comSpec)) return new ShellCommand(comSpec, Array.Empty<string>());
            return new ShellCommand("cmd.exe", Array.Empty<string>());
        }

        foreach (var candidate in new[] { getEnv("SHELL"), "/bin/bash", "/bin/sh" })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !exists(candidate)) continue;
            var name = Path.GetFileName(candidate);
            // On macOS a terminal starts a login shell, so that PATH from the profile is set.
            var login = macOs && name is "bash" or "zsh";
            return new ShellCommand(candidate, login ? new[] { "-l" } : Array.Empty<string>());
        }

        return new ShellCommand("/bin/sh", Array.Empty<string>());
    }

    /// <summary>The full path of a program: as given when it has a directory part, otherwise the first match on PATH.</summary>
    public static string? FindExecutable(string name) =>
        Find(name, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists);

    public static string? Find(string name, bool windows, Func<string, string?> getEnv, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var extensions = windows
            ? (getEnv("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        IEnumerable<string> Variants(string path)
        {
            yield return path;
            if (windows && string.IsNullOrEmpty(ExtensionOf(path)))
                foreach (var ext in extensions) yield return path + ext;
        }

        var hasDirectory = name.IndexOfAny(new[] { '/', '\\' }) >= 0 || (windows && name.Length > 1 && name[1] == ':');
        if (hasDirectory)
        {
            foreach (var v in Variants(name))
                if (exists(v)) return v;
            return null;
        }

        var separator = windows ? ';' : ':';
        foreach (var dir in (getEnv("PATH") ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = dir.Trim().Trim('"');
            if (directory.Length == 0) continue;
            foreach (var v in Variants(Join(directory, name, windows)))
                if (exists(v)) return v;
        }

        return null;
    }

    private static string ExtensionOf(string path)
    {
        var name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    private static string Join(string directory, string name, bool windows)
    {
        var separator = windows ? '\\' : '/';
        return directory.EndsWith(separator) ? directory + name : directory + separator + name;
    }

    /// <summary>Splits a command line into arguments; double and single quotes group words, a backslash only escapes a quote.</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        char quote = '\0';
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < commandLine.Length && commandLine[i + 1] == '"') sb.Append(commandLine[++i]);
                else sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }
            }
            else
            {
                sb.Append(c);
                inToken = true;
            }
        }

        if (inToken) result.Add(sb.ToString());
        return result;
    }
}
