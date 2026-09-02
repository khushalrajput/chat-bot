using System.Net.Http.Json;
using ChatBot.Models.Gemini;
using ChatBot.Settings;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

/// <summary>
/// Calls Gemini Embedding API to convert text → vectors.
///
/// KEY CONCEPT: Embedding models are different from generation models.
/// - Generation model (gemini-3.1-flash-lite): understands and generates text
/// - Embedding model (text-embedding-004): converts text to fixed-size number arrays
///
/// The embedding model doesn't "understand" your question — it maps meaning to geometry.
/// Similar meanings → nearby points in 768-dimensional space.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiSettings _settings;
    private readonly ILogger<EmbeddingService> _logger;

    private const string EmbeddingModel = "gemini-embedding-001";

    public EmbeddingService(
        HttpClient httpClient,
        IOptions<GeminiSettings> settings,
        ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await GetEmbeddingsAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(
        List<string> texts,
        CancellationToken cancellationToken = default)
    {
        // Batch embed endpoint: one call, multiple texts
        // Much more efficient than calling single-embed N times
        var request = new GeminiEmbedRequest
        {
            Requests = texts.Select(text => new GeminiEmbedSingleRequest
            {
                Model = $"models/{EmbeddingModel}",
                Content = new GeminiContent
                {
                    Parts = [new GeminiPart { Text = text }]
                }
            }).ToList()
        };

        var url = $"{_settings.BaseUrl}/models/{EmbeddingModel}:batchEmbedContents?key={_settings.ApiKey}";

        _logger.LogDebug("Embedding {Count} texts via Gemini", texts.Count);

        var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Embedding API error {Status}: {Error}", response.StatusCode, error);
            throw new HttpRequestException($"Embedding API returned {response.StatusCode}: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiEmbedResponse>(cancellationToken);

        var embeddings = result?.Embeddings?
            .Select(e => e.Values ?? [])
            .ToList();

        if (embeddings is null || embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"Expected {texts.Count} embeddings, got {embeddings?.Count ?? 0}");
        }

        _logger.LogDebug("Got {Count} embeddings, dimension={Dim}",
            embeddings.Count, embeddings[0].Length);

        return embeddings;
    }
}
