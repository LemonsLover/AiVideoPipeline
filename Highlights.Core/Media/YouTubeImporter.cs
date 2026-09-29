using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using Highlights.Core.Configuration;
using Highlights.Core.Models;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Media;

/// <summary>
/// Downloads a video by link with yt-dlp (fetched on first use and kept up to date) and creates a project for it.
/// </summary>
public sealed partial class YouTubeImporter(
    ToolLocator tools, ModelDownloader downloader, IProjectStore store, IOptionsMonitor<ImportOptions> options,
    ILogger<YouTubeImporter> logger)
{
    public const string Stage = "import";

    private readonly SemaphoreSlim _ytDlpLock = new(1, 1);
    private bool _updatedThisSession;

    /// <summary>Downloads the video (or reuses an earlier download) and opens or creates its project.</summary>
    public async Task<HighlightsProject> ImportAsync(string url, string? directory, IProgress<StageProgress> progress,
        CancellationToken cancellationToken)
    {
        var video = await DownloadAsync(url, directory, progress, cancellationToken);
        var projectFile = Path.Combine(ProjectLayout.ProjectDirectoryFor(video), ProjectLayout.ProjectFile);
        var project = File.Exists(projectFile)
            ? await store.LoadAsync(projectFile, cancellationToken)
            : await store.CreateAsync(video, cancellationToken);
        if (project.SourceUrl != url)
        {
            project.SourceUrl = url;
            await store.SaveAsync(project, cancellationToken);
        }
        return project;
    }

    /// <summary>Downloads the video and returns its path.</summary>
    public async Task<string> DownloadAsync(string url, string? directory, IProgress<StageProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PipelineException($"Not a link: {url}");

        var o = options.CurrentValue;
        var ytDlp = await EnsureYtDlpAsync(progress, cancellationToken);
        var dir = string.IsNullOrWhiteSpace(directory) ? o.EffectiveDownloadDirectory : Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);

        var args = new List<string>
        {
            "--no-playlist", "--newline", "--no-colors", "--no-simulate",
            "-f", o.Format, "--merge-output-format", "mkv",
            "--ffmpeg-location", Path.GetDirectoryName(tools.Ffmpeg)!,
            // Title, trimmed, plus the id: stable name, so a second import of the same link reuses the file.
            "-o", Path.Combine(dir, "%(title).100B [%(id)s].%(ext)s"),
            "--progress-template", "download:[dl] %(progress._percent_str)s %(progress._total_bytes_str)s %(progress._speed_str)s %(info.format_id)s",
            "--print", "video:ID=%(id)s",
            "--print", "after_move:FILE=%(filepath)s",
        };
        if (!string.IsNullOrWhiteSpace(o.CookiesFromBrowser))
            args.AddRange(["--cookies-from-browser", o.CookiesFromBrowser]);
        args.Add(uri.ToString());

        string? id = null, file = null;
        var part = 0;
        var lastPercent = 0.0;
        var stderr = new StringBuilder();
        progress.Report(new StageProgress(Stage, null, "reading video info"));

        void OnLine(string line)
        {
            if (line.StartsWith("ID=", StringComparison.Ordinal))
                id = line[3..].Trim();
            else if (line.StartsWith("FILE=", StringComparison.Ordinal))
                file = line[5..].Trim();
            else if (PercentRegex().Match(line) is { Success: true } m
                     && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                // Video and audio download one after another; a drop in percent means the next part started.
                if (pct + 20 < lastPercent)
                    part++;
                lastPercent = pct;
                var what = part == 0 ? "video" : "audio";
                progress.Report(new StageProgress(Stage, pct / 100, $"downloading {what} {line[4..].Trim()}"));
            }
            else if (line.StartsWith("[Merger]", StringComparison.Ordinal))
                progress.Report(new StageProgress(Stage, null, "merging video and audio"));
        }

        var result = await Cli.Wrap(ytDlp)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(OnLine, Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr, Encoding.UTF8))
            .WithEnvironmentVariables(e => e.Set("PYTHONIOENCODING", "utf-8"))
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            var error = string.Join('\n', stderr.ToString().Split('\n').Where(l => l.Contains("ERROR", StringComparison.Ordinal)).TakeLast(3));
            var hint = error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || error.Contains("Private video", StringComparison.OrdinalIgnoreCase)
                ? " For private videos set Import:CookiesFromBrowser (e.g. \"chrome\") in your settings; unlisted links work without it."
                : "";
            throw new PipelineException($"yt-dlp failed: {(error.Length > 0 ? error : stderr.ToString().Trim())}{hint}");
        }

        // An earlier download of the same link is reused by yt-dlp and then no "after_move" line is printed.
        file ??= id is null ? null : Directory.EnumerateFiles(dir, $"*[{id}].*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal));
        if (file is null || !File.Exists(file))
            throw new PipelineException($"yt-dlp finished but the downloaded file wasn't found in {dir}.");

        logger.LogInformation("Downloaded {Url} to {File}", url, file);
        progress.Report(new StageProgress(Stage, 1, Path.GetFileName(file)));
        return file;
    }

    /// <summary>yt-dlp from settings or PATH; otherwise a managed copy that is downloaded and kept up to date.</summary>
    private async Task<string> EnsureYtDlpAsync(IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        await _ytDlpLock.WaitAsync(cancellationToken);
        try
        {
            if (tools.TryFind("yt-dlp", options.CurrentValue.YtDlpPath) is { } existing)
                return existing;

            var managed = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
            var legacy = Path.Combine(HighlightsConfiguration.UserDirectory, "tools", "yt-dlp.exe");
            if (!File.Exists(managed) && File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
                File.Move(legacy, managed);
            }
            if (!File.Exists(managed))
            {
                await downloader.DownloadAsync(new Uri(options.CurrentValue.YtDlpUrl), managed, progress, Stage, cancellationToken);
                _updatedThisSession = true;
            }
            else if (options.CurrentValue.AutoUpdate && !_updatedThisSession)
            {
                progress.Report(new StageProgress(Stage, null, "updating yt-dlp"));
                var update = await Cli.Wrap(managed).WithArguments(["-U"]).WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);
                if (update.ExitCode != 0)
                    logger.LogWarning("yt-dlp -U exited with {Code}; continuing with the current version", update.ExitCode);
                _updatedThisSession = true;
            }
            return managed;
        }
        finally
        {
            _ytDlpLock.Release();
        }
    }

    [GeneratedRegex(@"^\[dl\]\s+([\d.]+)%")]
    private static partial Regex PercentRegex();
}
