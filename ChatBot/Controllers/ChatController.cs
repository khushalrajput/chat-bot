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
    /// Clear conversation history for a session.
    /// "Start over" button in a real app.
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
