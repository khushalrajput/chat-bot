namespace ChatBot.Data.Entities;

/// <summary>
/// EF Core entity for storing document chunks in SQLite.
///
/// WHY separate entity from domain model (DocumentChunk)?
/// - Domain model has float[] Embedding (for in-memory cosine math)
/// - Entity has byte[] EmbeddingBlob (for SQLite BLOB storage)
/// - Domain model might evolve differently from DB schema
/// - EF Core entities often need navigation properties, DB conventions
///
/// This is the "persistence model" vs "domain model" separation.
/// In CQRS terms: this is the write/read model for storage.
/// </summary>
public class DocumentChunkEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Logical ID like "leave-policy.txt::chunk-4"
    /// </summary>
    public required string ChunkId { get; set; }

    public required string DocumentName { get; set; }

    public required string Content { get; set; }

    public int ChunkIndex { get; set; }

    /// <summary>
    /// Embedding stored as byte[] (BLOB in SQLite).
    ///
    /// WHY bytes not float[]?
    /// - SQLite has no float array type
    /// - BLOB is most efficient storage for raw binary data
    /// - Convert: float[] → byte[] for storage, byte[] → float[] for search
    /// - 3072 floats × 4 bytes = 12,288 bytes per chunk (~12KB)
    /// - 1000 chunks = ~12MB. Manageable.
    /// </summary>
    public required byte[] EmbeddingBlob { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
