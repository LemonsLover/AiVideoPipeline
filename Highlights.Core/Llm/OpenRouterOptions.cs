namespace Highlights.Core.Llm;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";

    /// <summary>API key; put it only in %APPDATA%\Highlights\appsettings.local.json. Empty = use the environment variable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Fallback when <see cref="ApiKey"/> is empty.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>Primary model first, then fallbacks tried when a model fails or returns unusable output.</summary>
    public string[]? Models { get; set; }

    /// <summary>
    /// Null = provider default (recommended: many current models reject it). When set, it is sent only to models
    /// whose catalog entry lists it.
    /// </summary>
    public double? Temperature { get; set; }
    public int MaxOutputTokens { get; set; } = 16000;

    /// <summary>Re-asks per model when the JSON is malformed or fails validation (the error is sent back).</summary>
    public int MaxRepairAttempts { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Sent as X-Title so requests are identifiable in the OpenRouter dashboard.</summary>
    public string AppName { get; set; } = "Highlights";

    public IReadOnlyList<string> EffectiveModels => Models is { Length: > 0 }
        ? Models
        : ["anthropic/claude-opus-5.5", "anthropic/claude-sonnet-5", "google/gemini-3.8-flash"];
}
