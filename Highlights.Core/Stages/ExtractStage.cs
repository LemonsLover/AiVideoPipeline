using System.Globalization;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;

namespace Highlights.Core.Stages;

/// <summary>Extracts the selected voice track to audio.wav (16 kHz, mono, PCM s16le) for Whisper.</summary>
public sealed class ExtractStage(IFfmpegRunner ffmpeg) : IPipelineStage
{
    public const int SampleRate = 16_000;

    public string Name => StageNames.Extract;
    public IReadOnlyList<string> DependsOn => [];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.AudioFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var track = GetTrack(project);
        var video = new FileInfo(project.ResolveVideoPath());
        if (!video.Exists)
            throw new PipelineException($"Video not found: {video.FullName}");
        return string.Join('|', video.Name, video.Length, video.LastWriteTimeUtc.Ticks, track.StreamIndex, SampleRate);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var track = GetTrack(project);
        var output = project.PathOf(ProjectLayout.AudioFile);
        var tmp = output + ".tmp";
        var duration = track.DurationSeconds ?? project.Media!.DurationSeconds;

        progress.Report(new StageProgress(Name, 0, $"track {track.Index}: {track.Title ?? track.Codec}"));
        try
        {
            await ffmpeg.RunAsync(
            [
                "-y", "-i", project.ResolveVideoPath(),
                "-map", $"0:a:{track.Index}", "-vn", "-sn", "-dn",
                "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le",
                "-f", "wav", tmp,
            ], duration, new Progress(progress, Name), cancellationToken);

            File.Move(tmp, output, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }

        progress.Report(new StageProgress(Name, 1, "done"));
        return new Dictionary<string, string>
        {
            ["track"] = track.Index.ToString(CultureInfo.InvariantCulture),
            ["sizeBytes"] = new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static AudioTrackInfo GetTrack(HighlightsProject project)
    {
        var tracks = project.Media?.AudioTracks ?? [];
        if (project.Settings.AudioTrack is not { } index)
            throw new PipelineException("No voice track selected. List them with 'highlights tracks' and pick one with --track.");
        if (index < 0 || index >= tracks.Count)
            throw new PipelineException($"Audio track {index} does not exist (the video has {tracks.Count}).");
        return tracks[index];
    }

    private sealed class Progress(IProgress<StageProgress> inner, string stage) : IProgress<double>
    {
        public void Report(double value) => inner.Report(new StageProgress(stage, value));
    }
}
