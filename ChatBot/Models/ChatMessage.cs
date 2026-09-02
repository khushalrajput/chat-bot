namespace ChatBot.Models;

/// <summary>
/// A single message in a conversation.
/// Role is either "user" or "model" (Gemini's term for assistant).
///
/// WHY domain model and not reuse GeminiContent?
/// GeminiContent is an API DTO — tied to Gemini's schema.
/// ChatMessage is OUR domain concept. If we switch to OpenAI,
/// ChatMessage stays the same, only the mapping layer changes.
/// </summary>
public class ChatMessage
{
    public required string Role { get; set; }
    public required string Content { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
