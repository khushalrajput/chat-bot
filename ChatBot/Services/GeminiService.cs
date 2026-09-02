using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChatBot.Models;
using ChatBot.Models.Gemini;
using ChatBot.Settings;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

public class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiSettings _settings;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(
        HttpClient httpClient,
        IOptions<GeminiSettings> settings,
        ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> GenerateResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(userMessage, conversationHistory);
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        _logger.LogDebug("Calling Gemini with {TurnCount} turns", request.Contents.Count);

        var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini API error {StatusCode}: {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {errorBody}");
        }

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
        var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrEmpty(text))
        {
            _logger.LogWarning("Gemini returned empty response");
            return "I couldn't generate a response. Please try again.";
        }

        return text;
    }

    /// <summary>
    /// Stream response from Gemini using SSE (Server-Sent Events).
    ///
    /// HOW GEMINI STREAMING WORKS:
    /// 1. Call "streamGenerateContent" instead of "generateContent"
    /// 2. Add "?alt=sse" query param — tells Gemini to use SSE format
    /// 3. Response is chunked HTTP — arrives line by line
    /// 4. Each line prefixed with "data: " followed by JSON
    /// 5. Each JSON chunk has partial text in candidates[0].content.parts[0].text
    ///
    /// WHY HttpCompletionOption.ResponseHeadersRead?
    /// Default behavior: HttpClient buffers ENTIRE response in memory, then returns.
    /// That defeats streaming — you'd wait for all tokens then get them at once.
    /// ResponseHeadersRead: returns as soon as headers arrive, body streams lazily.
    /// This is CRITICAL for streaming — without it, no streaming happens.
    /// </summary>
    public async IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(userMessage, conversationHistory);

        // "streamGenerateContent" + "alt=sse" = streaming mode
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:streamGenerateContent?alt=sse&key={_settings.ApiKey}";

        _logger.LogDebug("Streaming from Gemini with {TurnCount} turns", request.Contents.Count);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };

        // ResponseHeadersRead = don't buffer full body, stream it
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini stream error {Status}: {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {errorBody}");
        }

        // Read response body as a stream — line by line
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // SSE format from Gemini:
        // data: {"candidates":[{"content":{"parts":[{"text":"Employees"}]}}]}
        //
        // data: {"candidates":[{"content":{"parts":[{"text":" are"}]}}]}
        //
        // Each "data: " line is a complete JSON chunk with partial text

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var json = line["data: ".Length..];

            // Extract text token — helper method so yield isn't in try-catch
            var text = TryParseToken(json);
            if (text is not null)
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Parse a single SSE JSON chunk and extract text token.
    /// Separated from streaming loop because C# forbids yield inside try-catch.
    /// </summary>
    private string? TryParseToken(string json)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize<GeminiResponse>(json);
            return chunk?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse SSE chunk: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Build Gemini request body — shared between streaming and non-streaming.
    /// Extracted to avoid duplication (DRY principle).
    /// </summary>
    private static GeminiRequest BuildRequest(
        string userMessage,
        List<ChatMessage>? conversationHistory)
    {
        var contents = new List<GeminiContent>();

        if (conversationHistory is { Count: > 0 })
        {
            foreach (var msg in conversationHistory)
            {
                contents.Add(new GeminiContent
                {
                    Role = msg.Role,
                    Parts = [new GeminiPart { Text = msg.Content }]
                });
            }
        }

        contents.Add(new GeminiContent
        {
            Role = "user",
            Parts = [new GeminiPart { Text = userMessage }]
        });

        return new GeminiRequest
        {
            Contents = contents,
            SystemInstruction = new GeminiContent
            {
                Parts =
                [
                    new GeminiPart
                    {
                        Text = """
                            You are a helpful HR assistant for answering questions about company policies.
                            Be concise and professional. If you don't know something, say so clearly.
                            Do not make up policies.
                            """
                    }
                ]
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.2f,
                MaxOutputTokens = 1024
            }
        };
    }
}
