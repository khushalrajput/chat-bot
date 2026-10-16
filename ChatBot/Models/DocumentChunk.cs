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

    // --- Metadata: shared across all use cases ---
    // All nullable so branches that don't populate them still work with same DB.

    /// <summary>
    /// Nearest section heading above this chunk.
    /// Use Case 1: "Director's Message", "Library Services"
    /// Use Case 2: lowest-level section heading
    /// Use Case 3: "Environmental Status Report", "Energy Efficiency"
    /// </summary>
    public string? SectionTitle { get; set; }

    /// <summary>
    /// Page number in original PDF (1-based).
    /// All use cases populate this for source citations.
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// Content type: "paragraph", "table", "figure", "list", "spec".
    /// Use Case 1: always "paragraph"
    /// Use Case 2: "paragraph", "table", "list"
    /// Use Case 3: "paragraph", "table", "figure", "spec"
    /// </summary>
    public string ChunkType { get; set; } = "paragraph";

    // --- Metadata: hierarchical documents (Use Case 2) ---

    /// <summary>
    /// Top-level chapter heading (Use Case 2: GEP report).
    /// Example: "Chapter 1: Global Outlook"
    /// Null for non-hierarchical documents (Use Cases 1 and 3).
    /// </summary>
    public string? Chapter { get; set; }

    /// <summary>
    /// Sub-section heading within a section (Use Case 2).
    /// Example: "Commodity Markets" under "Global Outlook"
    /// Null for flat documents.
    /// </summary>
    public string? SubSection { get; set; }

    // --- Metadata: visual documents (Use Case 3) ---

    /// <summary>
    /// Caption or title of a table/figure/chart (Use Case 3).
    /// Example: "Table 1: Carbon footprint by component"
    /// Null for plain text chunks.
    /// </summary>
    public string? Caption { get; set; }

    /// <summary>
    /// The embedding vector — list of floats representing meaning.
    /// Gemini embedding-001 produces 768-dimensional vectors.
    /// Two chunks with similar meaning = similar vectors = close in vector space.
    /// </summary>
    public float[]? Embedding { get; set; }
}
