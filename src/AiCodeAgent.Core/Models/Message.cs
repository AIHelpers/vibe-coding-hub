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

    /// <summary>
    /// Optional image attachments (screenshots, pasted images, annotation
    /// captures) carried alongside a User message's text. Populated for
    /// multimodal turns — e.g. a visual annotation on the preview pane, or a
    /// browser-tool screenshot fed back to the model. Providers that support
    /// vision (Anthropic, OpenAI-compatible, Ollama vision models) render
    /// these as inline image content blocks; providers/paths that don't
    /// simply ignore them and fall back to <see cref="Content"/> text.
    /// </summary>
    public List<ImageAttachment>? Images { get; init; }
}

/// <summary>
/// A single inline image attached to a <see cref="Message"/>. Stored as raw
/// base64 image bytes plus a MIME type, matching what every supported
/// provider's multimodal content-block format expects.
/// </summary>
public record ImageAttachment
{
    /// <summary>MIME type, e.g. "image/png" or "image/jpeg".</summary>
    public string MediaType { get; init; } = "image/png";

    /// <summary>Raw image bytes, base64-encoded (no data: URL prefix).</summary>
    public string Base64Data { get; init; } = string.Empty;

    /// <summary>Human-readable origin, e.g. "Preview pane annotation", for logs/UI.</summary>
    public string? SourceDescription { get; init; }
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