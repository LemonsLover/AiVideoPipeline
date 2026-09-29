using System.Text.Json.Nodes;

namespace Highlights.Core.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    /// <summary>Multimodal content (text and images, in order); when set, it is sent instead of <see cref="Content"/>.</summary>
    public IReadOnlyList<LlmContentPart>? Parts { get; init; }

    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);

    /// <summary>A user message made of text and images.</summary>
    public static LlmMessage UserParts(IReadOnlyList<LlmContentPart> parts) =>
        new("user", string.Concat(parts.Where(p => p.Text is not null).Select(p => p.Text))) { Parts = parts };
}

/// <summary>Either text or a JPEG image.</summary>
/// <param name="LowDetail">Ask the provider to process the image at low resolution (~4× fewer tokens).</param>
public sealed record LlmContentPart(string? Text, byte[]? Jpeg, bool LowDetail = false)
{
    public static LlmContentPart FromText(string text) => new(text, null);
    public static LlmContentPart FromJpeg(byte[] jpeg, bool lowDetail = false) => new(null, jpeg, lowDetail);
}

/// <summary>JSON schema for structured output. <see cref="Name"/> must match ^[a-zA-Z0-9_-]+$.</summary>
public sealed record LlmJsonSchema(string Name, JsonObject Schema);

public sealed record LlmRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>When set, the provider is asked to enforce this schema (json_schema response format).</summary>
    public LlmJsonSchema? JsonSchema { get; init; }
}

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, decimal? CostUsd);

public sealed record LlmResponse(string Content, string Model, LlmUsage Usage, string? FinishReason);

/// <param name="SupportedParameters">Request parameters the model accepts (OpenRouter "supported_parameters").</param>
/// <param name="PromptPricePerMillion">USD per million input tokens (0 = free, negative = unknown/varies).</param>
/// <param name="CompletionPricePerMillion">USD per million output tokens (0 = free, negative = unknown/varies).</param>
public sealed record LlmModelInfo(
    string Id, IReadOnlySet<string> SupportedParameters, int? ContextLength,
    string Name = "", decimal PromptPricePerMillion = 0, decimal CompletionPricePerMillion = 0, bool AcceptsImages = false)
{
    public bool SupportsJsonSchema => SupportedParameters.Contains("structured_outputs");
    public bool Supports(string parameter) => SupportedParameters.Contains(parameter);
    public bool IsFree => PromptPricePerMillion == 0 && CompletionPricePerMillion == 0;
}

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Model metadata, or null if the model is unknown to the provider.</summary>
    Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default);

    /// <summary>All models the provider offers (for the model picker).</summary>
    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provider error. <see cref="IsFatal"/> errors (auth, credits) stop the fallback chain.</summary>
/// <param name="StatusCode">HTTP status, when the provider answered with an error.</param>
/// <param name="RetryAfter">How long the provider asks to wait (rate limits).</param>
public sealed class LlmException(string message, bool isFatal, Exception? inner = null, int? statusCode = null, TimeSpan? retryAfter = null)
    : Exception(message, inner)
{
    public bool IsFatal { get; } = isFatal;
    public int? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Rate limited (typical for free models): worth waiting and retrying the same model.</summary>
    public bool IsRateLimit => StatusCode == 429;
}
