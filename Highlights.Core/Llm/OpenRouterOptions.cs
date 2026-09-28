namespace Highlights.Core.Llm;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";

    /// <summary>API key; put it only in %USERPROFILE%\.highlights\settings.json. Empty = use the environment variable.</summary>
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

    /// <summary>Per pipeline step (analyze, plan, describe, vision, refine): models, temperature, output limit.</summary>
    public Dictionary<string, LlmStageOptions> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public LlmStageOptions For(string stage) => Stages.GetValueOrDefault(stage) ?? new LlmStageOptions();

    /// <summary>The step's models, else the general list.</summary>
    public IReadOnlyList<string> ModelsFor(string stage) => For(stage).Models is { Length: > 0 } m ? m : EffectiveModels;

    public IReadOnlyList<string> EffectiveModels => Models is { Length: > 0 }
        ? Models
        : ["anthropic/claude-opus-5.5", "anthropic/claude-sonnet-5", "google/gemini-3.8-flash"];
}

/// <summary>Overrides for one pipeline step; null = the general OpenRouter setting.</summary>
public sealed class LlmStageOptions
{
    public string[]? Models { get; set; }
    public double? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
}
