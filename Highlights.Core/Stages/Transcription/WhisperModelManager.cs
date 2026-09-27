using System.Net;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Transcription;

/// <summary>Resolves ggml model files and downloads missing ones.</summary>
public sealed class WhisperModelManager(IHttpClientFactory httpClientFactory, IOptions<WhisperOptions> options)
{
    public const string HttpClientName = "whisper-models";

    /// <summary>"large-v3-turbo" → &lt;models dir&gt;\ggml-large-v3-turbo.bin; a *.bin path is used as is.</summary>
    public string ResolvePath(string model) =>
        IsExplicitPath(model)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(model))
            : Path.Combine(options.Value.EffectiveModelsDirectory, $"ggml-{model}.bin");

    /// <summary>Returns the local model path, downloading the model first if needed.</summary>
    public async Task<string> EnsureAsync(string model, IProgress<StageProgress> progress, string stage,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(model);
        if (File.Exists(path))
            return path;
        if (IsExplicitPath(model))
            throw new PipelineException($"Whisper model file not found: {path}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var url = new Uri(new Uri(options.Value.ModelBaseUrl), Path.GetFileName(path));
        var part = path + ".part";
        const string step = "model";

        using var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PipelineException($"Unknown Whisper model '{model}' ({url} not found).");
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        progress.Report(new StageProgress(stage, total is null ? null : 0, $"downloading {Path.GetFileName(path)}", step));

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    progress.Report(new StageProgress(stage, total > 0 ? (double)done / total.Value : null,
                        $"downloading {Path.GetFileName(path)}: {done / 1048576} MB", step));
                }
            }

            File.Move(part, path, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }

        progress.Report(new StageProgress(stage, 1, "model ready", step));
        return path;
    }

    private static bool IsExplicitPath(string model) =>
        model.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
        || model.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
}
