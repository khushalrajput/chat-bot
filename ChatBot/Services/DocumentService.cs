using System.Text.RegularExpressions;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Handles document parsing and chunking for NARRATIVE text documents.
///
/// CHUNKING STRATEGY: Paragraph-aware with heading extraction.
///
/// Why paragraph-aware for narrative reports (like GPO Annual Report)?
/// - Narrative text has facts embedded in prose ("GPO distributed 802,834 copies")
/// - Splitting mid-sentence loses the fact or its context
/// - Paragraphs are natural semantic units — each covers one topic/point
/// - Grouping consecutive paragraphs gives enough context for accurate retrieval
///
/// Key differences from the old section-based approach:
/// - OLD: regex for numbered headings (1. Annual Leave) — fails on real reports
/// - NEW: detect heading-like lines (short, no trailing period, title case / ALL CAPS)
/// - OLD: 500-char chunks — too small, splits facts from their context
/// - NEW: ~1200-char chunks — keeps 2-3 paragraphs together
/// - OLD: 50-char overlap — too thin to capture full sentences
/// - NEW: 200-char overlap — captures complete boundary sentences
/// - NEW: page number tracking from [PAGE N] markers
/// - NEW: section title tracking for source citations
/// </summary>
public partial class DocumentService : IDocumentService
{
    private readonly ILogger<DocumentService> _logger;

    // Tunable parameters — in production, move these to configuration
    //
    // WHY 1200 chars?
    // Average English paragraph is ~500-800 chars. 1200 lets us keep
    // 2 related paragraphs together. Important because narrative facts
    // often span across paragraph boundaries:
    //   "GPO completed the transition to govinfo. [new paragraph]
    //    The new system replaced FDsys and was fully deployed in December 2018."
    // At 500 chars, these end up in separate chunks. At 1200, they stay together.
    private const int MaxChunkSize = 1200;

    // WHY 200 chars overlap?
    // Average English sentence is ~75-100 chars. 200 chars guarantees
    // at least 2 full sentences of overlap. This means boundary sentences
    // appear in BOTH chunks, so a question about that sentence always finds it.
    private const int OverlapSize = 200;

    public DocumentService(ILogger<DocumentService> logger)
    {
        _logger = logger;
    }

    public List<DocumentChunk> ChunkDocument(string documentName, string content)
    {
        var chunks = new List<DocumentChunk>();

        // Step 1: Split content into page-aware blocks
        // PdfTextExtractor inserts [PAGE N] markers — we use them to track page numbers
        var pageBlocks = SplitByPages(content);

        _logger.LogInformation(
            "Document '{Document}' has {Count} pages",
            documentName, pageBlocks.Count);

        var chunkIndex = 0;
        var currentSection = "Introduction"; // Default until we find a heading

        // Accumulator: we group paragraphs across pages until hitting MaxChunkSize
        var pendingText = "";
        var pendingPageNumber = 1;

        foreach (var (pageNumber, pageText) in pageBlocks)
        {
            var paragraphs = pageText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var para in paragraphs)
            {
                var trimmed = para.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Check if this paragraph is actually a section heading
                if (IsHeading(trimmed))
                {
                    // Flush any pending text before starting new section
                    if (!string.IsNullOrWhiteSpace(pendingText))
                    {
                        chunks.Add(CreateChunk(
                            documentName, pendingText.Trim(), chunkIndex++,
                            currentSection, pendingPageNumber));
                        pendingText = "";
                    }

                    currentSection = trimmed;
                    pendingPageNumber = pageNumber;
                    continue;
                }

                // Would adding this paragraph exceed the limit?
                if (pendingText.Length + trimmed.Length + 2 > MaxChunkSize &&
                    !string.IsNullOrWhiteSpace(pendingText))
                {
                    // Flush current chunk
                    chunks.Add(CreateChunk(
                        documentName, pendingText.Trim(), chunkIndex++,
                        currentSection, pendingPageNumber));

                    // Start new chunk with overlap from end of previous
                    pendingText = GetOverlap(pendingText) + "\n\n" + trimmed;
                    pendingPageNumber = pageNumber;
                }
                else
                {
                    // Keep accumulating
                    pendingText = string.IsNullOrEmpty(pendingText)
                        ? trimmed
                        : pendingText + "\n\n" + trimmed;

                    if (pendingText.Length == trimmed.Length)
                        pendingPageNumber = pageNumber;
                }
            }
        }

        // Flush final chunk
        if (!string.IsNullOrWhiteSpace(pendingText))
        {
            chunks.Add(CreateChunk(
                documentName, pendingText.Trim(), chunkIndex++,
                currentSection, pendingPageNumber));
        }

