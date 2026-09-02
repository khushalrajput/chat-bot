using ChatBot.Models;

namespace ChatBot.Services;

public interface IVectorStoreService
{
    void AddChunks(List<DocumentChunk> chunks);
    List<DocumentChunk> Search(float[] queryEmbedding, int topK = 3);
    int ChunkCount { get; }

    /// <summary>
    /// Load persisted chunks from database into memory cache.
    /// Called once at app startup.
    /// </summary>
    Task InitializeAsync();
}
