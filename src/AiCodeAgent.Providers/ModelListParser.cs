using System.Text.Json;

namespace AiCodeAgent.Providers;

/// <summary>
/// Reads the "which models can this account use" answers of the providers into plain id lists. Tolerant: entries
/// of an unexpected shape are skipped, and text that is not JSON gives an empty list.
/// </summary>
public static class ModelListParser
{
    // OpenAI's /v1/models also lists embeddings, speech, images, moderation and so on; none of them can chat.
    private static readonly string[] NotChat =
    {
        "embedding", "tts", "whisper", "dall-e", "image", "audio", "realtime", "transcribe",
        "moderation", "omni-moderation", "sora", "davinci", "babbage", "-instruct"
    };

    /// <summary><c>GET /v1/models</c> of api.openai.com: <c>{ "data": [ { "id": "gpt-…" } ] }</c>, chat-capable models only.</summary>
    public static string[] ParseOpenAi(string json) =>
        ParseOpenAiCompatible(json).Where(IsOpenAiChatModel).ToArray();

    /// <summary><c>GET /v1/models</c> of any OpenAI-compatible server (LM Studio, vLLM, Groq…): every id, unfiltered.</summary>
    public static string[] ParseOpenAiCompatible(string json) => Ids(json, "data", "id");

    /// <summary><c>GET /v1/models?limit=1000</c> of api.anthropic.com: <c>{ "data": [ { "id": "claude-…", "type": "model" } ] }</c>.</summary>
    public static string[] ParseAnthropic(string json) => Ids(json, "data", "id");

    /// <summary><c>GET /api/tags</c> of Ollama: <c>{ "models": [ { "name": "llama3.1:latest" } ] }</c>.</summary>
    public static string[] ParseOllama(string json) => Ids(json, "models", "name");

    /// <summary>True for ids that look like a chat model (gpt-…, o1/o3…, chatgpt-…), false for embeddings, speech, images…</summary>
    public static bool IsOpenAiChatModel(string id)
    {
        var lower = id.ToLowerInvariant();
        if (NotChat.Any(lower.Contains)) return false;
        return lower.StartsWith("gpt-") || lower.StartsWith("chatgpt-") || lower.StartsWith("codex-")
               || (lower.Length > 1 && lower[0] == 'o' && char.IsDigit(lower[1]));
    }

    private static string[] Ids(string json, string listProperty, string idProperty)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(listProperty, out var list)
                || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return list.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(idProperty, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