        // Handle edge case: single huge paragraph that exceeds MaxChunkSize
        // Split those by sentence boundaries
        var finalChunks = new List<DocumentChunk>();
        var finalIndex = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Content.Length > MaxChunkSize * 1.5)
            {
                // Chunk is way too big — split by sentences
                var sentenceChunks = SplitBySentences(chunk.Content, MaxChunkSize, OverlapSize);
                foreach (var sc in sentenceChunks)
                {
                    finalChunks.Add(CreateChunk(
                        documentName, sc.Trim(), finalIndex++,
                        chunk.SectionTitle, chunk.PageNumber));
                }
            }
            else
            {
                chunk.ChunkIndex = finalIndex++;
                finalChunks.Add(chunk);
            }
        }

        _logger.LogInformation(
            "Document '{Document}' produced {Count} chunks (avg {Avg} chars)",
            documentName, finalChunks.Count,
            finalChunks.Count > 0 ? finalChunks.Average(c => c.Content.Length) : 0);

        return finalChunks;
    }

    /// <summary>
    /// Split text by [PAGE N] markers inserted by PdfTextExtractor.
    /// Returns list of (pageNumber, pageText) tuples.
    /// If no markers found (plain text file), returns entire text as page 1.
    /// </summary>
    private static List<(int PageNumber, string Text)> SplitByPages(string content)
    {
        var pages = new List<(int, string)>();
        var matches = PageMarkerRegex().Matches(content);

        if (matches.Count == 0)
        {
            // No page markers — treat entire content as page 1 (plain text file)
            pages.Add((1, content));
            return pages;
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var pageNum = int.Parse(matches[i].Groups[1].Value);
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var text = content[start..end].Trim();

            if (!string.IsNullOrWhiteSpace(text))
                pages.Add((pageNum, text));
        }

        return pages;
    }

    /// <summary>
    /// Detect whether a line is a section heading rather than body text.
    ///
    /// Heuristics for government/business reports:
    /// 1. Short (under 100 chars) — headings are brief
    /// 2. Doesn't end with a period — body sentences do
    /// 3. Matches one of these patterns:
    ///    - ALL CAPS: "LIBRARY SERVICES AND CONTENT MANAGEMENT"
    ///    - Title Case: "Director's Message"
    ///    - Starts with Roman numeral or number: "III. Financial Summary"
    ///
    /// WHY not just regex for specific patterns?
    /// Government reports don't follow one format. Some use ALL CAPS headings,
    /// some use Title Case, some use numbering. We need to catch all of them.
    /// False positives are OK — worst case we flush a chunk early.
    /// </summary>
    private static bool IsHeading(string line)
    {
        // Too long to be a heading
        if (line.Length > 100) return false;

        // Body text ends with period, question mark, etc.
        if (line.EndsWith('.') || line.EndsWith(',') || line.EndsWith(';'))
            return false;

        // Very short lines that are just numbers or single words — not headings
        if (line.Length < 3) return false;

        // ALL CAPS (at least 3 words to avoid false positives like "GPO" or "FY")
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3 && line == line.ToUpperInvariant())
            return true;

        // Title Case: most words start with uppercase
        // (at least 3 words, >60% capitalized — allows "and", "the", "of")
        if (words.Length >= 2)
        {
            var capitalizedCount = words.Count(w => w.Length > 0 && char.IsUpper(w[0]));
            if (capitalizedCount >= words.Length * 0.6)
                return true;
        }

        // Numbered heading: "1. Something" or "III. Something"
        if (NumberedHeadingRegex().IsMatch(line))
            return true;

        return false;
    }

    /// <summary>
    /// Get overlap text from the end of a chunk.
    /// Aligns to sentence boundaries so overlap always starts at a sentence.
    ///
    /// WHY sentence-aligned overlap?
    /// Raw character overlap: "...nd 802,834 copies of 4,211 titles"
    /// Sentence-aligned:      "GPO distributed 802,834 copies of 4,211 titles."
    /// The second version is a complete, searchable thought.
    /// </summary>
    private static string GetOverlap(string text)
    {
        if (text.Length <= OverlapSize)
            return text;

        var overlapRegion = text[^OverlapSize..];

        // Find the first sentence boundary in the overlap region
        var sentenceStart = SentenceBoundaryRegex().Match(overlapRegion);
        if (sentenceStart.Success)
        {
            return overlapRegion[(sentenceStart.Index + sentenceStart.Length)..];
        }

        // No sentence boundary found — use the raw overlap
        return overlapRegion;
    }

    /// <summary>
    /// Split a large text block by sentence boundaries.
    /// Used as fallback when a single paragraph exceeds MaxChunkSize.
    /// </summary>
    private static List<string> SplitBySentences(string text, int maxSize, int overlap)
    {
        var chunks = new List<string>();
        var sentences = SentenceSplitRegex().Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var current = "";
        foreach (var sentence in sentences)
        {
            if (current.Length + sentence.Length > maxSize && current.Length > 0)
            {
                chunks.Add(current.Trim());

                // Overlap: keep last ~overlap chars worth of sentences
                var overlapText = current.Length > overlap ? current[^overlap..] : current;
                current = overlapText + " " + sentence;
            }
            else
            {
                current = string.IsNullOrEmpty(current) ? sentence : current + " " + sentence;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
            chunks.Add(current.Trim());

        return chunks;
    }

    private static DocumentChunk CreateChunk(
        string documentName, string content, int index,
        string? sectionTitle, int pageNumber)
    {
        return new DocumentChunk
        {
            Id = $"{documentName}::chunk-{index}",
            DocumentName = documentName,
            Content = content,
            ChunkIndex = index,
            SectionTitle = sectionTitle,
            PageNumber = pageNumber,
            ChunkType = "paragraph"
        };
    }

    // --- Regex patterns ---

    /// <summary>Matches [PAGE 42] markers inserted by PdfTextExtractor.</summary>
    [GeneratedRegex(@"\[PAGE (\d+)\]", RegexOptions.Multiline)]
    private static partial Regex PageMarkerRegex();

    /// <summary>Matches numbered headings: "1. Something" or "III. Something"</summary>
    [GeneratedRegex(@"^(?:\d+|[IVXLC]+)\.\s+\S", RegexOptions.Multiline)]
    private static partial Regex NumberedHeadingRegex();

    /// <summary>
    /// Matches sentence boundaries: period/question/exclamation followed by
    /// whitespace and an uppercase letter.
    /// Used to find clean sentence starts in overlap regions.
    /// </summary>
    [GeneratedRegex(@"[.!?]\s+(?=[A-Z])")]
    private static partial Regex SentenceBoundaryRegex();

    /// <summary>
    /// Splits text at sentence boundaries.
    /// Keeps the delimiter (period + space) attached to the preceding sentence.
    /// </summary>
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceSplitRegex();
}
