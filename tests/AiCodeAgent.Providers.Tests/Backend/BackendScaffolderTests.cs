using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Providers.Backend;
using Xunit;

namespace AiCodeAgent.Providers.Tests.Backend;

public class BackendScaffolderTests : IDisposable
{
    private readonly string _tempDir;

    public BackendScaffolderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ScaffoldAsync_Writes_InlineTemplates()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        var result = await scaffolder.ScaffoldAsync(["auth"], _tempDir, "My App");

        Assert.Contains("auth/auth.js", result.WrittenFiles);
        Assert.True(File.Exists(Path.Combine(_tempDir, "auth", "auth.js")));
    }

    [Fact]
    public async Task ScaffoldAsync_Replaces_ProjectName_Tokens()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        await scaffolder.ScaffoldAsync(["db"], _tempDir, "My App");

        var dbFile = await File.ReadAllTextAsync(Path.Combine(_tempDir, "db", "db.js"));
        Assert.DoesNotContain("{{PROJECT_NAME}}", dbFile);
    }

    [Fact]
    public async Task ScaffoldAsync_Writes_EnvExample_With_Required_Vars()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        var result = await scaffolder.ScaffoldAsync(["auth"], _tempDir, "My App");

        var envPath = Path.Combine(_tempDir, ".env.example");
        Assert.True(File.Exists(envPath));
        var env = await File.ReadAllTextAsync(envPath);
        Assert.Contains("JWT_SECRET=", env);
        Assert.Contains("JWT_EXPIRES_HOURS=24", env);
    }

    [Fact]
    public async Task ScaffoldAsync_Writes_BackendHook_With_Wiring()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        var result = await scaffolder.ScaffoldAsync(["auth"], _tempDir, "My App");

        var hookPath = Path.Combine(_tempDir, "backend.js");
        Assert.True(File.Exists(hookPath));
        var hook = await File.ReadAllTextAsync(hookPath);
        Assert.Contains("registerBackend", hook);
        Assert.Contains("authMiddleware", hook);
    }

    [Fact]
    public async Task ScaffoldAsync_Resolves_Dependencies()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        var result = await scaffolder.ScaffoldAsync(["payments"], _tempDir, "My App");

        // payments depends on auth + db - all should be present
        Assert.Contains(result.Primitives, p => p.Id == "auth");
        Assert.Contains(result.Primitives, p => p.Id == "db");
        Assert.Contains(result.Primitives, p => p.Id == "payments");
        Assert.True(result.Primitives.Count >= 3);
    }

    [Fact]
    public async Task ScaffoldAsync_Preserves_Existing_Hook_Content_On_Rescaffold()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        var hookPath = Path.Combine(_tempDir, "backend.js");
        await File.WriteAllTextAsync(hookPath, "// my custom header\nmodule.exports = {};\n");

        await scaffolder.ScaffoldAsync(["auth"], _tempDir, "My App");

        var hook = await File.ReadAllTextAsync(hookPath);
        Assert.Contains("// my custom header", hook);
        Assert.Contains("authMiddleware", hook);
    }

    [Fact]
    public async Task ScaffoldAsync_Throws_On_Empty_Primitives()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            scaffolder.ScaffoldAsync([], _tempDir, "My App"));
    }

    [Fact]
    public async Task ScaffoldAsync_Throws_On_Empty_TargetPath()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            scaffolder.ScaffoldAsync(["auth"], "", "My App"));
    }

    [Fact]
    public async Task ScaffoldAsync_Throws_On_Empty_ProjectName()
    {
        var catalog = new BackendPrimitiveCatalog();
        var scaffolder = new BackendScaffolder(catalog);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            scaffolder.ScaffoldAsync(["auth"], _tempDir, ""));
    }
}