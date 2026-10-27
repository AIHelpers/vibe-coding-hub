namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>Finds the files of the repository that the sample tests look at.</summary>
internal static class SampleRepo
{
    /// <summary>The repository root: the nearest parent folder that holds <c>AiCodeAgent.slnx</c>.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (AiCodeAgent.slnx).");
    }

    public static string SampleFolder() => Path.Combine(Root(), "samples", "TodoApp");

    public static string ReferencePatch() => Path.Combine(Root(), "samples", "reference", "TodoApp-overdue-fix.patch");
}
