using ChatBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;
    private readonly PdfTextExtractor _pdfTextExtractor;
    private readonly GeminiPdfExtractor _geminiPdfExtractor;
    private readonly PerDocumentService _perDocumentService;

    public DocumentController(
        IDocumentService documentService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        PdfTextExtractor pdfTextExtractor,
        GeminiPdfExtractor geminiPdfExtractor,
        PerDocumentService perDocumentService)
    {
        _documentService = documentService;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _pdfTextExtractor = pdfTextExtractor;
        _geminiPdfExtractor = geminiPdfExtractor;
        _perDocumentService = perDocumentService;
    }

    /// <summary>
    /// Ingest a document (PDF or TXT) into the RAG pipeline.
    /// Full pipeline: Upload → Extract Text → Chunk → Embed → Store
    ///
    /// EXTRACTION STRATEGIES (controlled by ?vision= query param):
    ///
    ///   vision=false (default): PdfPig text-only → DocumentService (chapter-based)
    ///     Use for: text-heavy PDFs (GPO Annual Report, World Bank GEP)
    ///     Pro: fast, offline, zero extra tokens
    ///     Con: loses charts, callout boxes, diagrams
    ///
    ///   vision=true: Gemini Files API → PerDocumentService (lifecycle-stage-based)
    ///     Use for: vision-heavy PDFs (iMac PER, product reports, infographic-heavy docs)
    ///     Pro: captures visual data — charts, stat boxes, material cards
    ///     Con: extra API call + tokens for extraction step
    ///
    /// PRODUCTION CONSIDERATION:
    /// In real apps, ingestion is async (queue-based).
    /// Embedding 100+ chunks takes time — don't block the HTTP request.
    /// For learning: synchronous is fine.
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> UploadDocument(
        IFormFile file,
        [FromQuery] bool vision = false,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".txt" and not ".pdf")
            return BadRequest(new { error = "Only .txt and .pdf files are supported" });

        // Step 1: Extract text based on file type and chosen strategy
        string content;
        var extractionMethod = "text";

        if (extension == ".pdf" && vision)
        {
            // Vision strategy: Gemini Files API — captures text AND visual content
            // (charts, stat callouts, material cards, lifecycle diagrams)
            using var stream = file.OpenReadStream();
            content = await _geminiPdfExtractor.ExtractTextAsync(
                stream, file.FileName, cancellationToken);
            extractionMethod = "gemini-vision";
        }
        else if (extension == ".pdf")
        {
            // Text strategy: PdfPig — fast, offline, text glyphs only
            using var stream = file.OpenReadStream();
            content = _pdfTextExtractor.ExtractText(stream);
            extractionMethod = "pdfpig";
        }
        else
        {
            // TXT: read directly
            using var reader = new StreamReader(file.OpenReadStream());
            content = await reader.ReadToEndAsync(cancellationToken);
            extractionMethod = "plaintext";
        }

        // Step 2: Chunk using the appropriate strategy
        //   vision=true  → PerDocumentService: section-based (lifecycle stages)
        //   vision=false → DocumentService: chapter-based (region hierarchy)
        var chunks = vision
            ? _perDocumentService.ChunkDocument(file.FileName, content)
            : _documentService.ChunkDocument(file.FileName, content);

        // Step 3: Generate embeddings for all chunks (batch API call)
        var texts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, cancellationToken);

        // Step 4: Attach embeddings to chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = embeddings[i];
        }

        // Step 5: Store in vector store (SQLite + memory cache)
        _vectorStoreService.AddChunks(chunks);

        return Ok(new
        {
            document = file.FileName,
            fileType = extension,
            extractionMethod,
            chunksCreated = chunks.Count,
            embeddingDimension = embeddings[0].Length,
            averageChunkSize = (int)chunks.Average(c => c.Content.Length),
            totalChunksInStore = _vectorStoreService.ChunkCount
        });
    }

    /// <summary>
    /// Check what's in the store — useful for debugging.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            totalChunks = _vectorStoreService.ChunkCount
        });
    }
}
