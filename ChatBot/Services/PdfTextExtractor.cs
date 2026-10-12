using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ChatBot.Services;

/// <summary>
/// Extracts text from PDF files page-by-page using PdfPig.
///
/// WHY PdfPig?
/// - Pure C# — no native dependencies (poppler, tesseract, etc.)
/// - Extracts text with word positioning, so we can detect paragraphs
/// - Lightweight: ~2MB, works on all platforms
///
/// WHAT THIS DOES:
/// - Reads each page and extracts text
/// - Embeds [PAGE N] markers so the chunker knows page boundaries
/// - Strips repeated headers/footers (common in government reports)
/// - Preserves paragraph structure (double newlines between paragraphs)
///
/// WHAT THIS DOES NOT DO:
/// - OCR (scanned PDFs won't work — PdfPig reads text layers only)
/// - Table extraction (Use Case 3 will need that)
/// - Image/chart understanding (also Use Case 3)
/// </summary>
public class PdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract text from a PDF stream, returning page-delimited text.
    /// Each page's text is preceded by a [PAGE N] marker.
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

        // Second pass: clean and format each page
        for (int i = 0; i < rawPages.Count; i++)
        {
            var pageText = CleanPageText(rawPages[i], linesToStrip);

            if (string.IsNullOrWhiteSpace(pageText))
                continue;

            pageTexts.Add($"[PAGE {i + 1}]\n{pageText}");
        }

        var result = string.Join("\n\n", pageTexts);

        _logger.LogInformation(
            "Extracted {CharCount} chars from {PageCount} pages",
            result.Length, document.NumberOfPages);

        return result;
    }

    /// <summary>
    /// Find lines that appear on more than half the pages.
    /// These are almost always headers, footers, or page numbers.
    ///
    /// Example: "U.S. Government Publishing Office" on every page header.
    /// We don't want this repeated 82 times in our chunks.
    /// </summary>
    private static HashSet<string> DetectRepeatedLines(List<string> pages)
    {
        var lineCounts = new Dictionary<string, int>();
        var threshold = pages.Count / 2;

        foreach (var page in pages)
        {
            // Use a set so we count each line once per page, not once per occurrence
            var uniqueLines = page
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Length < 100) // Headers/footers are short
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
    /// - Remove repeated header/footer lines
    /// - Collapse excessive whitespace
    /// - Preserve paragraph boundaries (double newlines)
    /// </summary>
    private static string CleanPageText(string pageText, HashSet<string> linesToStrip)
    {
        var lines = pageText.Split('\n');
        var cleanedLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip lines identified as repeated headers/footers
            if (linesToStrip.Contains(trimmed))
                continue;

            // Skip standalone page numbers (just digits, maybe with dashes)
            if (IsPageNumber(trimmed))
                continue;

            cleanedLines.Add(trimmed);
        }

        // Rejoin and normalize whitespace: collapse 3+ newlines into 2
        var result = string.Join("\n", cleanedLines);
        while (result.Contains("\n\n\n"))
            result = result.Replace("\n\n\n", "\n\n");

        return result.Trim();
    }

    /// <summary>
    /// Detect standalone page numbers: "42", "- 42 -", "Page 42", etc.
    /// </summary>
    private static bool IsPageNumber(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        // Pure digits
        if (int.TryParse(line.Trim('-', ' '), out _))
            return true;

        // "Page 42" pattern
        if (line.StartsWith("Page ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(line[5..].Trim(), out _))
            return true;

        return false;
    }
}
