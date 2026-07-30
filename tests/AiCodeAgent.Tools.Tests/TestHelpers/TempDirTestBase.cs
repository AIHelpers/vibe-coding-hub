using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Tools.Tests.TestHelpers;

/// <summary>
/// Base class providing a temporary working directory that is cleaned up after each test.
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    protected string WorkingDir { get; }

    protected TempDirTestBase()
    {
        WorkingDir = Path.Combine(Path.GetTempPath(), "aiagent-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkingDir);
    }

    protected string WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(WorkingDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    protected string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(WorkingDir, relativePath));

    protected AgentExecutionContext Context(bool readOnly = false, List<string>? allowedPaths = null) => new()
    {
        SessionId = "test",
        WorkingDirectory = WorkingDir,
        IsReadOnly = readOnly,
        AllowedPaths = allowedPaths ?? new()
    };

    protected static ToolCall Call(Dictionary<string, object?> args) => new()
    {
        Id = "test-call",
        Name = "test",
        Arguments = args
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkingDir))
                Directory.Delete(WorkingDir, recursive: true);
        }
        catch { /* ignore cleanup errors */ }
        GC.SuppressFinalize(this);
    }
}