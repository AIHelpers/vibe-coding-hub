using System;
using System.Collections.Generic;
using System.IO;
using AvaloniaEdit.Highlighting;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Maps file extensions to AvaloniaEdit highlighting definitions.
/// Falls back to plain text for unknown extensions.
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
        [".gitignore"] = "INI",
        [".editorconfig"] = "INI",
    };

    private static readonly Dictionary<string, IHighlightingDefinition> Cache = new(StringComparer.OrdinalIgnoreCase);

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

        return GetOrLoad(languageName);
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
                Cache[languageName] = definition;
            }
            return definition;
        }
        catch
        {
            return null;
        }
    }
}