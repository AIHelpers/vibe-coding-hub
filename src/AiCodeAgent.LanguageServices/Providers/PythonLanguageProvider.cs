using AiCodeAgent.LanguageServices.Models;

namespace AiCodeAgent.LanguageServices.Providers;

/// <summary>
/// Python language provider that spawns `pyright-langserver`.
/// The binary must be installed via pip (or npm):
///   pip install pyright
/// </summary>
public class PythonLanguageProvider : ILanguageProvider
{
    public string LangId => "python";
    public string[] FileExtensions => [".py", ".pyw"];

    public LspServerDescriptor Lsp { get; }

    public PythonLanguageProvider()
    {
        Lsp = new LspServerDescriptor(
            Command: "pyright-langserver",
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