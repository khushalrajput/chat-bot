using Microsoft.SemanticKernel.Embeddings;

namespace ChatBot.Services;

/// <summary>
/// SK-based embedding service.
///
/// COMPARE WITH OLD EmbeddingService:
///
/// OLD (raw HTTP):
///   - Build GeminiEmbedRequest with nested DTOs
///   - POST to batchEmbedContents endpoint
///   - Deserialize GeminiEmbedResponse
///   - Extract float[] from embeddings[i].values
///
/// NEW (Semantic Kernel):
///   - Call GenerateEmbeddingsAsync(texts)
///   - Get back ReadOnlyMemory<float> per text
///   - Done.
///
/// SK knows which embedding model to use (configured in Program.cs).
/// Swap to OpenAI embeddings = change one line in DI, this code untouched.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly ITextEmbeddingGenerationService _embeddingService;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        ITextEmbeddingGenerationService embeddingService,
        ILogger<EmbeddingService> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await GetEmbeddingsAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(
        List<string> texts,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating embeddings for {Count} texts", texts.Count);

        // ONE LINE replaces entire batch embedding HTTP logic
        // SK handles: batching, API calls, response parsing
        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(
            texts,
            cancellationToken: cancellationToken);

        // SK returns ReadOnlyMemory<float> — convert to float[] for our vector store
        // ReadOnlyMemory<float> is a zero-copy wrapper — ToArray() copies once
        var result = embeddings
            .Select(e => e.ToArray())
            .ToList();

        _logger.LogDebug("Generated {Count} embeddings, dimension={Dim}",
            result.Count, result[0].Length);

        return result;
    }
}
