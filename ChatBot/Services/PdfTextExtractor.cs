using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace ChatBot.Services;

/// <summary>
/// Extracts text from PDF files page-by-page using PdfPig.
///
/// USE CASE 2 ENHANCEMENTS:
/// - Parses chapter headers like "CHAPTER 1 GLOBAL ECONOMIC PROSPECTS | JUNE 2026 3"
///   to extract chapter number as metadata and strip from body text
/// - Detects front matter (pages before first chapter) and marks with [FRONTMATTER]
/// - Detects appendix/reference sections at the end
///
/// The [PAGE N] and [CHAPTER N] markers let the chunker build hierarchy.
/// </summary>
public partial class PdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract text from a PDF stream, returning page-delimited text.
    /// Each page's text is preceded by [PAGE N] and optionally [CHAPTER N] markers.
    /// </summary>
    public string ExtractText(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var pageTexts = new List<string>();

        _logger.LogInformation("PDF has {PageCount} pages", document.NumberOfPages);

        // First pass: collect all page texts to detect repeated headers/footers
        var rawPages = new List<string>();
        foreach (var page in document.GetPages())
        {
            rawPages.Add(page.Text);
        }

        // Detect repeated header/footer lines (appear on >50% of pages)
        var linesToStrip = DetectRepeatedLines(rawPages);
        if (linesToStrip.Count > 0)
        {
            _logger.LogInformation(
                "Detected {Count} repeated header/footer lines to strip",
                linesToStrip.Count);
        }

        // Find where chapters start (for front matter detection)
        int firstChapterPage = -1;

        // Second pass: clean, extract chapter info, and format each page
        for (int i = 0; i < rawPages.Count; i++)
        {
            var rawText = rawPages[i];

            // Try to extract chapter number from page header
            // Pattern: "CHAPTER N GLOBAL ECONOMIC PROSPECTS | JUNE 2026 <pagenum>"
            // or standalone: "CHAPTER 1GLOBAL OUTLOOK"
            var chapterNum = ExtractChapterNumber(rawText);

            var pageText = CleanPageText(rawText, linesToStrip);

            if (string.IsNullOrWhiteSpace(pageText))
                continue;

            // Track first chapter page for front matter detection
            if (chapterNum > 0 && firstChapterPage < 0)
                firstChapterPage = i;

            // Build page marker with chapter info
            var markers = $"[PAGE {i + 1}]";
            if (chapterNum > 0)
                markers += $"\n[CHAPTER {chapterNum}]";

            // Mark statistical appendix / selected topics
            if (pageText.StartsWith("STATISTICAL APPENDIX", StringComparison.OrdinalIgnoreCase))
                markers += "\n[APPENDIX]";
            if (pageText.StartsWith("SELECTED TOPICS", StringComparison.OrdinalIgnoreCase))
                markers += "\n[APPENDIX]";

            // Mark front matter (before first chapter) — but keep Executive Summary
            if (firstChapterPage < 0 &&
                !pageText.Contains("Executive Summary", StringComparison.OrdinalIgnoreCase) &&
                !pageText.Contains("Foreword", StringComparison.OrdinalIgnoreCase))
                markers += "\n[FRONTMATTER]";

            pageTexts.Add($"{markers}\n{pageText}");
        }

        var result = string.Join("\n\n", pageTexts);

        _logger.LogInformation(
            "Extracted {CharCount} chars from {PageCount} pages (first chapter at page {FirstChapter})",
            result.Length, document.NumberOfPages,
            firstChapterPage >= 0 ? firstChapterPage + 1 : -1);

        return result;
    }

    /// <summary>
    /// Extract chapter number from page header text.
    ///
    /// GEP format: "CHAPTER 1 GLOBAL ECONOMIC PROSPECTS | JUNE 2026 3"
    /// Also catches: "CHAPTER 1GLOBAL OUTLOOK" (title pages with no space)
    ///
    /// Returns 0 if no chapter header found.
    /// </summary>
    private static int ExtractChapterNumber(string pageText)
    {
        var match = ChapterHeaderRegex().Match(pageText);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
            return num;
        return 0;
    }

    /// <summary>
    /// Find lines that appear on more than half the pages.
    /// These are almost always headers, footers, or page numbers.
    /// </summary>
    private static HashSet<string> DetectRepeatedLines(List<string> pages)
    {
        var lineCounts = new Dictionary<string, int>();
        var threshold = pages.Count / 2;

        foreach (var page in pages)
        {
            var uniqueLines = page
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Length < 100)
                .ToHashSet();

            foreach (var line in uniqueLines)
            {
                lineCounts[line] = lineCounts.GetValueOrDefault(line) + 1;
            }
        }

        return lineCounts
            .Where(kv => kv.Value > threshold)
            .Select(kv => kv.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Clean a single page's text:
    /// - Remove chapter header lines (extracted as metadata above)
    /// - Remove repeated header/footer lines
    /// - Strip page numbers
    /// - Collapse excessive whitespace
    /// </summary>
    private static string CleanPageText(string pageText, HashSet<string> linesToStrip)
    {
        var lines = pageText.Split('\n');
        var cleanedLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (linesToStrip.Contains(trimmed))
                continue;

            if (IsPageNumber(trimmed))
                continue;

            // Strip chapter header PREFIX from body text (already extracted as [CHAPTER N])
            // Pattern: "CHAPTER N GLOBAL ECONOMIC PROSPECTS | JUNE 2026 <pagenum>"
            // IMPORTANT: Don't skip the whole line! PdfPig often puts entire page on one line.
            // Strip only the header prefix and keep the rest.
            if (ChapterPageHeaderRegex().IsMatch(trimmed))
            {
                // Remove header prefix up to and including the page number after "|"
                trimmed = ChapterFullHeaderRegex().Replace(trimmed, "").Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
            }

            // Strip standalone roman numeral page markers (vii, xv, xxiii, etc.)
            // Only strip if the ENTIRE trimmed line is a roman numeral (standalone page number)
            if (trimmed.Length <= 10 && RomanNumeralRegex().IsMatch(trimmed))
                continue;

            cleanedLines.Add(trimmed);
        }

        var result = string.Join("\n", cleanedLines);
        while (result.Contains("\n\n\n"))
            result = result.Replace("\n\n\n", "\n\n");

        return result.Trim();
    }

    private static bool IsPageNumber(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        if (int.TryParse(line.Trim('-', ' '), out _))
            return true;

        if (line.StartsWith("Page ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(line[5..].Trim(), out _))
            return true;

        return false;
    }

    // --- Regex patterns ---

    /// <summary>Matches "CHAPTER 1" or "CHAPTER 4" at start of text.</summary>
    [GeneratedRegex(@"CHAPTER\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterHeaderRegex();

    /// <summary>
    /// Detects if a line starts with a chapter page header.
    /// "CHAPTER 1 GLOBAL ECONOMIC PROSPECTS | JUNE 2026 3"
    /// </summary>
    [GeneratedRegex(@"^CHAPTER\s+\d+\s+GLOBAL ECONOMIC PROSPECTS\s*\|", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPageHeaderRegex();

    /// <summary>
    /// Captures the FULL chapter header including page number, for stripping.
    /// "CHAPTER 1 GLOBAL ECONOMIC PROSPECTS | JUNE 2026 3" → strip all of this,
    /// keep everything after it (the actual page content).
    /// </summary>
    [GeneratedRegex(@"CHAPTER\s+\d+\s+GLOBAL ECONOMIC PROSPECTS\s*\|\s*JUNE\s+\d+\s*\d*", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterFullHeaderRegex();

    /// <summary>Matches standalone roman numeral lines: "vii", "xv", "xxiii"</summary>
    [GeneratedRegex(@"^[ivxlc]+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RomanNumeralRegex();
}
