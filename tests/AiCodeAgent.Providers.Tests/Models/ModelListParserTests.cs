using AiCodeAgent.Providers;

namespace AiCodeAgent.Providers.Tests.Discovery;

/// <summary>E3-S1: the provider model-list answers (recorded shapes) parse into plain id lists.</summary>
public class ModelListParserTests
{
    // Shortened recordings of GET /v1/models (OpenAI), GET /v1/models?limit=1000 (Anthropic) and GET /api/tags (Ollama).
    private const string OpenAiRecording = """
    {
      "object": "list",
      "data": [
        { "id": "gpt-5.6-terra", "object": "model", "created": 1760000000, "owned_by": "system" },
        { "id": "gpt-4o-mini", "object": "model", "created": 1721172741, "owned_by": "system" },
        { "id": "o3", "object": "model", "created": 1744225308, "owned_by": "system" },
        { "id": "text-embedding-3-small", "object": "model", "created": 1705948997, "owned_by": "system" },
        { "id": "whisper-1", "object": "model", "created": 1677532384, "owned_by": "openai-internal" },
        { "id": "dall-e-3", "object": "model", "created": 1698785189, "owned_by": "system" },
        { "id": "tts-1-hd", "object": "model", "created": 1699046015, "owned_by": "system" },
        { "id": "gpt-4o-transcribe", "object": "model", "created": 1742068463, "owned_by": "system" },
        { "id": "gpt-image-1", "object": "model", "created": 1745517030, "owned_by": "system" },
        { "id": "omni-moderation-latest", "object": "model", "created": 1731689265, "owned_by": "system" },
        { "id": "gpt-3.5-turbo-instruct", "object": "model", "created": 1692901427, "owned_by": "system" },
        { "id": "gpt-4o-mini", "object": "model", "created": 1721172741, "owned_by": "system" }
      ]
    }
    """;

    private const string AnthropicRecording = """
    {
      "data": [
        { "type": "model", "id": "claude-sonnet-5-5", "display_name": "Claude Sonnet 5.5", "created_at": "2026-09-28T00:00:00Z" },
        { "type": "model", "id": "claude-haiku-4-5-20251001", "display_name": "Claude Haiku 4.5", "created_at": "2025-10-01T00:00:00Z" },
        { "type": "model", "id": "claude-opus-4-5-20251101", "display_name": "Claude Opus 4.5", "created_at": "2025-11-01T00:00:00Z" }
      ],
      "has_more": false,
      "first_id": "claude-sonnet-5-5",
      "last_id": "claude-opus-4-5-20251101"
    }
    """;

    private const string OllamaRecording = """
    {
      "models": [
        { "name": "llama3.1:latest", "model": "llama3.1:latest", "modified_at": "2026-05-02T10:11:12Z", "size": 4920753328,
          "digest": "46e0c10c039e", "details": { "format": "gguf", "family": "llama", "parameter_size": "8.0B", "quantization_level": "Q4_0" } },
        { "name": "qwen2.5-coder:7b", "model": "qwen2.5-coder:7b", "modified_at": "2026-06-01T08:00:00Z", "size": 4683087332, "digest": "2b0496514337" }
      ]
    }
    """;

    [Fact]
    public void OpenAi_KeepsChatModelsOnly_InOrder_WithoutDuplicates()
    {
        var ids = ModelListParser.ParseOpenAi(OpenAiRecording);

        Assert.Equal(new[] { "gpt-5.6-terra", "gpt-4o-mini", "o3" }, ids);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("o1", true)]
    [InlineData("o4-mini", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("text-embedding-3-large", false)]
    [InlineData("whisper-1", false)]
    [InlineData("dall-e-2", false)]
    [InlineData("tts-1", false)]
    [InlineData("gpt-4o-realtime-preview", false)]
    [InlineData("gpt-4o-audio-preview", false)]
    [InlineData("davinci-002", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("computer-use-preview", false)]
    public void OpenAi_ChatModelFilter(string id, bool chat) =>
        Assert.Equal(chat, ModelListParser.IsOpenAiChatModel(id));

    [Fact]
    public void OpenAiCompatible_KeepsEveryId()
    {
        var ids = ModelListParser.ParseOpenAiCompatible("""{ "data": [ { "id": "qwen2.5-coder-7b-instruct" }, { "id": "text-embedding-nomic-embed-text-v1.5" } ] }""");

        Assert.Equal(new[] { "qwen2.5-coder-7b-instruct", "text-embedding-nomic-embed-text-v1.5" }, ids);
    }

    [Fact]
    public void Anthropic_ReadsIds()
    {
        Assert.Equal(new[] { "claude-sonnet-5-5", "claude-haiku-4-5-20251001", "claude-opus-4-5-20251101" },
            ModelListParser.ParseAnthropic(AnthropicRecording));
    }

    [Fact]
    public void Ollama_ReadsNames()
    {
        Assert.Equal(new[] { "llama3.1:latest", "qwen2.5-coder:7b" }, ModelListParser.ParseOllama(OllamaRecording));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("[]")]
    [InlineData("""{ "error": { "message": "Incorrect API key provided", "type": "invalid_request_error" } }""")]
    [InlineData("""{ "data": "not a list" }""")]
    public void AllParsers_GiveAnEmptyListForUnusableAnswers(string json)
    {
        Assert.Empty(ModelListParser.ParseOpenAi(json));
        Assert.Empty(ModelListParser.ParseOpenAiCompatible(json));
        Assert.Empty(ModelListParser.ParseAnthropic(json));
        Assert.Empty(ModelListParser.ParseOllama(json));
    }

    [Fact]
    public void EntriesOfTheWrongShape_AreSkipped()
    {
        var ids = ModelListParser.ParseAnthropic("""{ "data": [ 5, null, { "type": "model" }, { "id": 7 }, { "id": "claude-haiku-5-5" } ] }""");

        Assert.Equal(new[] { "claude-haiku-5-5" }, ids);
    }

    [Fact]
    public void MergedWithTheBuiltInCatalog_TheAnthropicRecording_PicksTheBalancedModelAndFlagsNothingRetired()
    {
        var catalog = AiCodeAgent.Core.Configuration.ModelCatalogLoader.LoadBuiltIn();
        var ids = ModelListParser.ParseAnthropic(AnthropicRecording);

        Assert.Equal("claude-sonnet-5-5", catalog.DefaultModel("anthropic", ids));
        Assert.All(catalog.Merge("anthropic", ids), o => Assert.False(o.Retired));
        // the dated ids of the account still get their tier
        Assert.Equal(AiCodeAgent.Core.Configuration.ModelTier.Fast, catalog.Merge("anthropic", ids).Single(o => o.Id == "claude-haiku-4-5-20251001").Tier);
    }

    [Fact]
    public void MergedWithTheBuiltInCatalog_AnOldAccountListFlagsTheRetiredModels()
    {
        var catalog = AiCodeAgent.Core.Configuration.ModelCatalogLoader.LoadBuiltIn();
        var ids = new[] { "claude-3-5-sonnet-20241022", "claude-3-opus-20240229", "claude-sonnet-5-5" };

        var merged = catalog.Merge("anthropic", ids);

        Assert.Equal("claude-sonnet-5-5", merged[0].Id);
        Assert.Equal(new[] { "claude-3-5-sonnet-20241022", "claude-3-opus-20240229" }, merged.Where(o => o.Retired).Select(o => o.Id).OrderBy(x => x));
    }
}
