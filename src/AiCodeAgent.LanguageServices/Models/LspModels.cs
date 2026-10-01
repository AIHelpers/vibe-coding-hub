namespace AiCodeAgent.LanguageServices.Models;

// ===== Core LSP Types =====

public record Position(int Line, int Character);

public record Range(Position Start, Position End);

public record Location(string Uri, Range Range);

public record TextDocumentIdentifier(string Uri);

public record VersionedTextDocumentIdentifier(string Uri, int Version) : TextDocumentIdentifier(Uri);

public record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public record TextDocumentContentChangeEvent(Range? Range, int? RangeLength, string Text);

public record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

public record CompletionContext(CompletionTriggerKind TriggerKind, string? TriggerCharacter = null);

public enum CompletionTriggerKind
{
    Invoked = 1,
    TriggerCharacter = 2,
    TriggerForIncompleteCompletions = 3
}

public record CompletionItem(
    string Label,
    CompletionItemKind Kind = CompletionItemKind.Text,
    string? Detail = null,
    string? Documentation = null,
    string? InsertText = null,
    string? SortText = null,
    string? FilterText = null,
    TextEdit? TextEdit = null,
    string? InsertTextFormat = null);

public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25
}

public record TextEdit(Range Range, string NewText);

/// <summary>
/// Represents a symbol information entry returned by the LSP
/// <c>workspace/symbol</c> request.
/// </summary>
public record SymbolInformation(
    string Name,
    int Kind,
    Location Location,
    string? ContainerName = null);

public record CompletionList(bool IsIncomplete, List<CompletionItem> Items);

public record Hover(HoverContents? Contents, Range? Range = null);

public record HoverContents(string? Kind, string? Value);

public record Diagnostic(
    Range Range,
    DiagnosticSeverity Severity,
    string Message,
    string? Source = null,
    string? Code = null,
    string? RelatedInformation = null);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

public record PublishDiagnosticsParams(string Uri, List<Diagnostic> Diagnostics);

public record InitializeParams(
    int ProcessId,
    string? RootUri,
    string? RootPath,
    ClientCapabilities Capabilities,
    string? Trace = "off");

public record ClientCapabilities(
    WorkspaceClientCapabilities? Workspace = null,
    TextDocumentClientCapabilities? TextDocument = null);

public record WorkspaceClientCapabilities(bool? ApplyEdit = null, bool? DidChangeConfiguration = null);

public record TextDocumentClientCapabilities(
    SynchronizationCapabilities? Synchronization = null,
    CompletionCapabilities? Completion = null,
    HoverCapabilities? Hover = null,
    DefinitionCapabilities? Definition = null,
    PublishDiagnosticsCapabilities? PublishDiagnostics = null);

public record SynchronizationCapabilities(bool? DynamicRegistration = null, bool? WillSave = null, bool? WillSaveWaitUntil = null, bool? DidSave = null);

public record CompletionCapabilities(CompletionItemCapabilities? CompletionItem = null);

public record CompletionItemCapabilities(bool? SnippetSupport = null);

public record HoverCapabilities(bool? ContentFormat = null);

public record DefinitionCapabilities(bool? DynamicRegistration = null);

public record PublishDiagnosticsCapabilities(bool? RelatedInformation = null);

public record InitializeResult(ServerCapabilities Capabilities, ServerInfo? ServerInfo = null);

public record ServerCapabilities(
    TextDocumentSyncOptions? TextDocumentSync = null,
    CompletionOptions? CompletionProvider = null,
    HoverOptions? HoverProvider = null,
    DefinitionOptions? DefinitionProvider = null);

public record TextDocumentSyncOptions(bool? OpenClose = null, int? Change = null, bool? Save = null);

public record CompletionOptions(bool? ResolveProvider = null, List<string>? TriggerCharacters = null);

public record HoverOptions(bool? WorkDoneProgress = null);

public record DefinitionOptions(bool? WorkDoneProgress = null);

public record ServerInfo(string Name, string? Version = null);

// ===== LSP Server Descriptor =====

/// <summary>Describes how to spawn and initialize a language server process.</summary>
public record LspServerDescriptor(
    string Command,
    string[] Args,
    Func<InitializeParams> BuildInitParams);

// ===== Language Provider =====

/// <summary>Describes a language provider that can be resolved by file extension.</summary>
public interface ILanguageProvider
{
    string LangId { get; }
    string[] FileExtensions { get; }
    LspServerDescriptor Lsp { get; }
}

// ===== LSP Client Events =====

/// <summary>Event raised when diagnostics are published for a document.</summary>
public record LspDiagnosticsEvent(string Uri, string FilePath, List<Diagnostic> Diagnostics) : LspEvent;

/// <summary>Base class for LSP events.</summary>
public abstract record LspEvent;

/// <summary>Event raised when the LSP server is ready.</summary>
public record LspServerReadyEvent(string LangId) : LspEvent;

/// <summary>Event raised when the LSP server has an error.</summary>
public record LspServerErrorEvent(string LangId, string Message) : LspEvent;