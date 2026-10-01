using AiCodeAgent.LanguageServices.Models;

namespace AiCodeAgent.LanguageServices.Providers;

/// <summary>
/// C# language provider pointing at the Roslyn language server.
/// The server is typically located in the .NET SDK installation at:
///   dotnet/$(DOTNET_ROOT)/sdk/{version}/Roslyn/bincore/Microsoft.CodeAnalysis.LanguageServer.exe
/// </summary>
public class CSharpLanguageProvider : ILanguageProvider
{
    public string LangId => "csharp";
    public string[] FileExtensions => [".cs", ".csx"];

    public LspServerDescriptor Lsp { get; }

    public CSharpLanguageProvider()
    {
        Lsp = new LspServerDescriptor(
            Command: "dotnet",
            Args: ["exec", RoslynServerPath],
            BuildInitParams: BuildInitParams);
    }

    private static string RoslynServerPath
    {
        get
        {
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? Path.GetDirectoryName(Environment.ProcessPath)
                ?? string.Empty;

            // Roslyn's language server ships with the .NET SDK:
            //   {dotnet}/sdk/{version}/Roslyn/bincore/Microsoft.CodeAnalysis.LanguageServer.exe
            var sdkRoot = Path.Combine(dotnetRoot, "sdk");
            if (!Directory.Exists(sdkRoot))
                sdkRoot = Path.Combine(dotnetRoot, "packs");

            string? bestMatch = null;
            var sdkDirs = Directory.Exists(sdkRoot) ? Directory.EnumerateDirectories(sdkRoot) : Array.Empty<string>();

            foreach (var sdkDir in sdkDirs)
            {
                var candidate = Path.Combine(sdkDir, "Roslyn", "bincore", "Microsoft.CodeAnalysis.LanguageServer.dll");
                if (File.Exists(candidate))
                {
                    if (bestMatch == null || string.Compare(Path.GetFileName(sdkDir), Path.GetFileName(bestMatch), StringComparison.Ordinal) > 0)
                    {
                        bestMatch = candidate;
                    }
                }
            }

            return bestMatch ?? "Microsoft.CodeAnalysis.LanguageServer.dll";
        }
    }

    private static InitializeParams BuildInitParams() => new(
        ProcessId: Environment.ProcessId,
        RootUri: null,
        RootPath: Directory.GetCurrentDirectory(),
        Capabilities: new ClientCapabilities(
            Workspace: new WorkspaceClientCapabilities(ApplyEdit: true, DidChangeConfiguration: true),
            TextDocument: new TextDocumentClientCapabilities(
                Synchronization: new SynchronizationCapabilities(DynamicRegistration: true, WillSave: true, WillSaveWaitUntil: true, DidSave: true),
                Completion: new CompletionCapabilities(new CompletionItemCapabilities(SnippetSupport: true)),
                Hover: new HoverCapabilities(ContentFormat: true),
                Definition: new DefinitionCapabilities(DynamicRegistration: true),
                PublishDiagnostics: new PublishDiagnosticsCapabilities(RelatedInformation: true)))
    );
}