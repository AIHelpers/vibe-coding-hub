using System.Reflection;
using System.Text;
using System.Text.Json;
using AiCodeAgent.Core.Agent;

namespace AiCodeAgent.Core.Flows;

/// <summary>A ready-made flow shown in the template gallery.</summary>
/// <param name="Id">File name without extension, e.g. <c>bug-fix</c>.</param>
/// <param name="Title">Card title.</param>
/// <param name="Icon">Emoji for the card.</param>
/// <param name="SampleTask">A task to try the flow with.</param>
/// <param name="Flow">The flow definition, as in <c>examples/pipelines/&lt;id&gt;.json</c>.</param>
public sealed record FlowTemplate(string Id, string Title, string Icon, string SampleTask, SdlcPipelineDefinition Flow)
{
    public string Description => Flow.Description ?? string.Empty;

    /// <summary>The characters the flow needs, in step order.</summary>
    public IReadOnlyList<string> Characters => Flow.Stages
        .Select(s => s.Character)
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => c!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>What using a template has to add to a project: characters (parents first) and the skills they name.</summary>
/// <param name="CharactersToCreate">Example characters the project lacks, templates they extend first.</param>
/// <param name="SkillsToCreate">Example skills those characters use that the project lacks.</param>
/// <param name="Unavailable">Characters the flow needs that neither exist nor ship as an example.</param>
public sealed record TemplateInstallPlan(
    IReadOnlyList<string> CharactersToCreate,
    IReadOnlyList<string> SkillsToCreate,
    IReadOnlyList<string> Unavailable)
{
    public bool NothingToCreate => CharactersToCreate.Count == 0 && SkillsToCreate.Count == 0;
}

/// <summary>
/// The files of <c>examples/</c> (flows, characters, skills) that ship inside the app, so the template gallery works
/// without the repository. Built from the embedded resources, or from a folder (tests, a checkout).
/// Paths are relative with forward slashes: <c>pipelines/bug-fix.json</c>, <c>characters/quinn-qa.md</c>,
/// <c>skills/csharp/SKILL.md</c>.
/// </summary>
public sealed class ExampleLibrary
{
    private const string ResourcePrefix = "examples/";
    private const string CatalogFile = "pipelines/templates.json";
    private static readonly Lazy<ExampleLibrary> Embedded = new(LoadEmbedded);

    private readonly Dictionary<string, string> _files;

    private ExampleLibrary(Dictionary<string, string> files) => _files = files;

    /// <summary>The examples compiled into this assembly.</summary>
    public static ExampleLibrary FromEmbedded() => Embedded.Value;

