using Highlights.Core.Llm;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core;

public sealed record CheckResult(string Text, bool Ok);

/// <summary>
/// Startup diagnostics: which settings file is used, whether ffmpeg/ffprobe are found and an API key is set
/// (never the key itself). Makes "works in the CLI but not in the app" problems visible.
/// </summary>
public sealed class EnvironmentCheck(ToolLocator tools, IOptionsMonitor<OpenRouterOptions> openRouter)
{
    public IReadOnlyList<CheckResult> Run()
    {
        var results = new List<CheckResult>();
        var settings = HighlightsConfiguration.UserSettingsPath;
        results.Add(File.Exists(settings)
            ? new CheckResult($"Settings: {settings}", true)
            : new CheckResult($"Settings file missing: {settings}", false));

        foreach (var (name, resolve) in new (string, Func<string>)[] { ("ffmpeg", () => tools.Ffmpeg), ("ffprobe", () => tools.Ffprobe) })
        {
            try
            {
                results.Add(new CheckResult($"{name}: {resolve()}", true));
            }
            catch (PipelineException ex)
            {
                results.Add(new CheckResult(ex.Message, false));
            }
        }

        var o = openRouter.CurrentValue;
        results.Add(OpenRouterClient.FindApiKey(o) is { } key
            ? new CheckResult($"OpenRouter key: set ({(string.IsNullOrWhiteSpace(o.ApiKey) ? o.ApiKeyEnvironmentVariable : "settings file")}, …{key[^4..]})", true)
            : new CheckResult($"OpenRouter key: not set — add OpenRouter:ApiKey to {settings}", false));
        return results;
    }
}
