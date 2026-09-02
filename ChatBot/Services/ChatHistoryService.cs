using System.Collections.Concurrent;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// In-memory conversation history store.
///
/// ConcurrentDictionary — thread-safe for concurrent HTTP requests.
/// Multiple users hitting API simultaneously = multiple threads accessing this.
/// Regular Dictionary would corrupt data under concurrency.
///
/// PRODUCTION UPGRADE PATH:
/// - Redis: distributed cache, survives restarts, TTL for auto-cleanup
/// - Database: full persistence, searchable, audit trail
/// - Hybrid: Redis for active sessions, DB for archival
///
/// MAX HISTORY: We cap at 20 messages (10 turns) to prevent:
/// 1. Token explosion — 100-turn history = massive API cost per call
/// 2. Memory bloat — thousands of sessions × unlimited messages
/// 3. Context confusion — very old messages become irrelevant noise
/// </summary>
public class ChatHistoryService : IChatHistoryService
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessions = new();
    private readonly ILogger<ChatHistoryService> _logger;

    private const int MaxHistoryMessages = 20; // 10 user + 10 assistant turns

    public ChatHistoryService(ILogger<ChatHistoryService> logger)
    {
        _logger = logger;
    }

    public List<ChatMessage> GetHistory(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ =>
        {
            _logger.LogDebug("New session created: {SessionId}", sessionId);
            return [];
        });
    }

    public void AddMessage(string sessionId, ChatMessage message)
    {
        var history = GetHistory(sessionId);

        lock (history) // Lock per-session, not globally — different sessions don't block each other
        {
            history.Add(message);

            // Trim oldest messages when limit exceeded
            // Keep most recent messages — old context less relevant
            if (history.Count > MaxHistoryMessages)
            {
                var excess = history.Count - MaxHistoryMessages;
                history.RemoveRange(0, excess);
                _logger.LogDebug("Session {SessionId}: trimmed {Count} old messages", sessionId, excess);
            }
        }
    }

    public void ClearHistory(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogDebug("Session {SessionId}: history cleared", sessionId);
    }
}
