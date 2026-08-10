using System.Text.Json.Serialization;

namespace AiCodeAgent.Providers.Anthropic;

public class AnthropicResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicContent> Content { get; set; } = new();

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

public class AnthropicContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public Dictionary<string, object?>? Input { get; set; }
}

public class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

public class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content_block")]
    public AnthropicContent? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicDelta? Delta { get; set; }
}

public class AnthropicDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }
}

public class AnthropicModelsResponse
{
    [JsonPropertyName("data")]
    public List<AnthropicModelInfo> Data { get; set; } = new();
}

public class AnthropicModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}