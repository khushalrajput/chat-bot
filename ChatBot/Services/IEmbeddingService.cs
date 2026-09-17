namespace ChatBot.Services;

/// <summary>
/// Embedding service — same interface as before.
/// Implementation now uses SK instead of raw HTTP.
/// Consumers (DocumentController, ChatController) don't change.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<List<float[]>> GetEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken = default);
}
