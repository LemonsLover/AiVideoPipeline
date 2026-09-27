using Highlights.Core.Configuration;
using Highlights.Core.Models;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Transcription;

/// <summary>Resolves ggml model files and downloads missing ones.</summary>
public sealed class WhisperModelManager(
    ModelDownloader downloader, IOptions<WhisperOptions> whisper, IOptions<ModelsOptions> models)
{
    /// <summary>"large-v3-turbo" → &lt;models dir&gt;\ggml-large-v3-turbo.bin; a *.bin path is used as is.</summary>
    public string ResolvePath(string model) =>
        IsExplicitPath(model)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(model))
            : Path.Combine(models.Value.EffectiveDirectory, $"ggml-{model}.bin");

    /// <summary>Returns the local model path, downloading the model first if needed.</summary>
    public async Task<string> EnsureAsync(string model, IProgress<StageProgress> progress, string stage,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(model);
        if (File.Exists(path))
            return path;
        if (IsExplicitPath(model))
            throw new PipelineException($"Whisper model file not found: {path}");

        var url = new Uri(new Uri(whisper.Value.ModelBaseUrl), Path.GetFileName(path));
        try
        {
            await downloader.DownloadAsync(url, path, progress, stage, cancellationToken);
        }
        catch (PipelineException ex)
        {
            throw new PipelineException($"Unknown Whisper model '{model}'. {ex.Message}", ex);
        }
        return path;
    }

    private static bool IsExplicitPath(string model) =>
        model.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
        || model.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
}
