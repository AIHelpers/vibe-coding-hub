using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiCodeAgent.Providers;

/// <summary>
/// A no-op AI provider that returns friendly error messages when the actual provider
/// cannot be initialized (e.g., due to missing API key).
/// </summary>
public class NoOpAiProvider : IAiProvider
{
    private readonly ILogger _logger;
    private readonly string _errorMessage;

    public string Name => "NoOpProvider";
    public string[] SupportedModels => Array.Empty<string>();

    public NoOpAiProvider(ILogger logger, string errorMessage)
    {
        _logger = logger;
        _errorMessage = errorMessage;
    }

    public Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Attempted to use NoOpAiProvider - {}", _errorMessage);
        return Task.FromResult(new CompletionResponse
        {
            Content = $"Error: {_errorMessage}\n\nPlease configure your API key in the settings and restart the application.",
            Model = "none",
            StopReason = "error"
        });
    }

    public IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Attempted to use NoOpAiProvider - {}", _errorMessage);
        return AsyncEnumerable.Empty<StreamChunk>();
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SupportedModels);
    }
}