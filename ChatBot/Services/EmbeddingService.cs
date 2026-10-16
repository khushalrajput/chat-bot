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

    /// <summary>
    /// Generate embeddings for a list of texts, batching to respect API limits.
    ///
    /// WHY BATCHING?
    /// Gemini embedding API limits batch size (typically ~100 texts per call)
    /// and has per-text token limits. An 82-page PDF produces 50-80+ chunks.
    /// Sending all at once → 400 Bad Request.
    ///
    /// We chunk the embedding requests into groups of BatchSize,
    /// call the API for each batch, and concatenate results.
    /// </summary>
    private const int BatchSize = 10;  // Smaller batches = less chance of 429
    private const int MaxRetries = 3;  // Retry up to 3 times on rate limit

    public async Task<List<float[]>> GetEmbeddingsAsync(
        List<string> texts,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating embeddings for {Count} texts in batches of {Batch}",
            texts.Count, BatchSize);

        var allResults = new List<float[]>();

        for (int i = 0; i < texts.Count; i += BatchSize)
        {
            var batch = texts.Skip(i).Take(BatchSize).ToList();
            var batchNum = (i / BatchSize) + 1;
            var totalBatches = (int)Math.Ceiling((double)texts.Count / BatchSize);

            _logger.LogInformation("Embedding batch {Batch}/{Total} ({Start}-{End})",
                batchNum, totalBatches, i + 1, Math.Min(i + BatchSize, texts.Count));

            // Retry with exponential backoff on 429 (Too Many Requests)
            IList<ReadOnlyMemory<float>>? embeddings = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    embeddings = await _embeddingService.GenerateEmbeddingsAsync(
                        batch, cancellationToken: cancellationToken);
                    break; // Success — exit retry loop
                }
                catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests"))
                {
                    var delay = attempt * 5; // 5s, 10s, 15s
                    _logger.LogWarning(
                        "Rate limited on batch {Batch}. Retry {Attempt}/{Max} in {Delay}s",
                        batchNum, attempt, MaxRetries, delay);

                    if (attempt == MaxRetries) throw;
                    await Task.Delay(delay * 1000, cancellationToken);
                }
            }

            allResults.AddRange(embeddings!.Select(e => e.ToArray()));

            // Pause between batches — Gemini free tier is strict
            if (i + BatchSize < texts.Count)
            {
                await Task.Delay(4000, cancellationToken); // 4s gap between batches
            }
        }

        _logger.LogInformation("Generated {Count} embeddings, dimension={Dim}",
            allResults.Count, allResults[0].Length);

        return allResults;
    }
}
