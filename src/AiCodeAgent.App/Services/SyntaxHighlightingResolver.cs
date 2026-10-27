using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Theming;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Maps file extensions to AvaloniaEdit highlighting definitions (falls back to plain text for unknown extensions) and
/// colours them from the theme tokens in <c>App.axaml</c> (<c>ThemeSyntax*</c>), one set per light/dark variant (E4-S2).
/// The built-in definitions are authored for white backgrounds (Navy, DarkBlue, ...), so each named colour is classified
/// with <see cref="SyntaxTokens"/>; a name the table does not know keeps its own hue but is made readable
/// (<see cref="Contrast.EnsureContrast"/>) on the editor background.
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
        [".json"] = "Json",
        [".jsonc"] = "Json",
        [".md"] = "MarkDown",
        [".markdown"] = "MarkDown",
        [".xml"] = "XML",
        [".xaml"] = "XML",
        [".axaml"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "CSS",
        [".less"] = "CSS",
        [".sql"] = "TSQL",
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
        [".vb"] = "VB",
        [".diff"] = "Patch",
        [".patch"] = "Patch",
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

    // The theme variant the shared definitions currently carry colours for.
    private static ThemeVariant? _appliedVariant;

    // The foreground each named colour had in the library, remembered before the first override.
    private static readonly ConditionalWeakTable<HighlightingColor, StrongBox<Color?>> Originals = new();

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
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        if (!Equals(_appliedVariant, variant))
        {
            foreach (var cached in Cache.Values)
                ApplyTheme(cached);
            _appliedVariant = variant;
        }

        return definition;
    }

    private static IHighlightingDefinition? GetOrLoad(string languageName)
    {
        if (Cache.TryGetValue(languageName, out var cached))
            return cached;

        try
        {
            var manager = HighlightingManager.Instance;
            // Definition names are case-sensitive ("Json", "MarkDown"); fall back to a case-insensitive search.
            var definition = manager.GetDefinition(languageName)
                ?? manager.HighlightingDefinitions.FirstOrDefault(d => d.Name.Equals(languageName, StringComparison.OrdinalIgnoreCase));
            if (definition != null)
            {
                ApplyTheme(definition);
                Cache[languageName] = definition;
            }
            return definition;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The colour of a theme token for the variant in effect, or null if the resource is missing (tests without App.axaml).</summary>
    private static Color? ThemeColor(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Color color)
            return color;
        return null;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Recolours every named colour of a definition for the theme variant in effect.</summary>
    private static void ApplyTheme(IHighlightingDefinition definition)
    {
        if (definition.NamedHighlightingColors == null)
            return;

        var codeBg = ThemeColor("ThemeCodeBg");

        foreach (var color in definition.NamedHighlightingColors)
        {
            if (color == null)
                continue;

            var original = Originals.GetValue(color, c => new StrongBox<Color?>(
                (c.Foreground?.GetBrush(null) as ISolidColorBrush)?.Color));

            Color? foreground = null;
            var category = SyntaxTokens.Classify(color.Name);
            var key = SyntaxTokens.ResourceKey(category);
            if (key != null)
                foreground = ThemeColor(key);

            if (foreground == null && category != SyntaxCategory.Default && original.Value is { } orig && codeBg is { } bg)
                foreground = Color.Parse(Contrast.EnsureContrast(Hex(orig), Hex(bg)));

            color.Foreground = foreground is { } fg ? new SimpleHighlightingBrush(fg) : null;
            // No coloured blocks behind text (the library has e.g. yellow section markers meant for white pages).
            color.Background = null;
        }
    }
}
