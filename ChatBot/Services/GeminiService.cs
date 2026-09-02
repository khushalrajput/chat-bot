using System.Net.Http.Json;
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
        // Build contents list — this is where multi-turn magic happens
        //
        // HOW MULTI-TURN WORKS IN GEMINI API:
        // You send the FULL conversation as an array of contents:
        // [
        //   { role: "user",  parts: "How many sick leaves?" },
        //   { role: "model", parts: "12 days per year." },
        //   { role: "user",  parts: "Can I carry them forward?" }  ← "them" = sick leaves
        // ]
        //
        // Gemini reads ALL messages and understands "them" from context.
        // Without history, "Can I carry them forward?" is meaningless.

        var contents = new List<GeminiContent>();

        // Add conversation history (previous turns)
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

        // Add current user message
        contents.Add(new GeminiContent
        {
            Role = "user",
            Parts = [new GeminiPart { Text = userMessage }]
        });

        var request = new GeminiRequest
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

        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        _logger.LogDebug("Calling Gemini with {TurnCount} turns", contents.Count);

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
}
