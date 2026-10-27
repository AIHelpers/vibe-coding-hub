namespace AiCodeAgent.Core.Sessions;

/// <summary>One stored chat session of the current working directory, as shown in the session list and the resume banner.</summary>
/// <param name="Title">The name the user gave it, else the first user message.</param>
/// <param name="MessageCount">User and assistant messages with text (what the user sees as messages).</param>
public sealed record ChatSessionInfo(
    string SessionId,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int MessageCount,
    decimal CostUsd);

/// <summary>Lists and manages the chat sessions kept on disk for the current working directory.</summary>
public interface ISessionCatalog
{
    /// <summary>Sessions that have at least one message, newest first.</summary>
    Task<IReadOnlyList<ChatSessionInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>The newest session that has messages, or null.</summary>
    Task<ChatSessionInfo?> GetLatestAsync(CancellationToken ct = default);

    Task RenameAsync(string sessionId, string title, CancellationToken ct = default);

    Task DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Stores the session's total cost so far (shown in the list).</summary>
    Task RecordCostAsync(string sessionId, decimal costUsd, CancellationToken ct = default);
}
