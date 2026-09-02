namespace ChatBot.Services;

public interface IEmbeddingService
{
    /// <summary>
    /// Generate embedding vector for a single text (user query).
    /// </summary>
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate embeddings for multiple texts in one batch call (document chunks).
    /// PRODUCTION: always batch when possible — fewer API calls = faster + cheaper.
    /// </summary>
    Task<List<float[]>> GetEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken = default);
}
