using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatBot.Settings;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

/// <summary>
/// Extracts text AND visual content from PDFs using the Gemini Files API.
///
/// USE CASE 3: iMac PER — vision-heavy PDF with charts, callout boxes, diagrams.
///
/// WHY NOT PdfPig?
/// PdfPig extracts text glyphs only. Charts, stat callouts, and diagrams
/// in the iMac PER contain critical data (percentages, CO2 figures, power specs)
/// that only appear as visual elements — no underlying text layer.
///
/// HOW IT WORKS:
/// 1. Upload PDF to Gemini Files API (free, 48h retention, up to 20GB)
/// 2. Send GenerateContent referencing the uploaded file
/// 3. Prompt instructs Gemini to extract text + describe all visuals with numbers
/// 4. Returns [PAGE N]-delimited text compatible with existing chunking pipeline
///    Extra markers: [SECTION: X], [DEFINITIONS], [ENDNOTES]
///
/// WHY FILES API vs INLINE BASE64?
/// PDF files can be large. Files API is the recommended approach for PDFs —
/// the file is stored server-side and referenced by URI, avoiding request size limits.
///
/// COMPARE WITH PdfTextExtractor:
///   PdfPig: fast, offline, text-only, loses all visual data
///   GeminiPdfExtractor: slower (API call), requires internet, captures visuals
/// </summary>
public class GeminiPdfExtractor
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiPdfExtractor> _logger;

    private const string BaseUrl = "https://generativelanguage.googleapis.com";

    public GeminiPdfExtractor(
        HttpClient httpClient,
        IOptions<GeminiSettings> settings,
        ILogger<GeminiPdfExtractor> logger)
    {
        _httpClient = httpClient;
        _apiKey = settings.Value.ApiKey;
        _model = settings.Value.Model;
        _logger = logger;
    }

    /// <summary>
    /// Full pipeline: upload → extract → delete → return structured text.
    /// Output format: [PAGE N]-delimited with [SECTION: X], [DEFINITIONS], [ENDNOTES] markers.
    /// </summary>
    public async Task<string> ExtractTextAsync(
        Stream pdfStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Uploading '{FileName}' to Gemini Files API...", fileName);
        var (fileUri, filePath) = await UploadFileAsync(pdfStream, fileName, cancellationToken);
        _logger.LogInformation("Uploaded. URI: {Uri}", fileUri);

        try
        {
            _logger.LogInformation("Requesting structured extraction via Gemini vision...");
            var text = await ExtractWithGeminiAsync(fileUri, cancellationToken);
            _logger.LogInformation("Extraction complete. {Chars} chars extracted", text.Length);
            return text;
        }
        finally
        {
            // Always clean up — files expire in 48h anyway but good practice
            await DeleteFileAsync(filePath, cancellationToken);
        }
    }

    /// <summary>
    /// Upload PDF to Gemini Files API using multipart upload.
    /// Returns (fileUri for GenerateContent, filePath for deletion).
    /// </summary>
    private async Task<(string FileUri, string FilePath)> UploadFileAsync(
        Stream pdfStream, string fileName, CancellationToken cancellationToken)
    {
        // Multipart upload endpoint
        var url = $"{BaseUrl}/upload/v1beta/files?uploadType=multipart&key={_apiKey}";

        // Buffer the stream (required for multipart)
        using var ms = new MemoryStream();
        await pdfStream.CopyToAsync(ms, cancellationToken);
        var pdfBytes = ms.ToArray();

        // Part 1: JSON metadata
        var metadata = JsonSerializer.Serialize(new { file = new { display_name = fileName } });

        // Part 2: PDF binary
        // multipart/related is the correct content type for Gemini Files API upload
        var multipart = new MultipartContent("related");
        multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));

        var filePart = new ByteArrayContent(pdfBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(filePart);

        var response = await _httpClient.PostAsync(url, multipart, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"File upload failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var file = doc.RootElement.GetProperty("file");

        // URI: used as file_uri in GenerateContent
        // name: "files/abc123" — used for deletion
        var uri = file.GetProperty("uri").GetString()
            ?? throw new Exception("No URI in upload response");
        var name = file.GetProperty("name").GetString()
            ?? throw new Exception("No name in upload response");

        return (uri, name);
    }

    /// <summary>
    /// Ask Gemini to extract all text + visual content from the uploaded PDF.
    /// Instructs Gemini to emit our custom markers so PerDocumentService can parse them.
    /// </summary>
    private async Task<string> ExtractWithGeminiAsync(
        string fileUri, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/v1beta/models/{_model}:generateContent?key={_apiKey}";

        // This prompt is carefully structured to produce output that PerDocumentService
        // can parse without ambiguity. The markers [PAGE N], [SECTION: X], etc.
        // match the format PdfTextExtractor produces for text-based PDFs.
        var prompt = """
            Extract all content from this PDF document, organized page by page.

            REQUIRED FORMAT — follow exactly:
            - Start each page with [PAGE N] on its own line (N = actual page number)
            - Before the first page of each major section, insert on its own line:
              [SECTION: SectionName]
              Known section names: "Progress toward carbon neutral", "Design and Source",
              "Make", "Package and Ship", "Use", "Recover", "Definitions", "Carbon Footprint", "Endnotes"
            - On the Definitions/Glossary page, also insert [DEFINITIONS] on its own line
            - On Endnotes pages, also insert [ENDNOTES] on its own line
            - Separate paragraphs with a blank line

            CONTENT EXTRACTION RULES:
            1. Charts and diagrams: describe what they show, including ALL numbers,
               percentages, and data labels visible in the chart
            2. Stat callout boxes (large bold numbers like "100%", "76%"):
               include them as: "Stat: [number] — [descriptive label from the box]"
            3. Material cards (e.g. "Gold. We use X% recycled gold..."): include verbatim
            4. Tables: reproduce all rows and columns in readable text;
               use | to separate columns if the table has multiple columns
            5. SKIP the running header "iMac | Product Environmental Report [number]" on every page
            6. SKIP purely decorative graphics (background colors, borders, icons) that carry no data
            7. Do NOT add any commentary, introduction, summary, or conclusion
            8. Extract content only — no meta-commentary like "This page contains..."
            """;

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { file_data = new { mime_type = "application/pdf", file_uri = fileUri } },
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,   // Low temp for factual extraction — no creativity wanted
                maxOutputTokens = 8192
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Gemini extraction failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? throw new Exception("Empty text in Gemini response");
    }

    /// <summary>
    /// Delete the uploaded file from Gemini Files API.
    /// Non-critical — files auto-expire after 48h.
    /// </summary>
    private async Task DeleteFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            // filePath = "files/abc123"
            var url = $"{BaseUrl}/v1beta/{filePath}?key={_apiKey}";
            await _httpClient.DeleteAsync(url, cancellationToken);
            _logger.LogInformation("Deleted Gemini file: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not delete Gemini file '{Path}': {Err}", filePath, ex.Message);
        }
    }
}
