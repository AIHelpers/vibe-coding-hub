using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Maps file extensions to AvaloniaEdit highlighting definitions.
/// Falls back to plain text for unknown extensions.
/// Applies a light theme to the built-in highlighting colors so the
/// editor text is readable on a white background.
/// </summary>
public class SyntaxHighlightingResolver
{
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".csx"] = "C#",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".py"] = "Python",
        [".pyw"] = "Python",
        [".json"] = "JSON",
        [".jsonc"] = "JSON",
        [".md"] = "Markdown",
        [".markdown"] = "Markdown",
        [".xml"] = "XML",
        [".xaml"] = "XML",
        [".axaml"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "CSS",
        [".less"] = "CSS",
        [".sql"] = "SQL",
        [".java"] = "Java",
        [".kt"] = "Kotlin",
        [".kts"] = "Kotlin",
        [".go"] = "Go",
        [".rs"] = "Rust",
        [".rb"] = "Ruby",
        [".php"] = "PHP",
        [".sh"] = "PowerShell",
        [".bash"] = "PowerShell",
        [".ps1"] = "PowerShell",
        [".bat"] = "PowerShell",
        [".cmd"] = "PowerShell",
        [".cpp"] = "C++",
        [".cc"] = "C++",
        [".cxx"] = "C++",
        [".h"] = "C++",
        [".hpp"] = "C++",
        [".c"] = "C++",
        [".swift"] = "Swift",
        [".dart"] = "Dart",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".toml"] = "INI",
        [".ini"] = "INI",
        [".config"] = "XML",
        [".csproj"] = "XML",
        [".sln"] = "XML",
        [".props"] = "XML",
        [".targets"] = "XML",
        [".resx"] = "XML",
        [".vb"] = "VB.NET",
        [".fs"] = "F#",
        [".fsx"] = "F#",
        [".lua"] = "Lua",
        [".pl"] = "Perl",
        [".r"] = "R",
        [".scala"] = "Scala",
        [".groovy"] = "Groovy",
        [".gradle"] = "Groovy",
        [".dockerfile"] = "PowerShell",
        [".makefile"] = "PowerShell",
        [".gitignore"] = "INI",
        [".editorconfig"] = "INI",
    };

    private static readonly Dictionary<string, IHighlightingDefinition> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Dark-theme palette (VS Code "Dark+"-like), used when the app theme is dark.
    // Built lazily: it derives from LightThemeColors, which is declared further down.
    private static Dictionary<string, string>? _darkThemeColors;
    private static Dictionary<string, string> DarkThemeColors => _darkThemeColors ??= BuildDarkColors();

    private static Dictionary<string, string> BuildDarkColors()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hex) in LightThemeColors)
        {
            d[name] = hex switch
            {
                "#1E1E2E" => "#E8E8F0",   // base text / punctuation
                "#A31515" => "#CE9178",   // strings, links
                "#0000FF" => "#569CD6",   // keywords, tags, headers
                "#FF0000" => "#9CDCFE",   // attributes, CSS properties
                "#098658" => "#B5CEA8",   // numbers
                "#267F99" => "#4EC9B0",   // types
                "#795E26" => "#DCDCAA",   // methods
                "#0451A5" => "#9CDCFE",   // JSON keys
                "#800000" => "#D7BA7D",   // CSS selectors
                "#808080" => "#9B9B9B",   // preprocessor / rules
                "#AF00DB" => "#C586C0",   // directives
                _ => hex
            };
        }
        return d;
    }

    // The theme the shared highlighting definitions currently carry colours for.
    private static bool? _appliedDark;

    // Light-theme color overrides for the built-in (dark) highlighting definitions.
    // Keyed by the named color as used inside AvaloniaEdit's .xshd definitions.
    private static readonly Dictionary<string, string> LightThemeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Base text colors
        ["Text"] = "#1E1E2E",
        ["Default"] = "#1E1E2E",

        // Comments - muted gray-green
        ["Comment"] = "#6A9955",
        ["CommentLine"] = "#6A9955",
        ["CommentBlock"] = "#6A9955",
        ["XmlComment"] = "#6A9955",
        ["DocComment"] = "#6A9955",

        // Strings - rust orange
        ["String"] = "#A31515",
        ["VerbatimString"] = "#A31515",
        ["Char"] = "#A31515",
        ["XmlString"] = "#A31515",
        ["InterpolatedString"] = "#A31515",

        // Keywords - blue
        ["Keyword"] = "#0000FF",
        ["XmlTag"] = "#0000FF",
        ["XmlName"] = "#0000FF",
        ["XmlAttribute"] = "#FF0000",

        // Numbers - teal
        ["Number"] = "#098658",
        ["Digits"] = "#098658",
        ["Float"] = "#098658",
        ["Hex"] = "#098658",

        // Preprocessor / directives - gray
        ["Preprocessor"] = "#808080",
        ["PreprocessorWord"] = "#AF00DB",
        ["PreprocessorText"] = "#1E1E2E",

        // Identifiers / types - dark blue
        ["Type"] = "#267F99",
        ["ClassName"] = "#267F99",
        ["InterfaceName"] = "#267F99",
        ["StructName"] = "#267F99",
        ["EnumName"] = "#267F99",
        ["DelegateName"] = "#267F99",
        ["TypeParameter"] = "#267F99",

        // Method names - dark blue
        ["MethodName"] = "#795E26",
        ["FunctionName"] = "#795E26",

        // Punctuation - dark
        ["Punctuation"] = "#1E1E2E",
        ["Operator"] = "#1E1E2E",
        ["Delimiter"] = "#1E1E2E",
        ["Brace"] = "#1E1E2E",

        // Markdown-specific
        ["Header"] = "#0000FF",
        ["Bold"] = "#1E1E2E",
        ["Italic"] = "#1E1E2E",
        ["Strikethrough"] = "#1E1E2E",
        ["Link"] = "#A31515",
        ["HorizontalRule"] = "#808080",

        // CSS/HTML specific
        ["PropertyName"] = "#FF0000",
        ["PropertyValue"] = "#0000FF",
        ["Selector"] = "#800000",
        ["AtRule"] = "#AF00DB",

        // JSON specific
        ["PropertyKey"] = "#0451A5",
        ["PropertyValue"] = "#A31515",

        // YAML specific
        ["Tag"] = "#0000FF",
        ["Anchor"] = "#A31515",
        ["Alias"] = "#A31515",
    };

    /// <summary>
    /// Resolve a highlighting definition for a file path.
    /// Returns null for unknown extensions (plain text fallback).
    /// </summary>
    public IHighlightingDefinition? Resolve(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            // Check for special filenames
            var fileName = Path.GetFileName(filePath);
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                ext = ".dockerfile";
            else if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                ext = ".makefile";
            else
                return null;
        }

        if (!ExtensionMap.TryGetValue(ext, out var languageName))
            return null;

        var definition = GetOrLoad(languageName);

        // The definitions are shared, so when the app theme flips, recolour the cached ones.
        var dark = AiCodeAgent.App.Services.ThemeService.IsDark;
        if (_appliedDark != dark)
        {
            foreach (var cached in Cache.Values)
                ApplyTheme(cached, dark);
            _appliedDark = dark;
        }

        return definition;
    }

    private static IHighlightingDefinition? GetOrLoad(string languageName)
    {
        if (Cache.TryGetValue(languageName, out var cached))
            return cached;

        try
        {
            var definition = HighlightingManager.Instance.GetDefinition(languageName);
            if (definition != null)
            {
                ApplyTheme(definition, AiCodeAgent.App.Services.ThemeService.IsDark);
                Cache[languageName] = definition;
            }
            return definition;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Overrides the named highlighting colors with the light or dark palette.
    /// This ensures the editor text is readable on a white background even
    /// though the built-in definitions target dark themes.
    /// </summary>
    private static void ApplyTheme(IHighlightingDefinition definition, bool dark)
    {
        var palette = dark ? DarkThemeColors : LightThemeColors;
        if (definition.NamedHighlightingColors == null)
            return;

        foreach (var color in definition.NamedHighlightingColors)
        {
            if (color == null || string.IsNullOrEmpty(color.Name))
                continue;

            if (palette.TryGetValue(color.Name, out var hex))
            {
                color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
                // Clear any background so we don't get colored blocks on white
                color.Background = null;
            }
        }
    }
}