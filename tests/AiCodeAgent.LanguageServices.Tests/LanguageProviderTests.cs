using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;

namespace AiCodeAgent.LanguageServices.Tests;

public class LanguageProviderTests
{
    [Fact]
    public void CSharpProvider_ExposesCSharpLangId()
    {
        var provider = new CSharpLanguageProvider();
        Assert.Equal("csharp", provider.LangId);
        Assert.Contains(".cs", provider.FileExtensions);
    }

    [Fact]
    public void CSharpProvider_UsesDotnetCommand()
    {
        var provider = new CSharpLanguageProvider();
        Assert.Equal("dotnet", provider.Lsp.Command);
        Assert.NotEmpty(provider.Lsp.Args);
    }

    [Fact]
    public void TypeScriptProvider_ExposesTypeScriptLangId()
    {
        var provider = new TypeScriptLanguageProvider();
        Assert.Equal("typescript", provider.LangId);
        Assert.Contains(".ts", provider.FileExtensions);
        Assert.Contains(".tsx", provider.FileExtensions);
    }

    [Fact]
    public void PythonProvider_ExposesPythonLangId()
    {
        var provider = new PythonLanguageProvider();
        Assert.Equal("python", provider.LangId);
        Assert.Contains(".py", provider.FileExtensions);
    }

    [Fact]
    public void BuildInitParams_HasProcessId()
    {
        var provider = new CSharpLanguageProvider();
        var initParams = provider.Lsp.BuildInitParams();
        Assert.True(initParams.ProcessId > 0);
        Assert.NotNull(initParams.Capabilities.TextDocument);
    }

    [Fact]
    public void BuildInitParams_EnablesSynchronizationAndDiagnostics()
    {
        var provider = new TypeScriptLanguageProvider();
        var initParams = provider.Lsp.BuildInitParams();

        Assert.NotNull(initParams.Capabilities.TextDocument?.Synchronization);
        Assert.True(initParams.Capabilities.TextDocument?.Synchronization?.DynamicRegistration);
        Assert.NotNull(initParams.Capabilities.TextDocument?.PublishDiagnostics);
        Assert.True(initParams.Capabilities.TextDocument?.PublishDiagnostics?.RelatedInformation);
    }
}