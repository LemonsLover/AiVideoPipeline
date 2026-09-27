using System.Globalization;
using System.Text;
using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Review;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Rendering;

/// <summary>Renders edit.json into highlights.mp4 (1080p, crossfades, loudness normalization, chapters).</summary>
public sealed class RenderStage(IFfmpegRunner ffmpeg, IOptions<RenderOptions> options, ILogger<RenderStage> logger) : IPipelineStage
{
    private const string WorkDirectory = "render.tmp";
    private bool? _nvencAvailable;

    public string Name => StageNames.Render;
    public IReadOnlyList<string> DependsOn => [StageNames.Review];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.VideoOutputFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        return string.Join('|', ReviewService.EditHash(project), o.Width, o.Height, FrameRate(project), o.CrossfadeSeconds,
            o.InnerCrossfadeSeconds, o.Encoder, o.NvencPreset, o.NvencCq, o.X264Preset, o.X264Crf, o.AudioBitrate, o.LoudnessLufs,
            o.TruePeakDb, string.Join(',', o.EffectiveAudioTracks), Titles(project), o.TitleSeconds, o.TitleFont, o.TitleFontSize,
            o.CaptionSeconds, o.CaptionFontSize);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var edit = await JsonDefaults.ReadAsync<EditDocument>(project.PathOf(ProjectLayout.EditFile), cancellationToken);
        var plan = RenderPlan.Create(edit.Clips, o.CrossfadeSeconds, o.InnerCrossfadeSeconds);
        var trackCount = project.Media?.AudioTracks.Count ?? 0;
        var missing = o.EffectiveAudioTracks.Where(t => t < 0 || t >= trackCount).ToList();
        if (missing.Count > 0)
            throw new PipelineException($"Render:AudioTracks: track(s) {string.Join(", ", missing)} don't exist (the video has {trackCount}).");

        var work = project.PathOf(WorkDirectory);
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var output = project.PathOf(ProjectLayout.VideoOutputFile);
        var tmpOutput = Path.Combine(work, "out.mp4");
        (double Before, double After)? loudness = null;

