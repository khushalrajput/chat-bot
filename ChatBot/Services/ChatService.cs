using System.Runtime.CompilerServices;
using ChatBot.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatBot.Services;

/// <summary>
/// SK-based chat service.
///
/// COMPARE WITH OLD GeminiService:
///
/// OLD (raw HTTP):
///   - Build GeminiRequest DTO manually
///   - Serialize to JSON
///   - POST to Gemini URL with API key in query string
///   - Deserialize GeminiResponse
///   - Extract text from candidates[0].content.parts[0].text
///   - For streaming: manually parse SSE lines
///
/// NEW (Semantic Kernel):
///   - Build ChatHistory (SK's built-in type)
///   - Call GetChatMessageContentAsync()
///   - Done. SK handles HTTP, JSON, auth, parsing internally.
///
/// Same result. ~80% less code. Provider-swappable.
/// </summary>
public class ChatService : IChatService
{
    private readonly IChatCompletionService _chatCompletion;
    private readonly ILogger<ChatService> _logger;

    /// <summary>
    /// SK's IChatCompletionService is injected by DI.
    /// Which PROVIDER it uses (Gemini, OpenAI, Claude) depends on
    /// what you registered in Program.cs. This class doesn't know or care.
    /// </summary>
    public ChatService(
        IChatCompletionService chatCompletion,
        ILogger<ChatService> logger)
    {
        _chatCompletion = chatCompletion;
        _logger = logger;
    }

    public async Task<string> GetResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = BuildChatHistory(userMessage, conversationHistory);

        _logger.LogDebug("Sending {Count} messages to LLM", chatHistory.Count);

        // ONE LINE replaces entire GeminiService.GenerateResponseAsync()
        // SK handles: HTTP call, auth, JSON serialization, response parsing
        var response = await _chatCompletion.GetChatMessageContentAsync(
            chatHistory,
            cancellationToken: cancellationToken);

        var text = response.Content;

        if (string.IsNullOrEmpty(text))
        {
            _logger.LogWarning("LLM returned empty response");
            return "I couldn't generate a response. Please try again.";
        }

        return text;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatHistory = BuildChatHistory(userMessage, conversationHistory);

        _logger.LogDebug("Streaming {Count} messages from LLM", chatHistory.Count);

        // ONE LINE replaces entire SSE parsing logic
        // SK handles: streamGenerateContent endpoint, SSE parsing, chunking
        await foreach (var chunk in _chatCompletion.GetStreamingChatMessageContentsAsync(
            chatHistory,
            cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                yield return chunk.Content;
            }
        }
    }

    /// <summary>
    /// Convert our domain ChatMessage list to SK's ChatHistory.
    ///
    /// SK's ChatHistory is similar to our ChatMessage list but with:
    /// - Built-in role enum (AuthorRole.User, .Assistant, .System)
    /// - System message support (what we called "system instruction")
    /// - Rich content (images, function calls — for future use)
    ///
    /// NOTE: Gemini uses "model" for assistant role.
    /// SK handles this mapping internally — we use AuthorRole.Assistant,
    /// SK converts to "model" when talking to Gemini. Abstraction win.
    /// </summary>
    private static ChatHistory BuildChatHistory(
        string userMessage,
        List<ChatMessage>? conversationHistory)
    {
        var chatHistory = new ChatHistory();

        // System instruction — same as before but using SK's API
        chatHistory.AddSystemMessage("""
            You are a helpful HR assistant for answering questions about company policies.
            Be concise and professional. If you don't know something, say so clearly.
            Do not make up policies.
            """);

        // Add conversation history
        if (conversationHistory is { Count: > 0 })
        {
            foreach (var msg in conversationHistory)
            {
                if (msg.Role == "user")
                    chatHistory.AddUserMessage(msg.Content);
                else
                    chatHistory.AddAssistantMessage(msg.Content);
            }
        }

        // Add current message
        chatHistory.AddUserMessage(userMessage);

        return chatHistory;
    }
}
