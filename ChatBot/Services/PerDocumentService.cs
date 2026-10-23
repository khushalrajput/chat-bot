using System.Text.RegularExpressions;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Chunking strategy for vision-extracted product environmental reports (Use Case 3: iMac PER).
///
/// Input: text produced by GeminiPdfExtractor — [PAGE N]-delimited with custom markers.
///
/// DOCUMENT STRUCTURE (iMac PER):
///   Cover → Progress toward carbon neutral → Lifecycle diagram
///   → Design and Source → Make → Package and Ship → Use → Recover
///   → Definitions → Carbon Footprint → Endnotes
///
/// MARKERS from GeminiPdfExtractor:
///   [SECTION: X]   — lifecycle stage boundary
///   [DEFINITIONS]  — glossary page: split per term
///   [ENDNOTES]     — skip entirely
///
/// METADATA MAPPING:
///   Chapter      → lifecycle stage (Design and Source, Make, etc.)
///   SectionTitle → sub-heading within stage (Smarter chemistry, Apple Trade In, etc.)
///   SubSection   → null (no 3rd level)
///   Caption      → null (table captions embedded in content)
///   ChunkType    → "paragraph" | "table" | "glossary"
///
/// WHY SMALLER CHUNKS (800) vs GEP (1500)?
/// Each lifecycle section in the iMac PER is 200-400 words of dense facts.
/// Smaller chunks keep stat callouts, material cards, and prose separate
/// so retrieval returns precise targeted content, not a whole section.
/// </summary>
public partial class PerDocumentService
{
    private readonly ILogger<PerDocumentService> _logger;

    private const int MaxChunkSize = 800;
    private const int OverlapSize = 100;

    public PerDocumentService(ILogger<PerDocumentService> logger)
    {
        _logger = logger;
    }

    public List<DocumentChunk> ChunkDocument(string documentName, string content)
    {
        var chunks = new List<DocumentChunk>();
        var pageBlocks = SplitByPages(content);

        _logger.LogInformation(
            "PER document '{Doc}' has {Count} pages after extraction",
            documentName, pageBlocks.Count);

        var chunkIndex = 0;
        var currentSection = "";     // lifecycle stage → Chapter field
        var currentSubSection = "";  // sub-heading → SectionTitle field
        var pendingText = "";
        var pendingPage = 1;
        var skipMode = false;
        var definitionsMode = false;

        foreach (var (pageNumber, rawPageText) in pageBlocks)
        {
            var pageText = rawPageText;

            // [ENDNOTES] — skip remainder of document
            if (pageText.Contains("[ENDNOTES]"))
            {
                Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                    currentSection, currentSubSection, pendingPage);
                skipMode = true;
                continue;
            }

            if (skipMode) continue;

            // [DEFINITIONS] — switch to glossary parsing mode
            if (pageText.Contains("[DEFINITIONS]"))
            {
                Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                    currentSection, currentSubSection, pendingPage);
                definitionsMode = true;
                currentSection = "Definitions";
                currentSubSection = "";
                pageText = pageText.Replace("[DEFINITIONS]", "").Trim();
            }

