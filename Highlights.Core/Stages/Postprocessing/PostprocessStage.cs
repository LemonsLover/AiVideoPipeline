using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Turns moments.json into clips.json: which source ranges go into the video, in order.</summary>
public sealed class PostprocessStage(IOptions<PostprocessOptions> options) : IPipelineStage
{
    public string Name => StageNames.Postprocess;
    public IReadOnlyList<string> DependsOn => [StageNames.Analyze];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.ClipsFile];

    public string DescribeInputs(HighlightsProject project) => Settings(project).ToString();

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var settings = Settings(project);
        var moments = await JsonDefaults.ReadAsync<MomentsDocument>(project.PathOf(ProjectLayout.MomentsFile), cancellationToken);
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var words = transcript.Segments.SelectMany(s => s.Words ?? []).OrderBy(w => w.Start).ToList();
        var duration = project.Media?.DurationSeconds ?? transcript.DurationSeconds;

        var clips = ClipPlanner.Plan(moments.Moments, words, duration, settings);
        var total = Math.Round(clips.Sum(c => c.Duration), 1);
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.ClipsFile), new ClipsDocument
        {
            MinScore = settings.MinScore,
            TargetMinutes = settings.TargetMinutes,
            TotalSeconds = total,
            Clips = clips,
        }, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{clips.Count} clips, {TimeSpan.FromSeconds(total):m\\:ss}"));
        return new Dictionary<string, string>
        {
            ["clips"] = clips.Count.ToString(CultureInfo.InvariantCulture),
            ["totalSeconds"] = total.ToString(CultureInfo.InvariantCulture),
            ["moments"] = clips.Sum(c => c.MomentIds.Count).ToString(CultureInfo.InvariantCulture),
        };
    }

    private ClipPlanner.Settings Settings(HighlightsProject project) =>
        ClipPlanner.From(options.Value, project.Settings.MinScore, project.Settings.TargetMinutes);
}
