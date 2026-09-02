using ChatBot.Models;

namespace ChatBot.Services;

public interface IGeminiService
{
    Task<string> GenerateResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream response token-by-token via IAsyncEnumerable.
    ///
    /// IAsyncEnumerable is .NET's native streaming primitive.
    /// "yield return" produces values one at a time, on demand.
    /// Consumer reads with "await foreach" — pulls next token when ready.
    ///
    /// WHY IAsyncEnumerable and not callback/event?
    /// - Composable: can use LINQ (Where, Take, etc.)
    /// - Cancellable: CancellationToken built in
    /// - Backpressure: consumer controls pace
    /// - Testable: easy to mock with async iterators
    /// </summary>
    IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default);
}