    /// <summary>The examples in a folder laid out like the repository's <c>examples/</c>.</summary>
    public static ExampleLibrary FromDirectory(string examplesDirectory)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(string sub, string pattern, SearchOption option)
        {
            var dir = Path.Combine(examplesDirectory, sub);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, pattern, option))
            {
                var relative = Path.GetRelativePath(examplesDirectory, file).Replace('\\', '/');
                files[relative] = File.ReadAllText(file);
            }
        }
        AddAll("pipelines", "*.json", SearchOption.TopDirectoryOnly);
        AddAll("characters", "*.md", SearchOption.TopDirectoryOnly);
        AddAll("skills", "SKILL.md", SearchOption.AllDirectories);
        return new ExampleLibrary(files);
    }

    private static ExampleLibrary LoadEmbedded()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(ExampleLibrary).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }
        return new ExampleLibrary(files);
    }

    /// <summary>Every file path in the library.</summary>
    public IReadOnlyCollection<string> Paths => _files.Keys;

    public string? ReadFile(string path) => _files.TryGetValue(path, out var text) ? text : null;

    /// <summary>Names of the example flows (every <c>pipelines/*.json</c> except the catalog).</summary>
    public IReadOnlyList<string> FlowNames => _files.Keys
        .Where(p => p.StartsWith("pipelines/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !p.Equals(CatalogFile, StringComparison.OrdinalIgnoreCase))
        .Select(p => Path.GetFileNameWithoutExtension(p))
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> CharacterIds => _files.Keys
        .Where(p => p.StartsWith("characters/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        .Select(p => Path.GetFileNameWithoutExtension(p))
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> SkillNames => _files.Keys
        .Where(p => p.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Split('/')[1])
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>One example flow, or null when it is missing or not valid JSON.</summary>
    public SdlcPipelineDefinition? GetFlow(string name)
    {
        var json = ReadFile($"pipelines/{name}.json");
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>The gallery entries from <c>pipelines/templates.json</c>, in file order; entries whose flow is missing are left out.</summary>
    public IReadOnlyList<FlowTemplate> Templates()
    {
        var json = ReadFile(CatalogFile);
        if (json == null) return Array.Empty<FlowTemplate>();
        var result = new List<FlowTemplate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("templates", out var list) || list.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in list.EnumerateArray())
            {
                var id = Str(item, "flow");
                if (id == null || GetFlow(id) is not { } flow) continue;
                result.Add(new FlowTemplate(id, Str(item, "title") ?? id, Str(item, "icon") ?? "🔀", Str(item, "sampleTask") ?? string.Empty, flow));
            }
        }
        catch (JsonException) { /* a broken catalog shows no templates */ }
        return result;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// What a project has to get before <paramref name="template"/> can run: the example characters it lacks (the templates
    /// they extend first) and the example skills those use. Built-in characters and anything already present are left alone.
    /// </summary>
    public TemplateInstallPlan Plan(FlowTemplate template, ISet<string> existingCharacters, ISet<string>? existingSkills)
    {
        var characters = new List<string>();
        var skills = new List<string>();
        var unavailable = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCharacter(string id)
        {
            if (existingCharacters.Contains(id) || characters.Contains(id, StringComparer.OrdinalIgnoreCase) || !visiting.Add(id)) return;
            var text = ReadFile($"characters/{id}.md");
            if (text == null)
            {
                if (!unavailable.Contains(id, StringComparer.OrdinalIgnoreCase)) unavailable.Add(id);
                return;
            }
            var (parent, used) = ReadCharacter(text);
            if (parent != null) AddCharacter(parent);
            characters.Add(id);
            foreach (var skill in used)
            {
                if (existingSkills == null || existingSkills.Contains(skill) || skills.Contains(skill, StringComparer.OrdinalIgnoreCase)) continue;
                if (ReadFile($"skills/{skill}/SKILL.md") != null) skills.Add(skill);
            }
        }

        foreach (var id in template.Characters) AddCharacter(id);
        return new TemplateInstallPlan(characters, skills, unavailable);
    }

    /// <summary>
    /// Write the planned characters to <paramref name="charactersDirectory"/> and the planned skills to
    /// <paramref name="skillsDirectory"/> (skipped when null). Existing files are never overwritten.
    /// Returns the files written.
    /// </summary>
    public IReadOnlyList<string> Install(TemplateInstallPlan plan, string charactersDirectory, string? skillsDirectory)
    {
        var written = new List<string>();
        foreach (var id in plan.CharactersToCreate)
        {
            var text = ReadFile($"characters/{id}.md");
            if (text == null) continue;
            var path = Path.Combine(charactersDirectory, id + ".md");
            if (WriteNew(path, text)) written.Add(path);
        }
        if (skillsDirectory != null)
        {
            foreach (var name in plan.SkillsToCreate)
            {
                var text = ReadFile($"skills/{name}/SKILL.md");
                if (text == null) continue;
                var path = Path.Combine(skillsDirectory, name, "SKILL.md");
                if (WriteNew(path, text)) written.Add(path);
            }
        }
        return written;
    }

    private static bool WriteNew(string path, string text)
    {
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    /// <summary>The <c>extends</c> parent and the skill names (<c>skills</c>, <c>pinned-skills</c>) from a character file's inline frontmatter.</summary>
    internal static (string? Extends, List<string> Skills) ReadCharacter(string text)
    {
        string? extends = null;
        var skills = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, skills);
        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("extends:", StringComparison.Ordinal))
                extends = line["extends:".Length..].Trim();
            else if (line.StartsWith("skills:", StringComparison.Ordinal) || line.StartsWith("pinned-skills:", StringComparison.Ordinal))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim().Trim('[', ']');
                skills.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return (string.IsNullOrEmpty(extends) ? null : extends, skills);
    }
}
