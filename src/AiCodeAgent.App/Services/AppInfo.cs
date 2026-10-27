using System;
using System.Reflection;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.App.Services;

/// <summary>What the running build says about itself.</summary>
public static class AppInfo
{
    /// <summary>
    /// The version of this build. The release workflow passes <c>-p:Version=1.2.3</c> (a snapshot build gets
    /// <c>0.0.0-dev.N</c>), which ends up in the assembly's informational version.
    /// </summary>
    public static SemanticVersion CurrentVersion { get; } = Resolve(typeof(AppInfo).Assembly);

    internal static SemanticVersion Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return From(informational, assembly.GetName().Version);
    }

    internal static SemanticVersion From(string? informationalVersion, Version? assemblyVersion)
    {
        if (SemanticVersion.TryParse(informationalVersion, out var informational)) return informational!;
        if (assemblyVersion != null && SemanticVersion.TryParse(assemblyVersion.ToString(), out var fromAssembly)) return fromAssembly!;
        return SemanticVersion.Parse("0.0.0");
    }
}
