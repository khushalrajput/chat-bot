using ChatBot.Models;

namespace ChatBot.Services;

public interface IGeminiService
{
    /// <summary>
    /// Generate response with optional conversation history.
    /// History enables multi-turn conversations where LLM understands context
    /// from previous messages (e.g., "them" refers to sick leaves from last turn).
    /// </summary>
    Task<string> GenerateResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default);
}
