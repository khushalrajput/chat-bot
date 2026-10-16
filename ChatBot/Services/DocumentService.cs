using System.Text.RegularExpressions;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Handles document parsing and chunking for HIERARCHICAL documents.
///
/// USE CASE 2: World Bank Global Economic Prospects (200 pages)
///
/// CHUNKING STRATEGY: Hierarchy-aware with disambiguation.
///
/// The key challenge: this document talks about GDP growth, inflation, and trade
/// for 6 different regions. "Growth is projected at 4.1 percent" means completely
/// different things depending on whether it's in the South Asia section or
/// Sub-Saharan Africa section. The chunker MUST tag each chunk with its full
/// hierarchy path so retrieval can disambiguate.
///
/// Hierarchy: Chapter → Section → SubSection
///   Chapter 2: Regional Outlooks
///     Section: South Asia
///       SubSection: Recent developments
///       SubSection: Outlook
///     Section: Sub-Saharan Africa
///       SubSection: Recent developments
///       SubSection: Outlook
///
/// Special content types:
///   BOX 1.1 — self-contained articles within a chapter
///   TABLE 2.3 — forecast tables, kept as single chunks
///   References — skipped (citation lists, not useful for Q&A)
///   Appendix — skipped (statistical tables, methodology)
///   Front matter — skipped (copyright, TOC, acknowledgments)
/// </summary>
public partial class DocumentService : IDocumentService
{
    private readonly ILogger<DocumentService> _logger;

    // Larger chunks for structured docs — each section has context that helps disambiguation
    private const int MaxChunkSize = 1500;
    private const int OverlapSize = 200;

    public DocumentService(ILogger<DocumentService> logger)
    {
        _logger = logger;
    }

    public List<DocumentChunk> ChunkDocument(string documentName, string content)
    {
        var chunks = new List<DocumentChunk>();

        // Step 1: Split by pages (using [PAGE N] markers from PdfTextExtractor)
        var pageBlocks = SplitByPages(content);

        _logger.LogInformation(
            "Document '{Document}' has {Count} pages",
            documentName, pageBlocks.Count);

        var chunkIndex = 0;

        // Tracking hierarchy state as we walk through pages
        var currentChapter = "";
        var currentSection = "";
        var currentSubSection = "";
        var currentChapterNum = 0;
        var pendingText = "";
        var pendingPageNumber = 1;
        var skipMode = false; // true when in references, appendix, or front matter

        foreach (var (pageNumber, pageText, chapterNum, isFrontMatter, isAppendix) in ParsePages(pageBlocks))
        {
            // Skip front matter, appendix, and statistical sections
            if (isFrontMatter || isAppendix)
            {
                skipMode = true;
                continue;
            }

            // Update chapter tracking
            if (chapterNum > 0 && chapterNum != currentChapterNum)
            {
                // Flush pending text before chapter change
                if (!string.IsNullOrWhiteSpace(pendingText))
                {
                    chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                        currentChapter, currentSection, currentSubSection, pendingPageNumber));
                    pendingText = "";
                }

                currentChapterNum = chapterNum;
                currentChapter = DetectChapterTitle(pageText, chapterNum);
                currentSection = "";
                currentSubSection = "";
                skipMode = false;
            }

