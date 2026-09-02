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
        List<string> sources = [];

        if (_vectorStoreService.ChunkCount > 0)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(
                request.Message, cancellationToken);

            var relevantChunks = _vectorStoreService.Search(queryEmbedding, topK: 3);

            var context = string.Join("\n\n---\n\n",
                relevantChunks.Select(c => c.Content));

            sources = relevantChunks.Select(c => c.Id).ToList();

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

    private static async Task WriteSseEventAsync(
        HttpResponse response, object data, CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

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
