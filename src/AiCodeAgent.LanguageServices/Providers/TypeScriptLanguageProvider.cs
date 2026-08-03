using AiCodeAgent.LanguageServices.Models;

namespace AiCodeAgent.LanguageServices.Providers;

/// <summary>
/// TypeScript language provider that spawns `typescript-language-server`
/// (wraps tsserver). The binary must be installed via npm:
///   npm install -g typescript-language-server typescript
/// </summary>
public class TypeScriptLanguageProvider : ILanguageProvider
{
    public string LangId => "typescript";
    public string[] FileExtensions => [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];

    public LspServerDescriptor Lsp { get; }

    public TypeScriptLanguageProvider()
    {
        Lsp = new LspServerDescriptor(
            Command: "typescript-language-server",
            Args: ["--stdio"],
            BuildInitParams: BuildInitParams);
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