            // Skip reference pages within chapters
            if (IsReferencePage(pageText))
            {
                // Flush before skipping
                if (!string.IsNullOrWhiteSpace(pendingText))
                {
                    chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                        currentChapter, currentSection, currentSubSection, pendingPageNumber));
                    pendingText = "";
                }
                continue;
            }

            if (skipMode) continue;

            // Process paragraphs on this page
            var paragraphs = pageText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var para in paragraphs)
            {
                var trimmed = para.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Check for BOX articles: "BOX 1.1 How much will AI affect global growth?"
                var boxMatch = BoxHeadingRegex().Match(trimmed);
                if (boxMatch.Success)
                {
                    // Flush pending before BOX
                    if (!string.IsNullOrWhiteSpace(pendingText))
                    {
                        chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                            currentChapter, currentSection, currentSubSection, pendingPageNumber));
                        pendingText = "";
                    }
                    currentSection = trimmed.Length > 80 ? trimmed[..80] : trimmed;
                    currentSubSection = "";
                    pendingPageNumber = pageNumber;
                    continue;
                }

                // Check for TABLE headings: "TABLE 1.1 Real GDP"
                if (TableHeadingRegex().IsMatch(trimmed))
                {
                    // Flush pending, then capture entire table block as one chunk
                    if (!string.IsNullOrWhiteSpace(pendingText))
                    {
                        chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                            currentChapter, currentSection, currentSubSection, pendingPageNumber,
                            "paragraph"));
                        pendingText = "";
                    }

                    chunks.Add(CreateChunk(documentName, trimmed, chunkIndex++,
                        currentChapter, currentSection, currentSubSection, pageNumber,
                        "table"));
                    continue;
                }

                // Check for region/section headings.
                // PdfPig often concatenates headings with body text:
                //   "South Asia Recent developments The disruptions to..."
                // We check both standalone AND embedded-at-start patterns.
                var detectedSection = DetectSectionHeading(trimmed);
                if (detectedSection == null)
                    detectedSection = DetectEmbeddedSection(trimmed);

                if (detectedSection != null)
                {
                    // Flush pending before section change
                    if (!string.IsNullOrWhiteSpace(pendingText))
                    {
                        chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                            currentChapter, currentSection, currentSubSection, pendingPageNumber));
                        pendingText = "";
                    }
                    currentSection = detectedSection;
                    currentSubSection = "";
                    pendingPageNumber = pageNumber;

                    // Strip the section name from the paragraph and continue processing remainder
                    var remainder = trimmed[detectedSection.Length..].Trim();

                    // Check if remainder starts with a sub-section heading
                    var embeddedSub = DetectSubSectionHeading(remainder);
                    if (embeddedSub != null)
                    {
                        currentSubSection = embeddedSub;
                        remainder = remainder[embeddedSub.Length..].Trim();
                    }

                    // Keep the remaining text as pending
                    if (!string.IsNullOrWhiteSpace(remainder))
                        pendingText = remainder;

                    continue;
                }

                // Check for sub-section headings (Recent developments, Outlook, Risks)
                var detectedSubSection = DetectSubSectionHeading(trimmed);
                if (detectedSubSection != null)
                {
                    // Flush pending before sub-section change
                    if (!string.IsNullOrWhiteSpace(pendingText))
                    {
                        chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                            currentChapter, currentSection, currentSubSection, pendingPageNumber));
                        pendingText = "";
                    }
                    currentSubSection = detectedSubSection;
                    pendingPageNumber = pageNumber;

                    // Handle embedded sub-section: "Recent developments Growth was broadly..."
                    var subRemainder = trimmed[detectedSubSection.Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(subRemainder))
                        pendingText = subRemainder;

                    continue;
                }

                // Accumulate paragraph text
                if (pendingText.Length + trimmed.Length + 2 > MaxChunkSize &&
                    !string.IsNullOrWhiteSpace(pendingText))
                {
                    chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                        currentChapter, currentSection, currentSubSection, pendingPageNumber));

                    pendingText = GetOverlap(pendingText) + "\n\n" + trimmed;
                    pendingPageNumber = pageNumber;
                }
                else
                {
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
            chunks.Add(CreateChunk(documentName, pendingText.Trim(), chunkIndex++,
                currentChapter, currentSection, currentSubSection, pendingPageNumber));
        }

        // Split oversized chunks by sentences
        var finalChunks = new List<DocumentChunk>();
        var finalIndex = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Content.Length > MaxChunkSize * 1.5 && chunk.ChunkType != "table")
            {
                var sentenceChunks = SplitBySentences(chunk.Content, MaxChunkSize, OverlapSize);
                foreach (var sc in sentenceChunks)
                {
                    finalChunks.Add(CreateChunk(
                        documentName, sc.Trim(), finalIndex++,
                        chunk.Chapter, chunk.SectionTitle, chunk.SubSection,
                        chunk.PageNumber, chunk.ChunkType));
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
            finalChunks.Count > 0 ? (int)finalChunks.Average(c => c.Content.Length) : 0);

        return finalChunks;
    }

    /// <summary>
    /// Parse page blocks into enriched tuples with chapter/section metadata.
    /// Uses [CHAPTER N], [FRONTMATTER], [APPENDIX] markers from PdfTextExtractor.
    /// </summary>
    private static IEnumerable<(int PageNumber, string Text, int ChapterNum, bool IsFrontMatter, bool IsAppendix)>
        ParsePages(List<(int PageNumber, string Text)> pages)
    {
        foreach (var (pageNumber, pageText) in pages)
        {
            var chapterNum = 0;
            var isFrontMatter = false;
            var isAppendix = false;
            var cleanText = pageText;

            // Parse and strip markers
            if (cleanText.Contains("[CHAPTER "))
            {
                var match = Regex.Match(cleanText, @"\[CHAPTER (\d+)\]");
                if (match.Success)
                    chapterNum = int.Parse(match.Groups[1].Value);
                cleanText = Regex.Replace(cleanText, @"\[CHAPTER \d+\]\n?", "");
            }

            if (cleanText.Contains("[FRONTMATTER]"))
            {
                isFrontMatter = true;
                cleanText = cleanText.Replace("[FRONTMATTER]\n", "").Replace("[FRONTMATTER]", "");
            }

            if (cleanText.Contains("[APPENDIX]"))
            {
                isAppendix = true;
                cleanText = cleanText.Replace("[APPENDIX]\n", "").Replace("[APPENDIX]", "");
            }

            yield return (pageNumber, cleanText.Trim(), chapterNum, isFrontMatter, isAppendix);
        }
    }

    /// <summary>
    /// Detect chapter title from the first content page of a chapter.
    /// </summary>
    private static string DetectChapterTitle(string pageText, int chapterNum)
    {
        var lines = pageText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(5))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5 || trimmed.Length > 120) continue;

            if (trimmed.Contains("Global Outlook", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Global Outlook";
            if (trimmed.Contains("Regional Outlook", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Regional Outlooks";
            if (trimmed.Contains("Sovereign Debt", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Sovereign Debt";
            if (trimmed.Contains("Fiscal Policy", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Fiscal Policy";
            if (trimmed.Contains("Rising Challenge", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Sovereign Debt";
            if (trimmed.Contains("Navigating Volatility", StringComparison.OrdinalIgnoreCase))
                return $"Chapter {chapterNum}: Fiscal Policy";
        }

        return $"Chapter {chapterNum}";
    }

    /// <summary>
    /// Detect major section headings — primarily region names in Chapter 2.
    ///
    /// Critical for disambiguation: "Growth is projected at 4.1 percent" in
    /// South Asia vs Sub-Saharan Africa are completely different facts.
    /// </summary>
    private static string? DetectSectionHeading(string line)
    {
        if (line.Length > 100 || line.Length < 5) return null;
        if (line.EndsWith('.') || line.EndsWith(',')) return null;

        var knownSections = new[]
        {
            "East Asia and Pacific",
            "Europe and Central Asia",
            "Latin America and the Caribbean",
            "Latin America and  the Caribbean",
            "Middle East, North Africa, Afghanistan, and Pakistan",
            "Middle East, North Africa",
            "South Asia",
            "Sub-Saharan Africa",
            "Global context",
            "Global trade",
            "Commodity markets",
            "Global inflation and financial developments",
            "Major economies",
            "Policy priorities",
            "Summary",
            "Executive Summary",
        };

        foreach (var section in knownSections)
        {
            if (line.Contains(section, StringComparison.OrdinalIgnoreCase))
                return section;
        }

        // Fallback: ALL CAPS line with 3+ words
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3 && line == line.ToUpperInvariant())
            return line;

        return null;
    }

    /// <summary>
    /// Detect section names embedded at the START of a paragraph.
    /// PdfPig often produces: "South Asia Recent developments The disruptions..."
    /// where "South Asia" is the section heading but not on its own line.
    /// </summary>
    private static string? DetectEmbeddedSection(string paragraph)
    {
        var knownSections = new[]
        {
            "East Asia and Pacific",
            "Europe and Central Asia",
            "Latin America and the Caribbean",
            "Latin America and  the Caribbean",
            "Middle East, North Africa, Afghanistan, and Pakistan",
            "Middle East, North Africa",
            "South Asia",
            "Sub-Saharan Africa",
            "Executive Summary",
        };

        foreach (var section in knownSections)
        {
            if (paragraph.StartsWith(section, StringComparison.OrdinalIgnoreCase))
                return section;
        }

        return null;
    }

    /// <summary>
    /// Detect sub-section headings within a region/section.
    /// </summary>
    private static string? DetectSubSectionHeading(string line)
    {
        var subSections = new[]
        {
            "Recent developments",
            "Outlook",
            "Risks",
            "Downside risks",
            "Upside risks",
        };

        foreach (var sub in subSections)
        {
            // Exact match (standalone heading)
            if (line.Trim().Equals(sub, StringComparison.OrdinalIgnoreCase))
                return sub;

            // Embedded at start: "Recent developments Growth was broadly..."
            // Only match if followed by space or end-of-string
            if (line.TrimStart().StartsWith(sub, StringComparison.OrdinalIgnoreCase) &&
                (line.TrimStart().Length == sub.Length ||
                 line.TrimStart()[sub.Length] == ' ' ||
                 line.TrimStart()[sub.Length] == '.'))
                return sub;
        }

        return null;
    }

    /// <summary>
    /// Detect reference/bibliography pages to skip.
    /// </summary>
    private static bool IsReferencePage(string pageText)
    {
        if (pageText.TrimStart().StartsWith("References", StringComparison.OrdinalIgnoreCase))
            return true;
        if (pageText.TrimStart().StartsWith("ANNEX", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // --- Shared utilities ---

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

    private static string GetOverlap(string text)
    {
        if (text.Length <= OverlapSize) return text;
        var overlapRegion = text[^OverlapSize..];
        var sentenceStart = SentenceBoundaryRegex().Match(overlapRegion);
        if (sentenceStart.Success)
            return overlapRegion[(sentenceStart.Index + sentenceStart.Length)..];
        return overlapRegion;
    }

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
        string? chapter, string? sectionTitle, string? subSection,
        int pageNumber, string chunkType = "paragraph")
    {
        return new DocumentChunk
        {
            Id = $"{documentName}::chunk-{index}",
            DocumentName = documentName,
            Content = content,
            ChunkIndex = index,
            Chapter = chapter,
            SectionTitle = sectionTitle,
            SubSection = subSection,
            PageNumber = pageNumber,
            ChunkType = chunkType
        };
    }

    // --- Regex patterns ---

    [GeneratedRegex(@"\[PAGE (\d+)\]", RegexOptions.Multiline)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(@"^BOX\s+\d+\.\d+\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex BoxHeadingRegex();

    [GeneratedRegex(@"^TABLE\s+\d+\.\d+\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex TableHeadingRegex();

    [GeneratedRegex(@"[.!?]\s+(?=[A-Z])")]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceSplitRegex();
}
