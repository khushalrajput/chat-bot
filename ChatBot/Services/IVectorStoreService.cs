using ChatBot.Models;

namespace ChatBot.Services;

public interface IVectorStoreService
{
    /// <summary>
    /// Store chunks with their embeddings.
    /// </summary>
    void AddChunks(List<DocumentChunk> chunks);

    /// <summary>
    /// Find the topK most similar chunks to the query embedding.
    ///
    /// HOW SIMILARITY SEARCH WORKS:
    /// 1. Query text gets embedded → query vector (768 floats)
    /// 2. Compare query vector against ALL stored chunk vectors
    /// 3. Return top K most similar (closest in vector space)
    ///
    /// "Closest" measured by cosine similarity — angle between vectors.
    /// cos=1.0 → identical meaning, cos=0.0 → unrelated.
    /// </summary>
    List<DocumentChunk> Search(float[] queryEmbedding, int topK = 3);

    /// <summary>
    /// Check if any documents are ingested.
    /// </summary>
    int ChunkCount { get; }
}
