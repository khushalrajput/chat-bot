namespace ChatBot.Settings;

/// <summary>
/// Strongly-typed configuration for Gemini API.
/// Bound from appsettings.json + user-secrets via Options Pattern.
///
/// WHY Options Pattern instead of IConfiguration directly?
/// 1. Compile-time safety — typo in key name caught at build, not runtime
/// 2. Testable — inject IOptions<GeminiSettings> in unit tests easily
/// 3. Validated — can add [Required] attributes and validate at startup
/// </summary>
public class GeminiSettings
{
    public const string SectionName = "Gemini";

    public required string ApiKey { get; set; }

    /// <summary>
    /// Model to use. Default: gemini-2.0-flash (fast, free tier friendly).
    /// gemini-2.5-pro available but higher rate limits.
    /// </summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>
    /// Base URL for Gemini API. Extracted to config so you can swap
    /// to a proxy, mock server, or different region without code changes.
    /// </summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}
