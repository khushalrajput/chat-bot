namespace ChatBot.Models;

/// <summary>
/// A chunk of a document with its embedding vector.
/// This is our core domain model for RAG — the unit of retrieval.
///
/// Think of it: document = book, chunk = relevant paragraph.
/// We search at chunk level, not document level.
/// </summary>
public class DocumentChunk
{
    public required string Id { get; set; }

    /// <summary>
    /// Which document this chunk came from.
    /// Important for: showing source attribution, filtering by document, re-ingesting.
    /// </summary>
    public required string DocumentName { get; set; }

    /// <summary>
    /// The actual text content of this chunk.
    /// Sent to LLM as context when this chunk is retrieved.
    /// </summary>
    public required string Content { get; set; }

    /// <summary>
    /// Position in original document (0-based).
    /// Useful for: ordering results, showing context around a chunk.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// The embedding vector — list of floats representing meaning.
    /// Gemini embedding-001 produces 768-dimensional vectors.
    /// Two chunks with similar meaning = similar vectors = close in vector space.
    /// </summary>
    public float[]? Embedding { get; set; }
}
