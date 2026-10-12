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

    public DocumentController(
        IDocumentService documentService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        PdfTextExtractor pdfTextExtractor)
    {
        _documentService = documentService;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _pdfTextExtractor = pdfTextExtractor;
    }

    /// <summary>
    /// Ingest a document (PDF or TXT) into the RAG pipeline.
    /// Full pipeline: Upload → Extract Text → Chunk → Embed → Store
    ///
    /// WHAT CHANGED FROM BEFORE:
    /// - Now accepts .pdf files (via PdfTextExtractor) in addition to .txt
    /// - PDF extraction preserves page numbers and paragraph structure
    /// - Chunks now carry metadata (section, page, type) for source citations
    ///
    /// PRODUCTION CONSIDERATION:
    /// In real apps, ingestion is async (queue-based).
    /// Embedding 100+ chunks takes time — don't block the HTTP request.
    /// For learning: synchronous is fine.
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> UploadDocument(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".txt" and not ".pdf")
            return BadRequest(new { error = "Only .txt and .pdf files are supported" });

        // Step 1: Extract text based on file type
        string content;

        if (extension == ".pdf")
        {
            // PDF: extract text with page markers using PdfPig
            using var stream = file.OpenReadStream();
            content = _pdfTextExtractor.ExtractText(stream);
        }
        else
        {
            // TXT: read directly
            using var reader = new StreamReader(file.OpenReadStream());
            content = await reader.ReadToEndAsync(cancellationToken);
        }

        // Step 2: Chunk the document (narrative-aware chunking with metadata)
        var chunks = _documentService.ChunkDocument(file.FileName, content);

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
