using System.Net;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Models;

/// <summary>Downloads model files with progress reporting; writes to *.part and renames on success.</summary>
public sealed class ModelDownloader(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "models";

    public async Task DownloadAsync(Uri url, string targetPath, IProgress<StageProgress> progress, string stage,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var name = Path.GetFileName(targetPath);
        var part = targetPath + ".part";
        const string step = "download";

        using var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PipelineException($"Model not found at {url}.");
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        progress.Report(new StageProgress(stage, total is null ? null : 0, $"downloading {name}", step));

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
                        $"downloading {name}: {done / 1048576} MB", step));
                }
            }

            File.Move(part, targetPath, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }

        progress.Report(new StageProgress(stage, 1, $"{name} ready", step));
    }
}
