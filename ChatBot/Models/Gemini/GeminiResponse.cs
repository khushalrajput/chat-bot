using System.Text.Json.Serialization;

namespace ChatBot.Models.Gemini;

/// <summary>
/// Maps Gemini API response. Only fields we care about.
/// Gemini returns more fields (safety ratings, citation metadata, etc.)
/// We ignore them — System.Text.Json skips unknown properties by default.
///
/// PRODUCTION TIP: Log the raw response in development.
/// When Gemini updates their API, you'll see new fields appearing
/// and can decide whether to handle them.
/// </summary>
public class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

public class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }

    /// <summary>
    /// Why the model stopped generating.
    /// "STOP" = normal completion. "MAX_TOKENS" = hit limit. "SAFETY" = blocked.
    /// Important to check in production — a SAFETY finish means no useful content.
    /// </summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }
}
