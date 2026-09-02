using System.Text.Json.Serialization;

namespace ChatBot.Models.Gemini;

/// <summary>
/// DTOs for Gemini Embedding API.
///
/// Embedding API is DIFFERENT from generation API:
/// - Generation: text in → text out (conversation)
/// - Embedding: text in → float[] out (vector for search)
///
/// Same API key, different endpoint, different models.
/// Embedding model: text-embedding-004 (768 dimensions)
/// </summary>

// --- Batch Request: embed multiple texts in one API call ---
// WHY batch? One API call for 20 chunks vs 20 separate calls.
// Reduces latency, respects rate limits, production standard.

public class GeminiEmbedRequest
{
    [JsonPropertyName("requests")]
    public List<GeminiEmbedSingleRequest> Requests { get; set; } = [];
}

public class GeminiEmbedSingleRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public GeminiContent Content { get; set; } = new();
}

// --- Response ---

public class GeminiEmbedResponse
{
    [JsonPropertyName("embeddings")]
    public List<GeminiEmbedding>? Embeddings { get; set; }
}

public class GeminiEmbedding
{
    [JsonPropertyName("values")]
    public float[]? Values { get; set; }
}
