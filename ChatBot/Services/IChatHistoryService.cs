using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Manages conversation history per session.
/// Each session = independent conversation with its own history.
/// </summary>
public interface IChatHistoryService
{
    /// <summary>
    /// Get or create a conversation for given session ID.
    /// </summary>
    List<ChatMessage> GetHistory(string sessionId);

    /// <summary>
    /// Add a message to conversation history.
    /// </summary>
    void AddMessage(string sessionId, ChatMessage message);

    /// <summary>
    /// Clear history for a session (start fresh).
    /// </summary>
    void ClearHistory(string sessionId);

    /// <summary>
    /// Get all active session IDs with their first user message as preview.
    /// </summary>
    List<SessionInfo> GetAllSessions();
}

public record SessionInfo(string SessionId, string Preview, int MessageCount, DateTime LastActivity);
