using ChatBot.Models;

namespace ChatBot.Services;

public interface IDocumentService
{
    /// <summary>
    /// Parse a text document into overlapping chunks.
    /// Returns chunks WITHOUT embeddings — embedding is separate step.
    ///
    /// WHY separate? Single Responsibility. Chunking is text processing.
    /// Embedding is API call. Different concerns, different failure modes.
    /// Chunking never fails. Embedding can (network, rate limits).
    /// </summary>
    List<DocumentChunk> ChunkDocument(string documentName, string content);
}
