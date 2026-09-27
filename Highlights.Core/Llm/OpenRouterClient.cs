using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Llm;

/// <summary>OpenRouter chat completions (OpenAI-compatible API).</summary>
public sealed class OpenRouterClient(
    IHttpClientFactory httpClientFactory, IOptions<OpenRouterOptions> options, ILogger<OpenRouterClient> logger) : ILlmClient
{
    public const string HttpClientName = "openrouter";

    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private Dictionary<string, LlmModelInfo>? _catalog;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (request.Temperature is { } t)
            body["temperature"] = t;
        if (request.MaxOutputTokens is { } max)
            body["max_tokens"] = max;
        if (request.JsonSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = schema.Name,
                    ["strict"] = true,
                    ["schema"] = schema.Schema.DeepClone(),
                },
            };
            // Only route to providers that actually enforce response_format.
            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetApiKey());
        message.Headers.Add("X-Title", options.Value.AppName);

        using var http = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"{request.Model}: network error: {ex.Message}", isFatal: false, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"{request.Model}: HTTP {(int)response.StatusCode}: {ErrorMessage(text)}",
                    isFatal: response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            // OpenRouter may report upstream errors with HTTP 200 and an "error" object.
            if (root.TryGetProperty("error", out var error))
                throw new LlmException($"{request.Model}: {error.GetRawText()}", isFatal: false);

            var choice = root.GetProperty("choices")[0];
            var content = choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : "";
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? request.Model : request.Model;

            var usage = new LlmUsage(0, 0, null);
            if (root.TryGetProperty("usage", out var u))
            {
                usage = new LlmUsage(
                    u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                    u.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0,
                    u.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null);
            }

            logger.LogInformation("{Model}: {Prompt}+{Completion} tokens, ${Cost}", model, usage.PromptTokens,
                usage.CompletionTokens, usage.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? "?");
            return new LlmResponse(content, model, usage, finish);
        }
    }

    public async Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            _catalog ??= await LoadCatalogAsync(cancellationToken);
        }
        finally
        {
            _catalogLock.Release();
        }
        return _catalog.GetValueOrDefault(model);
    }

    private async Task<Dictionary<string, LlmModelInfo>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        using var http = httpClientFactory.CreateClient(HttpClientName);
        using var doc = JsonDocument.Parse(await http.GetStringAsync("models", cancellationToken));
        var catalog = new Dictionary<string, LlmModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString()!;
            var parameters = m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                ? sp.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                : [];
            catalog[id] = new LlmModelInfo(id, parameters.Contains("structured_outputs"),
                m.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetInt32() : null);
        }
        return catalog;
    }

    private string GetApiKey()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
            return options.Value.ApiKey.Trim();

        var name = options.Value.ApiKeyEnvironmentVariable;
        var key = Environment.GetEnvironmentVariable(name)
                  ?? (OperatingSystem.IsWindows()
                      ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                      : null);
        return string.IsNullOrWhiteSpace(key)
            ? throw new LlmException(
                $"OpenRouter API key not found: set OpenRouter:ApiKey in appsettings.local.json or the {name} environment variable.",
                isFatal: true)
            : key.Trim();
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var msg))
                return msg.GetString() ?? body;
        }
        catch (JsonException)
        {
        }
        return body.Length > 500 ? body[..500] : body;
    }
}
