using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Theming;
using Xunit;

namespace AiCodeAgent.App.Tests.Theming;

/// <summary>E4-S2: every text/background pair of the theme tokens in App.axaml reaches WCAG AA, in both themes.</summary>
public class ThemeContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Variants = { "Light", "Dark" };

    private static readonly string[] SyntaxTokenNames =
    {
        "SyntaxKeyword", "SyntaxType", "SyntaxMethod", "SyntaxString", "SyntaxNumber", "SyntaxComment",
        "SyntaxTag", "SyntaxAttribute", "SyntaxDirective", "SyntaxHeading", "SyntaxLink"
    };

    private static readonly string[] TextTokens = { "Text", "TextMuted", "Accent", "Danger", "Success", "Warning" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AiCodeAgent.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string AppFile(params string[] relative) =>
        Path.Combine(new[] { RepoRoot(), "src", "AiCodeAgent.App" }.Concat(relative).ToArray());

    /// <summary>The Theme* colours of one variant, read from App.axaml: the token table.</summary>
    private static Dictionary<string, string> Tokens(string variant)
    {
        var doc = XDocument.Load(AppFile("App.axaml"));
        var dictionary = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == variant)
            .Single();
        return dictionary.Elements()
            .Where(e => e.Name.LocalName == "Color" && ((string?)e.Attribute(X + "Key"))?.StartsWith("Theme") == true)
            .ToDictionary(e => ((string)e.Attribute(X + "Key")!).Substring("Theme".Length), e => e.Value.Trim());
    }

    private static HashSet<string> BrushKeys(string variant)
    {
        var doc = XDocument.Load(AppFile("App.axaml"));
        var dictionary = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == variant)
            .Single();
        return dictionary.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => (string?)e.Attribute(X + "Key") ?? "")
            .ToHashSet();
    }

    public static IEnumerable<object[]> AllVariants() => Variants.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void BothThemes_DefineEveryToken(string variant)
    {
        var t = Tokens(variant);
        var required = new[]
        {
            "Text", "TextMuted", "Surface", "SurfaceRaised", "Border", "Accent", "Danger", "Success", "Warning",
            "CodeBg", "CodeFg", "DangerSolid", "SuccessSolid", "AccentSolid", "OnSolid"
        }.Concat(SyntaxTokenNames);
        var missing = required.Where(n => !t.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0, $"{variant}: missing {string.Join(", ", missing)}");

        // Every colour also has a brush for the views.
        var brushes = BrushKeys(variant);
        var noBrush = t.Keys.Where(n => !brushes.Contains($"Theme{n}Brush")).ToList();
        Assert.True(noBrush.Count == 0, $"{variant}: no brush for {string.Join(", ", noBrush)}");
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void Text_OnSurfaces_ReachesAA(string variant)
    {
        var t = Tokens(variant);
        var failures = new List<string>();
        foreach (var text in TextTokens)
            foreach (var surface in new[] { "Surface", "SurfaceRaised" })
                Check(failures, t, text, surface, Contrast.AaText);
        // Quiet text also sits on the code background.
        Check(failures, t, "Text", "CodeBg", Contrast.AaText);
        Check(failures, t, "TextMuted", "CodeBg", Contrast.AaText);
        Assert.True(failures.Count == 0, $"{variant}:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void Code_AndEverySyntaxColour_ReachAA_OnTheCodeBackground(string variant)
    {
        var t = Tokens(variant);
        var failures = new List<string>();
        Check(failures, t, "CodeFg", "CodeBg", Contrast.AaText);
        foreach (var name in SyntaxTokenNames)
            Check(failures, t, name, "CodeBg", Contrast.AaText);
        // Added / removed lines of a patch reuse the status colours.
        Check(failures, t, "Success", "CodeBg", Contrast.AaText);
        Check(failures, t, "Danger", "CodeBg", Contrast.AaText);
        Assert.True(failures.Count == 0, $"{variant}:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void WhiteText_OnSolidButtons_ReachesAA(string variant)
    {
        var t = Tokens(variant);
        var failures = new List<string>();
        foreach (var solid in new[] { "DangerSolid", "SuccessSolid", "AccentSolid" })
            Check(failures, t, "OnSolid", solid, Contrast.AaText);
        Assert.True(failures.Count == 0, $"{variant}:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void Borders_AreVisible_AgainstTheSurface(string variant)
    {
        var failures = new List<string>();
        Check(failures, Tokens(variant), "Border", "Surface", Contrast.AaLarge);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void EveryThemeResourceTheEditorAsks_ForExists(string variant)
    {
        // The highlighter looks these up by name; a typo would silently fall back to unreadable library colours.
        var t = Tokens(variant);
        foreach (var category in Enum.GetValues<SyntaxCategory>())
        {
            var key = SyntaxTokens.ResourceKey(category);
            if (key == null) continue;
            Assert.True(t.ContainsKey(key.Substring("Theme".Length)), $"{variant}: {key} is not defined in App.axaml");
        }
    }

    [Fact]
    public void Tokens_AreDifferentInTheTwoThemes()
    {
        var light = Tokens("Light");
        var dark = Tokens("Dark");
        Assert.True(Contrast.RelativeLuminance(light["Surface"]) > 0.8);
        Assert.True(Contrast.RelativeLuminance(dark["Surface"]) < 0.05);
        Assert.NotEqual(light["SyntaxKeyword"], dark["SyntaxKeyword"]);
    }

    // ----- the views use the tokens -----

    [Fact]
    public void Views_HaveNoHardCodedTextOrStatusColours()
    {
        var offenders = new List<string>();
        var pattern = new Regex(@"\b(Foreground|Fill|Stroke)=""#[0-9A-Fa-f]{3,8}""", RegexOptions.Compiled);
        var solid = new Regex(@"\bBackground=""#(C62828|FF4444|2E7D32)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(AppFile("Views"), "*.axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in pattern.Matches(text)) offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            foreach (Match m in solid.Matches(text)) offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }
        Assert.True(offenders.Count == 0, "Use the Theme* brushes from App.axaml instead of:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Text_IsNotDimmedWithOpacity_UseTheMutedToken()
    {
        // Opacity multiplies the text colour into the background, so the contrast check on the token table cannot see it.
        // Dim text with Foreground="{DynamicResource ThemeTextMutedBrush}" instead (muted text is AA-checked above).
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppFile("Views"), "*.axaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var e in doc.Descendants())
            {
                var name = e.Name.LocalName;
                var opacity = (string?)e.Attribute("Opacity");
                if ((name == "TextBlock" || name == "SelectableTextBlock") && opacity != null
                    && double.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture) < 0.8)
                    offenders.Add($"{Path.GetFileName(file)}: <{name} Opacity=\"{opacity}\">");

                if (name == "Style" && ((string?)e.Attribute("Selector"))?.StartsWith("TextBlock", StringComparison.Ordinal) == true)
                    foreach (var setter in e.Elements().Where(x => x.Name.LocalName == "Setter" && (string?)x.Attribute("Property") == "Opacity"))
                    {
                        var v = (string?)setter.Attribute("Value");
                        if (v != null && double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) < 0.8)
                            offenders.Add($"{Path.GetFileName(file)}: Style {e.Attribute("Selector")!.Value} sets Opacity {v}");
                    }
            }
        }
        Assert.True(offenders.Count == 0, "Dim text with ThemeTextMutedBrush, not Opacity:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Markdown_InlineCodeAndCodeBlocks_UseTheCodeTokens()
    {
        var styles = File.ReadAllText(AppFile("Styles", "MarkdownTheme.axaml"));
        Assert.Contains("ctxt|CCode", styles);
        Assert.Contains("ThemeCodeFgBrush", styles);
        Assert.Contains("ThemeCodeBgBrush", styles);
        Assert.Contains("Border.CodeBlock", styles);

        foreach (var view in new[] { "ChatTimelineView.axaml", "MarkdownOutputView.axaml" })
            Assert.Contains("MarkdownTheme.axaml", File.ReadAllText(AppFile("Views", view)));
    }

    [Fact]
    public void WelcomeMessage_IsPlainWrappedMarkdown_WithACleanList()
    {
        var text = ChatViewModel.WelcomeText;
        Assert.DoesNotContain(text, char.IsSurrogate); // no emoji (they render differently on every OS)
        Assert.DoesNotContain('○', text);
        var bullets = text.Split('\n').Where(l => l.StartsWith("- ")).ToList();
        Assert.True(bullets.Count >= 5);

        // The chat viewers wrap long lines instead of scrolling them sideways.
        var chat = File.ReadAllText(AppFile("Views", "ChatTimelineView.axaml"));
        Assert.Equal(3, Regex.Matches(chat, @"ctl:MarkdownWrap\.WrapText=""True""").Count);
    }

    [Fact]
    public void SettingsThemeHint_DoesNotNameWindows()
    {
        var settings = File.ReadAllText(AppFile("Views", "SettingsView.axaml"));
        Assert.Contains("System follows your OS light/dark setting.", settings);
        Assert.DoesNotContain("follows your Windows", settings);
    }

    private static void Check(List<string> failures, Dictionary<string, string> tokens, string fg, string bg, double minimum)
    {
        var ratio = Contrast.Ratio(tokens[fg], tokens[bg]);
        if (ratio < minimum)
            failures.Add($"{fg} {tokens[fg]} on {bg} {tokens[bg]} = {ratio:0.00}:1 (needs {minimum}:1)");
    }
}