            // [SECTION: X] — new lifecycle stage
            var sectionMatch = SectionMarkerRegex().Match(pageText);
            if (sectionMatch.Success)
            {
                var newSection = sectionMatch.Groups[1].Value.Trim();
                if (!newSection.Equals(currentSection, StringComparison.OrdinalIgnoreCase))
                {
                    Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                        currentSection, currentSubSection, pendingPage);
                    currentSection = newSection;
                    currentSubSection = "";

                    // "Endnotes" section reached without [ENDNOTES] marker — skip
                    if (currentSection.Equals("Endnotes", StringComparison.OrdinalIgnoreCase))
                    {
                        skipMode = true;
                        continue;
                    }

                    definitionsMode = currentSection.Equals("Definitions", StringComparison.OrdinalIgnoreCase);
                }
                pageText = SectionMarkerRegex().Replace(pageText, "").Trim();
            }

            if (definitionsMode)
            {
                // Glossary: parse each "Term: definition" as its own chunk
                var glossaryChunks = ParseGlossaryPage(documentName, pageText, pageNumber, chunkIndex);
                chunks.AddRange(glossaryChunks);
                chunkIndex += glossaryChunks.Count;
                continue;
            }

            // --- Regular content processing ---
            var paragraphs = pageText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var para in paragraphs)
            {
                var trimmed = para.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Table block: structured data with multiple columns/rows
                if (IsTableBlock(trimmed))
                {
                    Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                        currentSection, currentSubSection, pendingPage);
                    chunks.Add(CreateChunk(documentName, trimmed, chunkIndex++,
                        currentSection, currentSubSection, pageNumber, "table"));
                    continue;
                }

                // Sub-heading detection (Smarter chemistry, Apple Trade In, etc.)
                var subHeading = DetectSubHeading(trimmed);
                if (subHeading != null)
                {
                    Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                        currentSection, currentSubSection, pendingPage);
                    currentSubSection = subHeading;
                    pendingPage = pageNumber;

                    // Handle embedded: "Smarter chemistry We reduce emissions by..."
                    var remainder = trimmed[subHeading.Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        pendingText = remainder;
                        pendingPage = pageNumber;
                    }
                    continue;
                }

                // Accumulate paragraph
                if (pendingText.Length + trimmed.Length + 2 > MaxChunkSize
                    && !string.IsNullOrWhiteSpace(pendingText))
                {
                    Flush(ref pendingText, chunks, documentName, ref chunkIndex,
                        currentSection, currentSubSection, pendingPage);
                    pendingText = trimmed;
                    pendingPage = pageNumber;
                }
                else
                {
                    if (string.IsNullOrEmpty(pendingText))
                    {
                        pendingText = trimmed;
                        pendingPage = pageNumber;
                    }
                    else
                    {
                        pendingText += "\n\n" + trimmed;
                    }
                }
            }
        }

        // Flush any remaining text
        Flush(ref pendingText, chunks, documentName, ref chunkIndex,
            currentSection, currentSubSection, pendingPage);

        // Re-index
        for (int i = 0; i < chunks.Count; i++)
            chunks[i].ChunkIndex = i;

        _logger.LogInformation(
            "PER document '{Doc}' produced {Count} chunks (avg {Avg} chars)",
            documentName, chunks.Count,
            chunks.Count > 0 ? (int)chunks.Average(c => c.Content.Length) : 0);

        return chunks;
    }

    // --- Helpers ---

    private static void Flush(
        ref string pendingText,
        List<DocumentChunk> chunks,
        string documentName,
        ref int chunkIndex,
        string section,
        string subSection,
        int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(pendingText)) return;
        chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
            section, subSection, pageNumber));
        pendingText = "";
    }

    /// <summary>
    /// Parse a glossary page into one chunk per term definition.
    /// Gemini extracts definitions as "Term: definition text" or "Term\ndefinition".
    /// </summary>
    private static List<DocumentChunk> ParseGlossaryPage(
        string documentName, string text, int pageNumber, int startIndex)
    {
        var entries = new List<DocumentChunk>();

        // Glossary entries start with: "Bio-based plastics:" or "Carbon footprint:"
        // Pattern: line starting with capitalized words followed by colon
        var matches = GlossaryTermRegex().Matches(text);

        if (matches.Count == 0)
        {
            // Fallback: treat whole page as one glossary chunk
            if (!string.IsNullOrWhiteSpace(text))
                entries.Add(CreateChunk(documentName, text.Trim(), startIndex,
                    "Definitions", null, pageNumber, "glossary"));
            return entries;
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var entryStart = matches[i].Index;
            var entryEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var entry = text[entryStart..entryEnd].Trim();
            if (!string.IsNullOrWhiteSpace(entry))
                entries.Add(CreateChunk(documentName, entry, startIndex + i,
                    "Definitions", null, pageNumber, "glossary"));
        }

        return entries;
    }

    /// <summary>
    /// Detect structured data blocks (tables extracted from PDF by Gemini).
    /// Table pattern: 3+ lines containing | separators or bullet+units (%, kg, kWh).
    /// </summary>
    private static bool IsTableBlock(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3) return false;

        var structuredLines = lines.Count(l =>
            l.Contains('|') ||
            l.Contains('\t') ||
            (l.TrimStart().StartsWith("•") &&
             (l.Contains('%') || l.Contains("kg") || l.Contains("kWh") || l.Contains("W"))));

        return structuredLines >= 3;
    }

    /// <summary>
    /// Detect known sub-section headings within lifecycle stages.
    /// Checks both standalone (exact match) and embedded at paragraph start.
    /// </summary>
    private static string? DetectSubHeading(string text)
    {
        // Order matters: longer/more specific first to avoid partial matches
        var knownSubHeadings = new[]
        {
            "Energy consumption of ENERGY STAR-rated products",
            "Zero Waste to Landfill",
            "Supplier energy use",
            "Supplier Code of Conduct",
            "Made with smarter chemistry",
            "Designed to last",
            "Smarter chemistry",
            "Apple Trade In",
            "Longevity",
            "Packaging",
        };

        foreach (var heading in knownSubHeadings)
        {
            if (!text.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
                continue;

            // Confirm it's standalone or followed by whitespace (not a coincidental prefix)
            if (text.Length == heading.Length ||
                text[heading.Length] == ' ' ||
                text[heading.Length] == '\n')
                return heading;
        }

        return null;
    }

    private static List<(int PageNumber, string Text)> SplitByPages(string content)
    {
        var pages = new List<(int, string)>();
        var matches = PageMarkerRegex().Matches(content);

        if (matches.Count == 0)
        {
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

    private static DocumentChunk CreateChunk(
        string documentName, string content, int index,
        string? chapter, string? sectionTitle, int pageNumber,
        string chunkType = "paragraph")
    {
        return new DocumentChunk
        {
            Id = $"{documentName}::chunk-{index}",
            DocumentName = documentName,
            Content = content,
            ChunkIndex = index,
            Chapter = chapter,
            SectionTitle = sectionTitle,
            SubSection = null,
            PageNumber = pageNumber,
            ChunkType = chunkType
        };
    }

    // --- Regex patterns ---

    [GeneratedRegex(@"\[PAGE (\d+)\]", RegexOptions.Multiline)]
    private static partial Regex PageMarkerRegex();

    /// <summary>Matches [SECTION: Design and Source] — captures section name.</summary>
    [GeneratedRegex(@"\[SECTION:\s*([^\]]+)\]")]
    private static partial Regex SectionMarkerRegex();

    /// <summary>
    /// Matches start of a glossary term: line beginning with capitalized word(s) then colon.
    /// "Bio-based plastics:" "Carbon footprint:" "Supplier Clean Energy Program:"
    /// </summary>
    [GeneratedRegex(@"(?m)^[A-Z][A-Za-z\s\-]+:")]
    private static partial Regex GlossaryTermRegex();
}
