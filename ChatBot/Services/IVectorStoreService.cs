using ChatBot.Models;

namespace ChatBot.Services;

public interface IVectorStoreService
{
    void AddChunks(List<DocumentChunk> chunks);

    /// <summary>
    /// Search for chunks most similar to the query embedding.
    ///
    /// WHY topK=5 (was 3)?
    /// With 82 pages of content, the right answer might rank 4th.
    /// 3 was too aggressive — missed relevant chunks.
    ///
    /// WHY minScore=0.3?
    /// Without a threshold, we always return topK chunks even if they're irrelevant.
    /// Irrelevant chunks pollute the LLM context and cause hallucination.
    /// 0.3 is conservative — filters obvious noise while keeping fuzzy matches.
    /// Tune this based on your embedding model and document type.
    /// </summary>
    List<DocumentChunk> Search(float[] queryEmbedding, int topK = 5, float minScore = 0.3f);

    int ChunkCount { get; }

    /// <summary>
    /// Load persisted chunks from database into memory cache.
    /// Called once at app startup.
    /// </summary>
    Task InitializeAsync();
}
