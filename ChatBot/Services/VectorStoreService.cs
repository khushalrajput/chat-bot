using System.Numerics;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// In-memory vector store with cosine similarity search.
///
/// WHY in-memory first?
/// - Zero infrastructure — learn the concept without database setup
/// - Fast for small datasets (our policy doc = ~20 chunks)
/// - Same interface → swap to Qdrant/Pinecone/pgvector later without changing consumers
///
/// PRODUCTION: Replace with a real vector database when:
/// - Data exceeds memory (>100K chunks)
/// - Need persistence across restarts
/// - Need distributed search
/// - Need filtering (e.g., search only "HR" category docs)
///
/// Popular options: Qdrant (open source), Pinecone (managed), pgvector (PostgreSQL extension)
///
/// REGISTERED AS SINGLETON — one shared store for entire app lifecycle.
/// If registered as scoped/transient, each request gets empty store. Bad.
/// </summary>
public class VectorStoreService : IVectorStoreService
{
    private readonly List<DocumentChunk> _chunks = [];
    private readonly ILogger<VectorStoreService> _logger;
    private readonly object _lock = new(); // Thread safety for concurrent requests

    public VectorStoreService(ILogger<VectorStoreService> logger)
    {
        _logger = logger;
    }

    public int ChunkCount
    {
        get { lock (_lock) return _chunks.Count; }
    }

    public void AddChunks(List<DocumentChunk> chunks)
    {
        lock (_lock)
        {
            _chunks.AddRange(chunks);
        }

        _logger.LogInformation("Added {Count} chunks. Total: {Total}", chunks.Count, ChunkCount);
    }

    public List<DocumentChunk> Search(float[] queryEmbedding, int topK = 3)
    {
        lock (_lock)
        {
            if (_chunks.Count == 0)
                return [];

            // Calculate similarity between query and EVERY stored chunk
            // Then return top K most similar
            //
            // TIME COMPLEXITY: O(n) — scans all chunks.
            // Fine for hundreds/thousands. At millions, need approximate
            // nearest neighbor (ANN) algorithms like HNSW — that's what
            // real vector DBs implement.

            return _chunks
                .Where(c => c.Embedding is not null)
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Score = CosineSimilarity(queryEmbedding, chunk.Embedding!)
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x =>
                {
                    _logger.LogDebug("Match: {Id} (score: {Score:F4})", x.Chunk.Id, x.Score);
                    return x.Chunk;
                })
                .ToList();
        }
    }

    /// <summary>
    /// COSINE SIMILARITY — the heart of vector search.
    ///
    /// Measures the angle between two vectors, not their magnitude.
    /// Formula: cos(θ) = (A · B) / (|A| × |B|)
    ///
    /// Why cosine and not Euclidean distance?
    /// - Cosine ignores vector length, focuses on direction
    /// - "leave policy" and "LEAVE POLICY" might have different magnitudes
    ///   but same direction → cosine treats them as identical
    /// - Range: -1 to 1 (1 = same direction, 0 = orthogonal, -1 = opposite)
    ///
    /// Using SIMD (System.Numerics.Vector) for hardware-accelerated math.
    /// On modern CPUs, processes 8 floats per instruction instead of 1.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimension");

        float dot = 0, normA = 0, normB = 0;

        // SIMD-friendly loop — .NET JIT auto-vectorizes this
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }
}
