using System.Text.Json;

namespace AiCodeAgent.Core.Models;

public enum MessageRole { System, User, Assistant, Tool }

public record Message
{
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Name { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int TokenCount { get; set; }
}

public record ToolCall
{
    public string Id { get; init; } = GenerateToolCallId();
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = new();

    private static string GenerateToolCallId()
    {
        // Use cryptographically random bytes for a secure, unique ID
        Span<byte> randomBytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
        return Convert.ToHexString(randomBytes).ToLower();
    }
}

public record ToolResult
{
    public string ToolCallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public object? Data { get; init; }
}

public record CompletionRequest
{
    public List<Message> Messages { get; init; } = new();
    public List<ToolDefinition> Tools { get; init; } = new();
    public string? SystemPrompt { get; init; }
    public CompletionOptions Options { get; init; } = new();
}

public record CompletionOptions
{
    public float Temperature { get; init; } = 0.7f;
    public int MaxTokens { get; init; } = 8192;
    public bool Stream { get; init; } = true;
    public string? Model { get; init; }
    public float TopP { get; init; } = 1.0f;
}

public record CompletionResponse
{
    public string Content { get; init; } = string.Empty;
    public List<ToolCall> ToolCalls { get; init; } = new();
    public TokenUsage Usage { get; init; } = new();
    public string Model { get; init; } = string.Empty;
    public string StopReason { get; init; } = string.Empty;
}

public record TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}

public record StreamChunk
{
    public string Delta { get; init; } = string.Empty;
    public List<ToolCall>? ToolCalls { get; init; }
    public bool IsFinished { get; init; }
    public string? FinishReason { get; init; }
    public TokenUsage? Usage { get; init; }
}

public record ToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonSchema Parameters { get; init; } = new();
}

public record JsonSchema
{
    public string Type { get; init; } = "object";
    public Dictionary<string, PropertySchema> Properties { get; init; } = new();
    public List<string> Required { get; init; } = new();
}

public record PropertySchema
{
    public string Type { get; init; } = "string";
    public string Description { get; init; } = string.Empty;
    public List<string>? Enum { get; init; }
    public PropertySchema? Items { get; init; }
}