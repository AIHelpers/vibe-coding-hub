using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.LanguageServices.Tests;

public class LanguageProviderRegistryTests
{
    private static LanguageProviderRegistry CreateRegistry(
        out ILogger<LanguageProviderRegistry> logger,
        out ILoggerFactory loggerFactory)
    {
        logger = Substitute.For<ILogger<LanguageProviderRegistry>>();
        loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        return new LanguageProviderRegistry(
            new ILanguageProvider[]
            {
                new CSharpLanguageProvider(),
                new TypeScriptLanguageProvider(),
                new PythonLanguageProvider()
            },
            logger,
            loggerFactory);
    }

    [Fact]
    public void ResolveProvider_ReturnsProviderForKnownExtension()
    {
        var registry = CreateRegistry(out _, out _);

        var provider = registry.ResolveProvider(@"C:\Repo\Program.cs");

        Assert.NotNull(provider);
        Assert.Equal("csharp", provider.LangId);
    }

    [Fact]
    public void ResolveProvider_ReturnsNullForUnknownExtension()
    {
        var registry = CreateRegistry(out _, out _);

        var provider = registry.ResolveProvider(@"C:\Repo\file.xyz");

        Assert.Null(provider);
    }

    [Fact]
    public void ResolveProvider_IsCaseInsensitive()
    {
        var registry = CreateRegistry(out _, out _);

        var provider = registry.ResolveProvider(@"C:\Repo\Program.CS");

        Assert.NotNull(provider);
        Assert.Equal("csharp", provider.LangId);
    }

    [Fact]
    public void ResolveProvider_HandlesTypeScriptAndPython()
    {
        var registry = CreateRegistry(out _, out _);

        Assert.Equal("typescript", registry.ResolveProvider(@"C:\Repo\app.ts")?.LangId);
        Assert.Equal("typescript", registry.ResolveProvider(@"C:\Repo\app.tsx")?.LangId);
        Assert.Equal("typescript", registry.ResolveProvider(@"C:\Repo\app.js")?.LangId);
        Assert.Equal("python", registry.ResolveProvider(@"C:\Repo\main.py")?.LangId);
    }

    [Fact]
    public void Providers_ContainsAllRegisteredLanguages()
    {
        var registry = CreateRegistry(out _, out _);

        var ids = registry.Providers.Select(p => p.LangId).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "csharp", "python", "typescript" }, ids);
    }

    [Fact]
    public async Task GetOrStartClient_ForUnsupportedFile_ReturnsNull()
    {
        var registry = CreateRegistry(out _, out _);

        var client = await registry.GetOrStartClientAsync(@"C:\Repo\file.xyz", @"C:\Repo");

        Assert.Null(client);
    }
}