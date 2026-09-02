using ChatBot.Models;
using ChatBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IGeminiService _geminiService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;
    private readonly IChatHistoryService _chatHistoryService;

    public ChatController(
        IGeminiService geminiService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        IChatHistoryService chatHistoryService)
    {
        _geminiService = geminiService;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _chatHistoryService = chatHistoryService;
    }

    /// <summary>
    /// RAG-powered chat with conversation history.
    ///
    /// SessionId enables multi-turn conversations:
    /// Turn 1: "How many sick leaves?" → "12 days"
    /// Turn 2: "Can I carry them forward?" → understands "them" = sick leaves
    ///
    /// Without sessionId, every question is independent (stateless).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message is required" });

        // Generate sessionId if not provided — allows stateless testing too
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();

        // Get existing conversation history for this session
        var history = _chatHistoryService.GetHistory(sessionId);

        string response;
        List<string> sources = [];

        if (_vectorStoreService.ChunkCount > 0)
        {
            // RAG path: embed → search → augment → generate
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(
                request.Message, cancellationToken);

            var relevantChunks = _vectorStoreService.Search(queryEmbedding, topK: 3);

            var context = string.Join("\n\n---\n\n",
                relevantChunks.Select(c => c.Content));

            sources = relevantChunks.Select(c => c.Id).ToList();

            // Pass history so LLM understands conversation context
            response = await _geminiService.GenerateResponseAsync(
                BuildRagPrompt(request.Message, context),
                history,
                cancellationToken);
        }
        else
        {
            response = await _geminiService.GenerateResponseAsync(
                request.Message,
                history,
                cancellationToken);
        }

        // Store both user message and assistant response in history
        // Next request with same sessionId will include these
        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "user",
            Content = request.Message
        });
        _chatHistoryService.AddMessage(sessionId, new ChatMessage
        {
            Role = "model",  // Gemini uses "model", not "assistant"
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

    /// <summary>
    /// SSE streaming endpoint — tokens arrive in real-time.
    ///
    /// HOW SSE WORKS FROM SERVER SIDE:
    /// 1. Set Content-Type to "text/event-stream" — tells client "this is SSE"
    /// 2. Write "data: {json}\n\n" for each token — SSE format
    /// 3. Flush after each write — forces bytes through network immediately
    ///    Without flush, ASP.NET buffers and sends in large batches — no streaming
    /// 4. Write "data: [DONE]\n\n" — signals end of stream
    ///
    /// WHY not return IAsyncEnumerable directly from controller?
    /// ASP.NET can return IAsyncEnumerable, but it serializes as JSON array.
    /// SSE is different protocol — must write raw to response stream.
    ///
    /// POSTMAN NOTE: Postman supports SSE. Send request, response appears
    /// token by token in real-time. Much more visible than JSON array.
    /// </summary>
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

        // SSE headers — must be set BEFORE writing body
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        string prompt;
        List<string> sources = [];

        if (_vectorStoreService.ChunkCount > 0)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(
                request.Message, cancellationToken);
            var relevantChunks = _vectorStoreService.Search(queryEmbedding, topK: 3);
            var context = string.Join("\n\n---\n\n", relevantChunks.Select(c => c.Content));
            sources = relevantChunks.Select(c => c.Id).ToList();
            prompt = BuildRagPrompt(request.Message, context);
        }
        else
        {
            prompt = request.Message;
        }

        // Send sources first so client knows which docs were used
        await WriteSseEventAsync(Response, new { type = "sources", sessionId, sources }, cancellationToken);

        // Stream tokens as they arrive from Gemini
        var fullResponse = new System.Text.StringBuilder();

        await foreach (var token in _geminiService.StreamResponseAsync(
            prompt, history, cancellationToken))
        {
            fullResponse.Append(token);
            await WriteSseEventAsync(Response, new { type = "token", token }, cancellationToken);
        }

        // Signal stream is complete
        await WriteSseEventAsync(Response, new { type = "done" }, cancellationToken);

        // Save to conversation history (same as non-streaming)
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

    /// <summary>
    /// Write a single SSE event.
    /// Format: "data: {json}\n\n"
    /// The double newline is REQUIRED by SSE spec — it marks event boundary.
    /// FlushAsync forces bytes to network — without it, buffering kills streaming.
    /// </summary>
    private static async Task WriteSseEventAsync(
        HttpResponse response, object data, CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Clear conversation history for a session.
    /// </summary>
    [HttpDelete("{sessionId}")]
    public IActionResult ClearSession(string sessionId)
    {
        _chatHistoryService.ClearHistory(sessionId);
        return Ok(new { message = "Session cleared", sessionId });
    }

    private static string BuildRagPrompt(string question, string context)
    {
        return $"""
            Answer the following question based ONLY on the provided context.
            If the answer cannot be found in the context, clearly state that the information
            is not available in the provided documents. Do not make up information.

            Context from company documents:
            {context}

            Question: {question}

            Provide a clear, concise answer based on the context above.
            """;
    }
}

public record ChatRequest(string Message, string? SessionId = null);

public record ChatResponse
{
    public required string SessionId { get; init; }
    public required string Message { get; init; }
    public required string Response { get; init; }
    public List<string> Sources { get; init; } = [];
}
