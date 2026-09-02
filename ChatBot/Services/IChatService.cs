using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>
/// Chat service abstraction — provider agnostic.
///
/// Notice: no mention of Gemini, OpenAI, or any provider.
/// This interface speaks OUR domain language.
/// SK handles the provider details internally.
///
/// WHY still wrap SK behind our own interface?
/// 1. Controllers depend on OUR interface, not SK's — cleaner testing
/// 2. RAG prompt building is OUR business logic, not SK's concern
/// 3. If SK introduces breaking changes, we fix one place
/// 4. System instructions, temperature — configured in ONE place
/// </summary>
public interface IChatService
{
    Task<string> GetResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default);
}
