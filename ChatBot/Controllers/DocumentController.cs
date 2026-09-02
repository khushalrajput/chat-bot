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

    public DocumentController(
        IDocumentService documentService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService)
    {
        _documentService = documentService;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
    }

    /// <summary>
    /// Ingest a text document into the RAG pipeline.
    /// This is the FULL ingestion pipeline in one endpoint:
    /// Upload → Parse → Chunk → Embed → Store
    ///
    /// PRODUCTION CONSIDERATION:
    /// In real apps, ingestion is async (queue-based).
    /// Embedding 1000 chunks takes time — don't block the HTTP request.
    /// Use background jobs (Hangfire, Azure Service Bus, etc.)
    /// For learning: synchronous is fine.
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> UploadDocument(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (!file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .txt files supported for now" });

        // Step 1: Read file content
        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync(cancellationToken);

        // Step 2: Chunk the document
        var chunks = _documentService.ChunkDocument(file.FileName, content);

        // Step 3: Generate embeddings for all chunks (batch API call)
        var texts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, cancellationToken);

        // Step 4: Attach embeddings to chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = embeddings[i];
        }

        // Step 5: Store in vector store
        _vectorStoreService.AddChunks(chunks);

        return Ok(new
        {
            document = file.FileName,
            chunksCreated = chunks.Count,
            embeddingDimension = embeddings[0].Length,
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
