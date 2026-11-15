using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.Core.Diagnostics;

/// <summary>What the running build says about itself, for a crash report.</summary>
public sealed record CrashReportContext(string AppVersion, string OperatingSystem, string Runtime, string Architecture)
{
    /// <summary>Reads the operating system, .NET runtime and CPU of the current process.</summary>
    public static CrashReportContext FromCurrentProcess(string appVersion) => new(
        appVersion,
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.OSArchitecture.ToString());
}

/// <summary>A crash report ready to be sent: the issue text and the GitHub address that opens it prefilled.</summary>
/// <param name="Title">Issue title.</param>
/// <param name="Body">Issue body (Markdown).</param>
/// <param name="IssueUrl">A "new issue" page with title and body filled in. Nothing is sent until the user submits it there.</param>
/// <param name="Truncated">True when the exception text was shortened to fit into the address.</param>
public sealed record CrashReport(string Title, string Body, string IssueUrl, bool Truncated);

/// <summary>
/// Turns a handled exception into a prefilled GitHub issue (E9-S6). There is no telemetry: the report is only a link the
/// user chooses to open, and GitHub shows the complete text for review and editing before anything is submitted.
/// Secrets (see <see cref="SecretRedactor"/>) and user names in file paths are removed before the text is built.
/// </summary>
public static class CrashReportBuilder
{
    public const string Repository = "AIHelpers/vibe-coding-hub";

    /// <summary>
    /// Longest address we produce. Browsers and GitHub accept more, but about 8 KB is the practical limit for a link
    /// that survives the operating system's "open URL" hand-over and GitHub's own front end.
    /// </summary>
    public const int MaxUrlLength = 7500;

    private const int MaxTitleLength = 120;

    // Windows user names may contain spaces ("C:\Users\Ann Lee\..."), so there the name runs up to the next separator.
    private static readonly Regex WindowsUserFolderPattern = new(
        @"(?<root>[A-Za-z]:[\\/]+Users[\\/]+)(?<user>[^\\/:""'<>|\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnixUserFolderPattern = new(
        @"(?<root>/Users/|/home/)(?<user>[^\\/\s:""'<>|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="exceptionText">The full exception text (<c>Exception.ToString()</c>).</param>
    /// <param name="context">Version, system and runtime of this build.</param>
    /// <param name="redactor">Removes secrets; defaults to one built from the process environment.</param>
    /// <param name="homeDirectory">The user's home folder, replaced by <c>~</c>; defaults to the current user's.</param>
    public static CrashReport Build(
        string exceptionText,
        CrashReportContext context,
        SecretRedactor? redactor = null,
        string? homeDirectory = null)
    {
        redactor ??= SecretRedactor.FromEnvironment();
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var text = Anonymize(redactor.Redact(exceptionText ?? string.Empty), homeDirectory).Trim();
        var title = BuildTitle(text);

        // The longest prefix of the exception text for which the whole address still fits.
        var low = 0;
        var high = text.Length;
        if (UrlFor(title, RenderBody(context, text, false)).Length <= MaxUrlLength)
            low = high;
        else
        {
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                var candidate = RenderBody(context, Cut(text, mid), true);
                if (UrlFor(title, candidate).Length <= MaxUrlLength) low = mid;
                else high = mid - 1;
            }
        }

        var truncated = low < text.Length;
        var body = RenderBody(context, truncated ? Cut(text, low) : text, truncated);
        return new CrashReport(title, body, UrlFor(title, body), truncated);
    }

    /// <summary>Replaces the home folder with <c>~</c> and any other user name inside a user folder with <c>&lt;user&gt;</c>.</summary>
    internal static string Anonymize(string text, string? homeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(homeDirectory) && homeDirectory!.Length > 3)
        {
            var home = homeDirectory.TrimEnd('\\', '/');
            text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        text = WindowsUserFolderPattern.Replace(text, m => m.Groups["root"].Value + "<user>");
        return UnixUserFolderPattern.Replace(text, m => m.Groups["root"].Value + "<user>");
    }

    private static string BuildTitle(string text)
    {
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0) firstLine = "Unhandled exception";
        var title = "Crash: " + firstLine;
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
    }

    /// <summary>The first <paramref name="length"/> characters, cut at a line end when there is one and never inside a surrogate pair.</summary>
    private static string Cut(string text, int length)
    {
        if (length >= text.Length) return text;
        if (length <= 0) return string.Empty;
        var lineEnd = text.LastIndexOf('\n', length - 1);
        if (lineEnd > 0) return text[..lineEnd].TrimEnd('\r');
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    private static string RenderBody(CrashReportContext context, string exceptionText, bool truncated)
    {
        // A fence longer than any run of backticks inside the text, so the text cannot close it early.
        var longestRun = 0;
        var run = 0;
        foreach (var c in exceptionText)
        {
            run = c == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }
        var fence = new string('`', Math.Max(3, longestRun + 1));

        var sb = new StringBuilder();
        sb.AppendLine("<!-- This report was prefilled by Vibe Coding Hub. Nothing has been sent: review the text, remove anything private and press \"Submit new issue\" to send it. -->");
        sb.AppendLine();
        sb.AppendLine("**What were you doing when it happened?**");
        sb.AppendLine();
        sb.AppendLine("<!-- Please describe the steps. -->");
        sb.AppendLine();
        sb.AppendLine("**Environment**");
        sb.AppendLine();
        sb.AppendLine($"- Version: {context.AppVersion}");
        sb.AppendLine($"- System: {context.OperatingSystem} ({context.Architecture})");
        sb.AppendLine($"- Runtime: {context.Runtime}");
        sb.AppendLine();
        sb.AppendLine("**Exception**");
        sb.AppendLine();
        sb.AppendLine(fence);
        sb.AppendLine(exceptionText);
        sb.AppendLine(fence);
        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("_The text was shortened to fit into a link. The full text is in the log folder (Settings > Open log folder)._");
        }
        return sb.ToString();
    }

    private static string UrlFor(string title, string body) =>
        $"https://github.com/{Repository}/issues/new?labels=crash-report&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
}
