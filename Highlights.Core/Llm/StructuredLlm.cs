using System.Text.Json;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Llm;

public sealed record StructuredResult<T>(T Value, string Model, IReadOnlyList<LlmUsage> Calls)
{
    public int PromptTokens => Calls.Sum(c => c.PromptTokens);
    public int CompletionTokens => Calls.Sum(c => c.CompletionTokens);
    public decimal? CostUsd => Calls.All(c => c.CostUsd is null) ? null : Calls.Sum(c => c.CostUsd ?? 0);
}

/// <summary>
/// Gets a validated JSON object from an LLM: json_schema response format when the model supports it,
/// otherwise the schema in the prompt; malformed/invalid output is sent back with the error for repair;
/// on provider errors or exhausted repairs the next model in the fallback list is tried.
/// </summary>
public sealed class StructuredLlm(ILlmClient client, IOptions<OpenRouterOptions> options, ILogger<StructuredLlm> logger)
{
    /// <param name="validate">Returns an error message for the model, or null when the value is acceptable.</param>
    /// <param name="onCall">Called after every paid call (including failed attempts), with the model id.</param>
    public async Task<StructuredResult<T>> CompleteAsync<T>(
        IReadOnlyList<LlmMessage> messages, LlmJsonSchema schema, Func<T, string?> validate,
        IProgress<StageProgress> progress, string stage, Action<string, LlmUsage>? onCall, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var calls = new List<LlmUsage>();
        var failures = new List<string>();

        foreach (var model in o.EffectiveModels)
        {
            LlmModelInfo? info;
            try
            {
                info = await client.GetModelInfoAsync(model, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Could not load the model catalog; assuming no json_schema support");
                info = null;
            }

            var useSchema = info?.SupportsJsonSchema == true;
            var conversation = useSchema
                ? messages.ToList()
                : [.. messages, LlmMessage.User(SchemaInstructions(schema))];

            for (var attempt = 0; attempt <= o.MaxRepairAttempts; attempt++)
            {
                progress.Report(new StageProgress(stage, null,
                    attempt == 0 ? $"asking {model}" : $"asking {model} to fix its answer (attempt {attempt + 1})", "llm"));

                // Optional parameters are sent only when the model lists them: with json_schema we require
                // providers to support every parameter, so an unsupported one blocks routing entirely.
                var request = new LlmRequest
                {
                    Model = model,
                    Messages = conversation,
                    Temperature = o.Temperature is { } t && info?.Supports("temperature") == true ? t : null,
                    MaxOutputTokens = info is null || info.Supports("max_tokens") ? o.MaxOutputTokens : null,
                    JsonSchema = useSchema ? schema : null,
                };

                LlmResponse response;
                try
                {
                    try
                    {
                        response = await client.CompleteAsync(request, cancellationToken);
                    }
                    catch (LlmException ex) when (!ex.IsFatal && IsUnroutableParameters(ex)
                                                  && (request.Temperature is not null || request.MaxOutputTokens is not null))
                    {
                        // The catalog lists parameters across all providers; the ones that enforce the schema
                        // may still reject e.g. temperature. Retry with only the essential parameters.
                        logger.LogWarning("{Model}: retrying without optional parameters", model);
                        response = await client.CompleteAsync(request with { Temperature = null, MaxOutputTokens = null }, cancellationToken);
                    }
                }
                catch (LlmException ex) when (!ex.IsFatal)
                {
                    logger.LogWarning("{Error}", ex.Message);
                    failures.Add(ex.Message);
                    break; // next model
                }
                catch (LlmException ex)
                {
                    throw new PipelineException(ex.Message, ex);
                }

                calls.Add(response.Usage);
                onCall?.Invoke(response.Model, response.Usage);
                var error = TryParse(response, validate, out var value);
                if (error is null)
                {
                    progress.Report(new StageProgress(stage, 1, $"answer from {response.Model}", "llm"));
                    return new StructuredResult<T>(value!, response.Model, calls);
                }

                logger.LogWarning("{Model} returned an unusable answer: {Error}", model, error);
                failures.Add($"{model}: {error}");
                conversation =
                [
                    .. conversation,
                    LlmMessage.Assistant(response.Content),
                    LlmMessage.User($"Your answer can't be used: {error}\nReturn the complete corrected JSON object only."),
                ];
            }
        }

        throw new PipelineException("All LLM models failed:\n  " + string.Join("\n  ", failures));
    }

    private static string? TryParse<T>(LlmResponse response, Func<T, string?> validate, out T? value)
    {
        value = default;
        if (response.FinishReason == "length")
            return "the answer was cut off (max output tokens reached); be more concise";

        var json = ExtractJson(response.Content);
        if (json is null)
            return "no JSON object found in the answer";
        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            return $"invalid JSON: {ex.Message}";
        }
        return value is null ? "the JSON is null" : validate(value);
    }

    private static bool IsUnroutableParameters(LlmException ex) =>
        ex.Message.Contains("No endpoints found that can handle the requested parameters", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips markdown fences / surrounding prose: takes the outermost {...}.</summary>
    internal static string? ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    private static string SchemaInstructions(LlmJsonSchema schema) =>
        "Respond with a single JSON object and nothing else (no markdown, no comments). " +
        "It must conform to this JSON Schema:\n" + schema.Schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
