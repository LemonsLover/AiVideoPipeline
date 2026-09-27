using System.Globalization;
using System.Security.Cryptography;
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
        var edit = project.PathOf(ProjectLayout.EditFile);
        var editHash = File.Exists(edit) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(edit)))[..16] : "";
        return string.Join('|', editHash, o.Width, o.Height, FrameRate(project), o.CrossfadeSeconds, o.Encoder, o.NvencPreset,
            o.NvencCq, o.X264Preset, o.X264Crf, o.AudioBitrate, o.LoudnessLufs, o.TruePeakDb,
            string.Join(',', o.EffectiveAudioTracks), Titles(project), o.TitleSeconds, o.TitleFont, o.TitleFontSize);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var edit = await JsonDefaults.ReadAsync<EditDocument>(project.PathOf(ProjectLayout.EditFile), cancellationToken);
        var plan = RenderPlan.Create(edit.Clips, o.CrossfadeSeconds);
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
            string? font = null;
            var titleFiles = new string?[plan.Clips.Count];
            if (titles)
            {
                if (!File.Exists(o.TitleFont))
                    throw new PipelineException($"Title font not found: {o.TitleFont} (Render:TitleFont).");
                font = "font" + Path.GetExtension(o.TitleFont);
                File.Copy(o.TitleFont, Path.Combine(work, font));
                for (var i = 0; i < plan.Clips.Count; i++)
                {
                    titleFiles[i] = $"title{i:000}.txt";
                    await File.WriteAllTextAsync(Path.Combine(work, titleFiles[i]!), plan.Clips[i].Title, new UTF8Encoding(false), cancellationToken);
                }
            }

            var graph = FilterGraphBuilder.Build(plan, new FilterGraphBuilder.Settings(
                o.Width, o.Height, FrameRate(project), o.EffectiveAudioTracks, titleFiles, font, o.TitleFontSize, o.TitleSeconds));
            await File.WriteAllTextAsync(Path.Combine(work, "filter.txt"), graph, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(work, "chapters.txt"), ChaptersMetadata(plan), new UTF8Encoding(false), cancellationToken);

            var encoder = await ResolveEncoderAsync(cancellationToken);
            progress.Report(new StageProgress(Name, 0, $"{plan.Clips.Count} clips → {TimeSpan.FromSeconds(plan.TotalSeconds):m\\:ss} ({encoder})"));

            var video = project.ResolveVideoPath();
            var args = new List<string> { "-y" };
            foreach (var clip in plan.Clips)
                args.AddRange(["-ss", F(clip.Start), "-t", F(clip.Duration), "-i", video]);
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

            // Pass 2: measure loudness; pass 3: copy the video, encode AAC once with the loudness correction.
            string[] audioFilter = [];
            if (o.LoudnessLufs is { } lufs)
            {
                progress.Report(new StageProgress(Name, 0.93, "normalizing loudness"));
                var (filter, before, after) = await LoudnessNormalizer.BuildFilterAsync(
                    ffmpeg, Path.Combine(work, "render.mkv"), lufs, o.TruePeakDb, cancellationToken);
                logger.LogInformation("Loudness {Before} → {After} LUFS", before, after);
                loudness = (before, after);
                audioFilter = ["-af", filter];
            }

            progress.Report(new StageProgress(Name, 0.95, "writing mp4"));
            await ffmpeg.RunAsync(
            [
                "-y", "-i", "render.mkv", "-map", "0:v", "-map", "0:a", "-map_metadata", "0", "-map_chapters", "0",
                "-c:v", "copy", .. audioFilter, "-c:a", "aac", "-b:a", o.AudioBitrate, "-movflags", "+faststart", "out.mp4",
            ], plan.TotalSeconds, new FractionProgress(progress, Name, 0.95, 1), cancellationToken, work);

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
        for (var i = 0; i < plan.Clips.Count; i++)
        {
            var start = (long)(plan.OutputStarts[i] * 1000);
            var end = (long)((i + 1 < plan.Clips.Count ? plan.OutputStarts[i + 1] : plan.TotalSeconds) * 1000);
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
              .Append(CultureInfo.InvariantCulture, $"START={start}\nEND={end}\n")
              .Append("title=").Append(Escape(plan.Clips[i].Title)).Append('\n');
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
