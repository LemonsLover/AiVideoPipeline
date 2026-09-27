using System.Text.Json.Nodes;

namespace Highlights.Core.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
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
public sealed record LlmModelInfo(string Id, IReadOnlySet<string> SupportedParameters, int? ContextLength)
{
    public bool SupportsJsonSchema => SupportedParameters.Contains("structured_outputs");
    public bool Supports(string parameter) => SupportedParameters.Contains(parameter);
}

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Model metadata, or null if the model is unknown to the provider.</summary>
    Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default);
}

/// <summary>Provider error. <see cref="IsFatal"/> errors (auth, credits) stop the fallback chain.</summary>
public sealed class LlmException(string message, bool isFatal, Exception? inner = null) : Exception(message, inner)
{
    public bool IsFatal { get; } = isFatal;
}
