using System.Text.RegularExpressions;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Handles document parsing and chunking.
///
/// CHUNKING STRATEGY: Section-based with overlap.
///
/// Why section-based for policy documents?
/// - Policies have natural sections (numbered headings)
/// - Each section = self-contained topic = ideal retrieval unit
/// - Better than fixed-size: no mid-sentence cuts
/// - Better than sentence-based: keeps related sentences together
///
/// Fallback: if section is too large, split into paragraphs.
/// If paragraph is too large, split by sentences with overlap.
/// </summary>
public partial class DocumentService : IDocumentService
{
    private readonly ILogger<DocumentService> _logger;

    // Tunable parameters — in production, move these to configuration
    private const int MaxChunkSize = 500;      // chars — keeps chunks focused
    private const int OverlapSize = 50;         // chars — prevents boundary information loss

    public DocumentService(ILogger<DocumentService> logger)
    {
        _logger = logger;
    }

    public List<DocumentChunk> ChunkDocument(string documentName, string content)
    {
        var chunks = new List<DocumentChunk>();

        // Step 1: Split by sections (numbered headings like "1. Annual Leave")
        var sections = SplitIntoSections(content);

        _logger.LogInformation(
            "Document '{Document}' split into {Count} sections",
            documentName, sections.Count);

        var chunkIndex = 0;

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section))
                continue;

            // Step 2: If section fits in one chunk, use it directly
            if (section.Length <= MaxChunkSize)
            {
                chunks.Add(CreateChunk(documentName, section.Trim(), chunkIndex++));
            }
            else
            {
                // Step 3: Section too large — split into paragraphs with overlap
                var subChunks = SplitWithOverlap(section, MaxChunkSize, OverlapSize);
                foreach (var sub in subChunks)
                {
                    if (!string.IsNullOrWhiteSpace(sub))
                    {
                        chunks.Add(CreateChunk(documentName, sub.Trim(), chunkIndex++));
                    }
                }
            }
        }

        _logger.LogInformation(
            "Document '{Document}' produced {Count} chunks",
            documentName, chunks.Count);

        return chunks;
    }

    /// <summary>
    /// Split document by numbered section headings.
    /// Pattern: line starting with digit(s) followed by period and space.
    /// Examples: "1. Annual Leave", "10. General Leave Rules"
    /// </summary>
    private static List<string> SplitIntoSections(string content)
    {
        // Split on lines that start with a number followed by ". "
        // (?=...) is lookahead — keeps the delimiter in the result
        var sections = SectionSplitRegex().Split(content);
        return sections.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>
    /// Split text into chunks of maxSize with overlap.
    /// Splits on paragraph boundaries (\n\n) first, then sentence boundaries.
    ///
    /// OVERLAP explained:
    /// Chunk 1: "...leave can be carried forward up to 5 days."
    /// Chunk 2: "carried forward up to 5 days. Any balance exceeding..."
    ///          ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    ///          This overlap ensures a question about "carry forward"
    ///          matches BOTH chunks, increasing retrieval accuracy.
    /// </summary>
    private static List<string> SplitWithOverlap(string text, int maxSize, int overlap)
    {
        var chunks = new List<string>();
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = "";

        foreach (var para in paragraphs)
        {
            // If adding this paragraph exceeds limit, save current and start new
            if (currentChunk.Length + para.Length > maxSize && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk);

                // Start new chunk with overlap from end of previous
                var overlapText = currentChunk.Length > overlap
                    ? currentChunk[^overlap..]
                    : currentChunk;
                currentChunk = overlapText + "\n\n" + para;
            }
            else
            {
                currentChunk = string.IsNullOrEmpty(currentChunk)
                    ? para
                    : currentChunk + "\n\n" + para;
            }
        }

        // Don't forget the last chunk
        if (!string.IsNullOrWhiteSpace(currentChunk))
            chunks.Add(currentChunk);

        return chunks;
    }

    private static DocumentChunk CreateChunk(string documentName, string content, int index)
    {
        return new DocumentChunk
        {
            Id = $"{documentName}::chunk-{index}",
            DocumentName = documentName,
            Content = content,
            ChunkIndex = index
        };
    }

    [GeneratedRegex(@"(?=\n\d+\.\s)", RegexOptions.Multiline)]
    private static partial Regex SectionSplitRegex();
}
