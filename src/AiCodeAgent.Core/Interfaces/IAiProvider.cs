using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

public interface IAiProvider
{
    string Name { get; }
    string[] SupportedModels { get; }
    
    Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
    
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context);
}

public interface IContextManager
{
    Task<List<Message>> GetContextAsync(string sessionId);
    Task AddMessageAsync(string sessionId, Message message);
    Task<int> GetTokenCountAsync(string sessionId);
    Task TrimContextAsync(string sessionId, int maxTokens);
    Task ClearAsync(string sessionId);
}

public interface IMemoryStore
{
    Task StoreAsync(string key, string content, Dictionary<string, string>? metadata = null);
    Task<List<MemoryEntry>> SearchAsync(string query, int topK = 5);
    Task<MemoryEntry?> GetAsync(string key);
    Task DeleteAsync(string key);
}

public interface ICodeIndexer
{
    Task IndexDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<List<CodeSnippet>> SearchAsync(string query, int topK = 10);
    Task<string> GetFileContentAsync(string path);
    IAsyncEnumerable<string> GetIndexedFilesAsync();
}

public interface IAgentOrchestrator
{
    Task<AgentResponse> RunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<AgentEvent> StreamRunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default);
}