using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.App.Services;

/// <summary>
/// The sample project that ships inside the app: <c>samples/TodoApp</c> of the repository, embedded by
/// <c>AiCodeAgent.App.csproj</c> as resources named <c>sample/TodoApp/&lt;path&gt;</c>.
/// </summary>
public sealed class EmbeddedSampleFileSource : ISampleFileSource
{
    private const string Prefix = "sample/TodoApp/";
    private readonly Assembly _assembly;

    public EmbeddedSampleFileSource(Assembly? assembly = null) => _assembly = assembly ?? typeof(EmbeddedSampleFileSource).Assembly;

    public IReadOnlyList<SampleFile> GetFiles()
    {
        var files = new List<SampleFile>();
        foreach (var resource in _assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var relative = name[Prefix.Length..];
            if (relative.Length == 0) continue;
            var captured = resource;
            files.Add(new SampleFile(relative, () => _assembly.GetManifestResourceStream(captured)
                ?? throw new FileNotFoundException($"Embedded sample file not found: {captured}")));
        }
        return files;
    }
}
