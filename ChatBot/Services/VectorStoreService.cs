using ChatBot.Data;
using ChatBot.Data.Entities;
using ChatBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatBot.Services;

/// <summary>
/// SQLite-backed vector store with in-memory cache for fast search.
///
/// PATTERN: Write-through cache
/// - AddChunks: write to SQLite AND memory cache simultaneously
/// - Search: always from memory cache (fast cosine similarity)
/// - Startup: load all from SQLite into memory cache
///
/// WHY not search directly from SQLite?
/// SQLite has no vector similarity functions. We'd have to:
/// 1. Load ALL embeddings from DB
/// 2. Deserialize byte[] → float[]
/// 3. Calculate cosine similarity
/// That's what we do — but cached in memory so step 1-2 happen once at startup.
///
/// REGISTERED AS SINGLETON — one cache for entire app lifetime.
/// Uses IServiceScopeFactory to create scoped DbContext instances.
///
/// WHY IServiceScopeFactory?
/// DbContext is scoped (one per HTTP request). Singleton can't inject scoped directly.
/// IServiceScopeFactory lets singleton create temporary scopes to get DbContext.
/// This is standard EF Core pattern for singletons and background services.
/// </summary>
public class VectorStoreService : IVectorStoreService
{
    private readonly List<DocumentChunk> _cache = [];
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VectorStoreService> _logger;
    private readonly object _lock = new();
    private bool _initialized;

    public VectorStoreService(
        IServiceScopeFactory scopeFactory,
        ILogger<VectorStoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int ChunkCount
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>
    /// Load all chunks from SQLite into memory cache.
    /// Called once at startup. If DB is empty, cache stays empty.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatBotDbContext>();

        var entities = await db.DocumentChunks.ToListAsync();

        lock (_lock)
        {
            foreach (var entity in entities)
            {
                _cache.Add(EntityToChunk(entity));
            }
            _initialized = true;
        }

        _logger.LogInformation("Loaded {Count} chunks from database", entities.Count);
    }

    public void AddChunks(List<DocumentChunk> chunks)
    {
        // Write to SQLite
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatBotDbContext>();

        var entities = chunks.Select(ChunkToEntity).ToList();
        db.DocumentChunks.AddRange(entities);
        db.SaveChanges();

        // Update memory cache
        lock (_lock)
        {
            _cache.AddRange(chunks);
        }

        _logger.LogInformation(
            "Persisted {Count} chunks to DB. Total in cache: {Total}",
            chunks.Count, ChunkCount);
    }

    public List<DocumentChunk> Search(float[] queryEmbedding, int topK = 3)
    {
        lock (_lock)
        {
            if (_cache.Count == 0) return [];

            return _cache
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

    // --- Conversion helpers: Domain ↔ Entity ---

    /// <summary>
    /// float[] → byte[] for SQLite BLOB storage.
    /// Buffer.BlockCopy is fastest way to convert float arrays to bytes.
    /// No serialization overhead — direct memory copy.
    /// </summary>
    private static DocumentChunkEntity ChunkToEntity(DocumentChunk chunk)
    {
        byte[] blob = new byte[chunk.Embedding!.Length * sizeof(float)];
        Buffer.BlockCopy(chunk.Embedding, 0, blob, 0, blob.Length);

        return new DocumentChunkEntity
        {
            ChunkId = chunk.Id,
            DocumentName = chunk.DocumentName,
            Content = chunk.Content,
            ChunkIndex = chunk.ChunkIndex,
            EmbeddingBlob = blob
        };
    }

    /// <summary>
    /// byte[] → float[] for in-memory search.
    /// Reverse of above — direct memory copy, zero parsing.
    /// </summary>
    private static DocumentChunk EntityToChunk(DocumentChunkEntity entity)
    {
        float[] embedding = new float[entity.EmbeddingBlob.Length / sizeof(float)];
        Buffer.BlockCopy(entity.EmbeddingBlob, 0, embedding, 0, entity.EmbeddingBlob.Length);

        return new DocumentChunk
        {
            Id = entity.ChunkId,
            DocumentName = entity.DocumentName,
            Content = entity.Content,
            ChunkIndex = entity.ChunkIndex,
            Embedding = embedding
        };
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimension");

        float dot = 0, normA = 0, normB = 0;

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