        try
        {
            // Captions and the font are referenced by relative paths (ffmpeg runs in the work directory),
            // which avoids escaping drive-letter colons inside the filtergraph.
            var titles = Titles(project);
            var titleFiles = new string?[plan.Clips.Count];
            var captionFiles = new string?[plan.Clips.Count];
            for (var i = 0; i < plan.Clips.Count; i++)
            {
                if (titles)
                    titleFiles[i] = await WriteTextAsync(work, $"title{i:000}.txt", plan.Clips[i].Title, cancellationToken);
                if (plan.Clips[i].Caption is { Length: > 0 } caption)
                    captionFiles[i] = await WriteTextAsync(work, $"caption{i:000}.txt", caption, cancellationToken);
            }

            string? font = null;
            if (titleFiles.Any(f => f is not null) || captionFiles.Any(f => f is not null))
            {
                if (!File.Exists(o.TitleFont))
                    throw new PipelineException($"Caption font not found: {o.TitleFont} (Render:TitleFont).");
                font = "font" + Path.GetExtension(o.TitleFont);
                File.Copy(o.TitleFont, Path.Combine(work, font));
            }

            var graph = FilterGraphBuilder.Build(plan, new FilterGraphBuilder.Settings(
                o.Width, o.Height, FrameRate(project), o.EffectiveAudioTracks, titleFiles, captionFiles, font,
                o.TitleFontSize, o.TitleSeconds, o.CaptionFontSize, o.CaptionSeconds));
            await File.WriteAllTextAsync(Path.Combine(work, "filter.txt"), graph, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(work, "chapters.txt"), ChaptersMetadata(plan), new UTF8Encoding(false), cancellationToken);

            var encoder = await ResolveEncoderAsync(cancellationToken);
            progress.Report(new StageProgress(Name, 0, $"{plan.Clips.Count} clips → {TimeSpan.FromSeconds(plan.TotalSeconds):m\\:ss} ({encoder})"));

            var video = project.ResolveVideoPath();
            var args = new List<string> { "-y" };
            foreach (var clip in plan.Clips)
                args.AddRange(["-ss", F(clip.Start), "-t", F(clip.End - clip.Start), "-i", video]);
            args.AddRange(["-f", "ffmetadata", "-i", "chapters.txt"]);
            args.AddRange(["-/filter_complex", "filter.txt",
                "-map", $"[{FilterGraphBuilder.VideoOut}]", "-map", $"[{FilterGraphBuilder.AudioOut}]",
                "-map_metadata", plan.Clips.Count.ToString(CultureInfo.InvariantCulture),
                "-map_chapters", plan.Clips.Count.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(encoder == "nvenc"
                ? ["-c:v", "h264_nvenc", "-preset", o.NvencPreset, "-tune", "hq", "-rc", "vbr", "-cq", o.NvencCq.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-profile:v", "high"]
                : ["-c:v", "libx264", "-preset", o.X264Preset, "-crf", o.X264Crf.ToString(CultureInfo.InvariantCulture), "-profile:v", "high"]);
            // Pass 1: final video + lossless audio into an intermediate mkv (keeps chapters).
            args.AddRange(["-c:a", "flac", "render.mkv"]);
            await ffmpeg.RunAsync(args, plan.TotalSeconds, new FractionProgress(progress, Name, 0, 0.92), cancellationToken, work);

            // Pass 2: find the loudness filter; pass 3: copy the video, encode AAC once with it. AAC can overshoot
            // on loud transients (yells), so the true peak is checked and the mux redone with more limiter headroom.
            var margin = 0.5;
            for (var attempt = 0; ; attempt++)
            {
                string[] audioFilter = [];
                if (o.LoudnessLufs is { } lufs)
                {
                    progress.Report(new StageProgress(Name, 0.93, "normalizing loudness"));
                    var (filter, before, after) = await LoudnessNormalizer.BuildFilterAsync(
                        ffmpeg, Path.Combine(work, "render.mkv"), lufs, o.TruePeakDb, margin, cancellationToken);
                    logger.LogInformation("Loudness {Before} → {After} LUFS (limiter margin {Margin} dB)", before, after, margin);
                    loudness = (before, after);
                    audioFilter = ["-af", filter];
                }

                progress.Report(new StageProgress(Name, 0.95, "writing mp4"));
                await ffmpeg.RunAsync(
                [
                    "-y", "-i", "render.mkv", "-map", "0:v", "-map", "0:a", "-map_metadata", "0", "-map_chapters", "0",
                    "-c:v", "copy", .. audioFilter, "-c:a", "aac", "-b:a", o.AudioBitrate, "-movflags", "+faststart", "out.mp4",
                ], plan.TotalSeconds, new FractionProgress(progress, Name, 0.95, 1), cancellationToken, work);

                if (o.LoudnessLufs is null || attempt == 2)
                    break;
                var peak = await LoudnessNormalizer.MeasureTruePeakAsync(ffmpeg, tmpOutput, cancellationToken);
                if (peak <= o.TruePeakDb + 0.3)
                    break;
                logger.LogInformation("True peak {Peak} dBTP is above {Target}; redoing audio with more headroom", peak, o.TruePeakDb);
                margin += peak - o.TruePeakDb;
            }

            File.Move(tmpOutput, output, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete {Directory}", work);
            }
        }

        progress.Report(new StageProgress(Name, 1, Path.GetFileName(output)));
        return new Dictionary<string, string>
        {
            ["clips"] = plan.Clips.Count.ToString(CultureInfo.InvariantCulture),
            ["durationSeconds"] = plan.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            ["encoder"] = await ResolveEncoderAsync(cancellationToken),
            ["sizeBytes"] = new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture),
            ["loudness"] = loudness is { } l ? FormattableString.Invariant($"{l.Before:0.#} -> {l.After:0.#} LUFS") : "not normalized",
        };
    }

    private bool Titles(HighlightsProject project) => project.Settings.Titles ?? options.Value.Titles;

    private double FrameRate(HighlightsProject project) =>
        options.Value.FrameRate ?? Math.Round(Math.Min(60, project.Media?.Video?.FrameRate ?? 30), 3);

    private async Task<string> ResolveEncoderAsync(CancellationToken cancellationToken)
    {
        var mode = options.Value.Encoder.ToLowerInvariant();
        if (mode is "x264" or "libx264")
            return "x264";

        // A tiny test encode: NVENC can be present in ffmpeg but unusable (no NVIDIA GPU, outdated driver).
        _nvencAvailable ??= await ffmpeg.TryRunAsync(
            ["-f", "lavfi", "-i", "color=c=black:s=320x240:d=0.2", "-frames:v", "3", "-c:v", "h264_nvenc", "-f", "null", "-"],
            cancellationToken);
        if (_nvencAvailable == true)
            return "nvenc";
        if (mode == "nvenc")
            throw new PipelineException("NVENC is not available (update the NVIDIA driver) — or set Render:Encoder to \"auto\" or \"x264\".");
        logger.LogWarning("NVENC is not available, falling back to libx264");
        return "x264";
    }

    /// <summary>ffmetadata with one chapter per clip (also embedded in the mp4).</summary>
    internal static string ChaptersMetadata(RenderPlan plan)
    {
        static string Escape(string s) => new StringBuilder(s)
            .Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ").ToString();

        var sb = new StringBuilder(";FFMETADATA1\n");
        var starts = plan.ClipStarts;
        for (var i = 0; i < plan.Clips.Count; i++)
        {
            var start = (long)(starts[i] * 1000);
            var end = (long)((i + 1 < plan.Clips.Count ? starts[i + 1] : plan.TotalSeconds) * 1000);
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
              .Append(CultureInfo.InvariantCulture, $"START={start}\nEND={end}\n")
              .Append("title=").Append(Escape(plan.Clips[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static async Task<string> WriteTextAsync(string directory, string name, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, name), Wrap(text, MaxLineChars), new UTF8Encoding(false), cancellationToken);
        return name;
    }

    /// <summary>drawtext doesn't wrap: ~55 characters of 44 px bold text is about 2/3 of a 1080p frame.</summary>
    private const int MaxLineChars = 55;

    /// <summary>Breaks text into lines of at most <paramref name="max"/> characters at word boundaries, balancing two lines.</summary>
    internal static string Wrap(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Aim for lines of similar length rather than a long first line and a stub.
        var lines = (int)Math.Ceiling(text.Length / (double)max);
        var target = Math.Min(max, (int)Math.Ceiling(text.Length / (double)lines) + 4);
        var sb = new StringBuilder();
        var line = 0;
        foreach (var word in words)
        {
            if (line > 0 && line + 1 + word.Length > target)
            {
                sb.Append('\n');
                line = 0;
            }
            else if (line > 0)
            {
                sb.Append(' ');
                line++;
            }
            sb.Append(word);
            line += word.Length;
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Maps a step's 0..1 progress into the [from, to] part of the stage.</summary>
    private sealed class FractionProgress(IProgress<StageProgress> inner, string stage, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(new StageProgress(stage, from + (to - from) * value));
    }
}
