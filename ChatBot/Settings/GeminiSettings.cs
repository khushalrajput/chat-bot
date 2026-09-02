namespace ChatBot.Settings;

/// <summary>
/// Configuration for Gemini via Semantic Kernel.
/// BaseUrl removed — SK manages endpoint URLs internally.
/// </summary>
public class GeminiSettings
{
    public const string SectionName = "Gemini";

    public required string ApiKey { get; set; }

    public string Model { get; set; } = "gemini-3.1-flash-lite";
}
