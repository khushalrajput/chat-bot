using System.Text.Json.Serialization;

namespace ChatBot.Models.Gemini;

/// <summary>
/// Maps exactly to Gemini API request schema.
/// These are DTOs (Data Transfer Objects) — they exist ONLY to serialize/deserialize JSON.
/// No business logic here. Ever.
///
/// WHY separate from domain models?
/// Your domain says "ChatMessage". Gemini says "Content" with "Parts".
/// OpenAI says "messages" with "content". Keep API shapes isolated.
/// </summary>
public class GeminiRequest
{
    [JsonPropertyName("contents")]
    public List<GeminiContent> Contents { get; set; } = [];

    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; set; }

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

public class GeminiContent
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public List<GeminiPart> Parts { get; set; } = [];
}

public class GeminiPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Controls HOW the model generates responses.
/// Temperature = randomness. Low = factual/consistent. High = creative/varied.
/// MaxOutputTokens = cap response length (cost control + prevents runaway responses).
/// </summary>
public class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.2f;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; } = 1024;
}
