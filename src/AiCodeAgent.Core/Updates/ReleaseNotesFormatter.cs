using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Updates;

/// <summary>Turns a release's markdown body into a short plain text for the banner.</summary>
public static class ReleaseNotesFormatter
{
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^[ \t]{0,3}#{1,6}[ \t]*", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"(\*\*|__)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\((https?://[^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex BlankRuns = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Format(string? markdown, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HtmlComment.Replace(text, string.Empty);
        text = Heading.Replace(text, string.Empty);
        text = Bold.Replace(text, "$2");
        text = Link.Replace(text, "$1");
        text = Regex.Replace(text, @"^[ \t]*[-*+][ \t]+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"[ \t]+$", string.Empty, RegexOptions.Multiline);
        text = BlankRuns.Replace(text, "\n\n").Trim();

        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf('\n', maxChars);
        if (cut < maxChars / 2) cut = maxChars;
        return text[..cut].TrimEnd() + "\n…";
    }
}
