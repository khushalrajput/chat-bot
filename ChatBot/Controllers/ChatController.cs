using ChatBot.Models;
using ChatBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;
    private readonly IChatHistoryService _chatHistoryService;

    public ChatController(
        IChatService chatService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        IChatHistoryService chatHistoryService)
    {
        _chatService = chatService;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _chatHistoryService = chatHistoryService;
    }

    [HttpPost]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message is required" });

        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();
        var history = _chatHistoryService.GetHistory(sessionId);

        string response;
        List<SourceInfo> sources = [];

        if (_vectorStoreService.ChunkCount > 0)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(
                request.Message, cancellationToken);

            var relevantChunks = _vectorStoreService.Search(queryEmbedding);

            // Format context with metadata for source attribution
            var context = FormatChunksAsContext(relevantChunks);

            // Build structured source info for response
            sources = relevantChunks.Select(c => new SourceInfo
            {
                Document = c.DocumentName,
                Chapter = c.Chapter,
                Section = c.SectionTitle,
                SubSection = c.SubSection,
                Page = c.PageNumber
            }).ToList();

            response = await _chatService.GetResponseAsync(
                BuildRagPrompt(request.Message, context),
                history,
                cancellationToken);
        }
        else
        {
            response = await _chatService.GetResponseAsync(
                request.Message,
                history,
                cancellationToken);
        }

        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "user",
            Content = request.Message
        });
        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "model",
            Content = response
        });

        return Ok(new ChatResponse
        {
            SessionId = sessionId,
            Message = request.Message,
            Response = response,
            Sources = sources
        });
    }

    [HttpPost("stream")]
    public async Task StreamChat(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("{\"error\":\"Message is required\"}", cancellationToken);
            return;
        }

        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();
        var history = _chatHistoryService.GetHistory(sessionId);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        string prompt;
        List<SourceInfo> sources = [];

        if (_vectorStoreService.ChunkCount > 0)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(
                request.Message, cancellationToken);
            var relevantChunks = _vectorStoreService.Search(queryEmbedding);
            var context = FormatChunksAsContext(relevantChunks);

            sources = relevantChunks.Select(c => new SourceInfo
            {
                Document = c.DocumentName,
                Chapter = c.Chapter,
                Section = c.SectionTitle,
                SubSection = c.SubSection,
                Page = c.PageNumber
            }).ToList();

            prompt = BuildRagPrompt(request.Message, context);
        }
        else
        {
            prompt = request.Message;
        }

        await WriteSseEventAsync(Response, new { type = "sources", sessionId, sources }, cancellationToken);

        var fullResponse = new System.Text.StringBuilder();

        await foreach (var token in _chatService.StreamResponseAsync(
            prompt, history, cancellationToken))
        {
            fullResponse.Append(token);
            await WriteSseEventAsync(Response, new { type = "token", token }, cancellationToken);
        }

        await WriteSseEventAsync(Response, new { type = "done" }, cancellationToken);

        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "user",
            Content = request.Message
        });
        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "model",
            Content = fullResponse.ToString()
        });
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private static async Task WriteSseEventAsync(
        HttpResponse response, object data, CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data, _jsonOptions);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        var sessions = _chatHistoryService.GetAllSessions();
        return Ok(sessions);
    }

    [HttpGet("sessions/{sessionId}")]
    public IActionResult GetSessionHistory(string sessionId)
    {
        var history = _chatHistoryService.GetHistory(sessionId);
        return Ok(new { sessionId, messages = history });
    }

    [HttpDelete("{sessionId}")]
    public IActionResult ClearSession(string sessionId)
    {
        _chatHistoryService.ClearHistory(sessionId);
        return Ok(new { message = "Session cleared", sessionId });
    }

    /// <summary>
    /// Format retrieved chunks with metadata headers for the LLM.
    ///
    /// WHY include metadata in the context?
    /// 1. LLM can cite the section and page in its answer — verifiable
    /// 2. LLM uses section titles as disambiguation hints:
    ///    "Library Services" section vs "Financial Summary" section
    ///    helps when the same number appears in different contexts
    /// 3. User can go to the exact page in the PDF to verify
    ///
    /// OLD format: "chunk text\n---\nchunk text"
    /// NEW format: "[Source: doc | Section: ... | Page: N]\nchunk text"
    /// </summary>
    private static string FormatChunksAsContext(List<DocumentChunk> chunks)
    {
        return string.Join("\n\n---\n\n", chunks.Select(c =>
        {
            // Build hierarchical source path: Chapter > Section > SubSection
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.Chapter)) parts.Add(c.Chapter);
            if (!string.IsNullOrWhiteSpace(c.SectionTitle)) parts.Add(c.SectionTitle);
            if (!string.IsNullOrWhiteSpace(c.SubSection)) parts.Add(c.SubSection);
            var location = parts.Count > 0 ? string.Join(" > ", parts) : "Unknown";

            var typeTag = c.ChunkType != "paragraph" ? $" | Type: {c.ChunkType}" : "";

            return $"[Source: {c.DocumentName} | {location} | Page: {c.PageNumber}{typeTag}]\n{c.Content}";
        }));
    }

    /// <summary>
    /// Build the RAG prompt — tightened for fact extraction.
    ///
    /// WHAT CHANGED FROM BEFORE:
    /// - Explicit rule: quote numbers EXACTLY as written
    /// - Explicit rule: cite section and page
    /// - Explicit rule: never infer or calculate
    /// - Explicit "I don't know" instruction — prevents hallucination
    ///
    /// WHY so strict?
    /// GPO report has specific numbers: "802,834 copies", "304 libraries", "$94.9 million".
    /// If LLM rounds "802,834" to "about 800,000" — the answer is WRONG.
    /// The prompt must constrain the LLM to be a faithful reporter, not a summarizer.
    /// </summary>
    private static string BuildRagPrompt(string question, string context)
    {
        return $"""
            You are a document Q&A assistant for a structured economic report. Answer ONLY using the provided context.

            Rules:
            1. If the answer contains a number, statistic, or percentage, quote it EXACTLY as it appears in the source — do not round, paraphrase, or recalculate.
            2. Do NOT include source citations, references, page numbers, or section names in your answer. Source attribution is handled separately by the system.
            3. If the context does not contain the answer, say: "This information is not available in the provided document."
            4. Do NOT infer, extrapolate, or add information beyond what is explicitly stated in the context.
            5. Give a clean, direct answer without any "According to..." or "Sources:" prefixes.
            6. CRITICAL: The document contains data for multiple regions and chapters. Pay close attention to the [Source: ...] headers above each context block. Do NOT mix facts from one region with another or from one chapter with another. If the question asks about a specific region, use ONLY context from that region's section.

            Context from the document:
            {context}

            Question: {question}

            Provide a clear, accurate answer.
            """;
    }
}

public record ChatRequest(string Message, string? SessionId = null);

/// <summary>
/// Structured source citation — tells the user exactly where the answer came from.
///
/// OLD: List of string chunk IDs like "GPO-report.pdf::chunk-4" — meaningless to users
/// NEW: Document name, section heading, and page number — actionable for verification
/// </summary>
public record SourceInfo
{
    public required string Document { get; init; }
    public string? Chapter { get; init; }
    public string? Section { get; init; }
    public string? SubSection { get; init; }
    public int Page { get; init; }
}

public record ChatResponse
{
    public required string SessionId { get; init; }
    public required string Message { get; init; }
    public required string Response { get; init; }
    public List<SourceInfo> Sources { get; init; } = [];
}
