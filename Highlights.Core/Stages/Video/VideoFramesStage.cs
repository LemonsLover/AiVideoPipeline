using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Video;

/// <summary>
/// One decoding pass over the video: a JPEG per second (for the vision passes) and a motion curve at 2 fps
/// (how much the picture changes — fights, running, explosions vs. standing still).
/// </summary>
public sealed class VideoFramesStage(IFfmpegRunner ffmpeg, IOptionsMonitor<VisionOptions> options) : IPipelineStage
{
    private const int MotionFps = 2, MotionWidth = 160, MotionHeight = 90;

    public string Name => StageNames.Frames;
    public IReadOnlyList<string> DependsOn => [];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.MotionFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var video = new FileInfo(project.ResolveVideoPath());
        if (!video.Exists)
            throw new PipelineException($"Video not found: {video.FullName}");
        return string.Join('|', video.Name, video.Length, video.LastWriteTimeUtc.Ticks, options.CurrentValue.FrameWidth, options.CurrentValue.JpegQuality);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        var framesDir = project.PathOf(ProjectLayout.FramesDirectory);
        if (Directory.Exists(framesDir))
            Directory.Delete(framesDir, recursive: true);
        Directory.CreateDirectory(framesDir);
        var raw = project.PathOf("motion.raw");
        var duration = project.Media?.DurationSeconds ?? 0;

        try
        {
            await ffmpeg.RunAsync(
            [
                "-y", "-i", project.ResolveVideoPath(),
                "-filter_complex",
                $"[0:v]split=2[a][b];[a]fps={MotionFps},scale={MotionWidth}:{MotionHeight},format=gray[m];[b]fps=1,scale={o.FrameWidth}:-2[f]",
                "-map", "[m]", "-f", "rawvideo", raw,
                "-map", "[f]", "-q:v", o.JpegQuality.ToString(CultureInfo.InvariantCulture), Path.Combine(framesDir, "%05d.jpg"),
            ], duration, new FractionProgress(progress, Name, 0, 0.95), cancellationToken);

            progress.Report(new StageProgress(Name, 0.97, "computing motion"));
            var motion = ComputeMotion(await File.ReadAllBytesAsync(raw, cancellationToken));
            var frames = Directory.EnumerateFiles(framesDir, "*.jpg").Count();
            await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.MotionFile),
                new MotionTrack(MotionFps, frames, Median(motion), motion), cancellationToken);

            progress.Report(new StageProgress(Name, 1, $"{frames} frames"));
            return new Dictionary<string, string>
            {
                ["frames"] = frames.ToString(CultureInfo.InvariantCulture),
                ["sizeMb"] = (Directory.EnumerateFiles(framesDir).Sum(f => new FileInfo(f).Length) / 1048576).ToString(CultureInfo.InvariantCulture),
            };
        }
        finally
        {
            File.Delete(raw);
        }
    }

    /// <summary>Mean absolute difference between consecutive grayscale frames, 0..1.</summary>
    private static float[] ComputeMotion(byte[] raw)
    {
        const int size = MotionWidth * MotionHeight;
        var count = raw.Length / size;
        var values = new float[count];
        for (var f = 1; f < count; f++)
        {
            long sum = 0;
            var a = raw.AsSpan((f - 1) * size, size);
            var b = raw.AsSpan(f * size, size);
            for (var i = 0; i < size; i++)
                sum += Math.Abs(a[i] - b[i]);
            values[f] = (float)Math.Round(sum / (size * 255.0), 4);
        }
        return values;
    }

    private static float Median(float[] values)
    {
        if (values.Length == 0)
            return 0;
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private sealed class FractionProgress(IProgress<StageProgress> inner, string stage, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(new StageProgress(stage, from + (to - from) * value, "extracting frames"));
    }
}

/// <summary>Contents of motion.json. Value i covers time i / Fps.</summary>
public sealed record MotionTrack(int Fps, int FrameCount, float Median, float[] Values)
{
    /// <summary>Stretches with strong on-screen motion (Values above factor × median), merged across short gaps.</summary>
    public IReadOnlyList<TimeRange> ActionRanges(double factor)
    {
        var threshold = Math.Max(0.01, Median * factor);
        var ranges = new List<TimeRange>();
        for (var i = 0; i < Values.Length; i++)
        {
            if (Values[i] < threshold)
                continue;
            var start = (i - 1.0) / Fps;
            var end = (i + 1.0) / Fps;
            if (ranges.Count > 0 && start - ranges[^1].End <= 1)
                ranges[^1] = new TimeRange(ranges[^1].Start, end);
            else
                ranges.Add(new TimeRange(Math.Max(0, start), end));
        }
        return ranges;
    }
}

/// <summary>Frames extracted by <see cref="VideoFramesStage"/>: frame n (1-based) shows second n − 1.</summary>
public static class FrameStore
{
    public static string? PathAt(HighlightsProject project, double seconds)
    {
        var path = Path.Combine(project.PathOf(ProjectLayout.FramesDirectory), $"{(int)Math.Round(seconds) + 1:00000}.jpg");
        return File.Exists(path) ? path : null;
    }
}
