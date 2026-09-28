using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Planning;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Highlights.Core.Stages.Video;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Turns plan.json into clips.json: exact source segments per clip (padding, snapping, silence trimming).</summary>
public sealed class PostprocessStage(EditModeStore modes, IOptions<PostprocessOptions> options, IOptions<VisionOptions> vision) : IPipelineStage
{
    public string Name => StageNames.Postprocess;
    public IReadOnlyList<string> DependsOn => [StageNames.Refine];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.ClipsFile];

    public string DescribeInputs(HighlightsProject project) =>
        ClipPlanner.From(modes.Load(modes.ResolveId(project)), options.Value) + "|" + vision.Value.ActionMotionFactor;

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var mode = modes.Load(modes.ResolveId(project));
        var plan = await JsonDefaults.ReadAsync<PlanDocument>(project.PathOf(ProjectLayout.PlanFile), cancellationToken);
        var moments = await JsonDefaults.ReadAsync<MomentsDocument>(project.PathOf(ProjectLayout.MomentsFile), cancellationToken);
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var signals = await JsonDefaults.ReadAsync<AudioSignals>(project.PathOf(ProjectLayout.SignalsFile), cancellationToken);
        var words = transcript.Segments.SelectMany(s => s.Words ?? []).OrderBy(w => w.Start).ToList();
        var duration = project.Media?.DurationSeconds ?? transcript.DurationSeconds;

        var refine = await JsonDefaults.ReadAsync<RefineDocument>(project.PathOf(ProjectLayout.RefineFile), cancellationToken);
        var onScreen = await OnScreenActivityAsync(project, cancellationToken);
        var clips = ClipPlanner.Build(plan, moments.Moments.ToDictionary(m => m.Id), words, signals.Events, refine.Clips,
            onScreen, duration, ClipPlanner.From(mode, options.Value));
        var total = Math.Round(clips.Sum(c => c.Duration), 1);
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.ClipsFile), new ClipsDocument
        {
            Mode = plan.Mode,
            TargetMinutes = plan.TargetMinutes,
            TotalSeconds = total,
            Clips = clips,
        }, cancellationToken);

        var trimmed = Math.Round(plan.PlannedSeconds - total, 1);
        progress.Report(new StageProgress(Name, 1, $"{clips.Count} clips, {TimeSpan.FromSeconds(total):m\\:ss}"));
        return new Dictionary<string, string>
        {
            ["clips"] = clips.Count.ToString(CultureInfo.InvariantCulture),
            ["segments"] = clips.Sum(c => c.Segments.Count).ToString(CultureInfo.InvariantCulture),
            ["totalSeconds"] = total.ToString(CultureInfo.InvariantCulture),
            ["trimmedSeconds"] = trimmed.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Strong motion (fights, running) and ±2 s around events the vision model saw.</summary>
    private async Task<IReadOnlyList<TimeRange>> OnScreenActivityAsync(HighlightsProject project, CancellationToken cancellationToken)
    {
        var ranges = new List<TimeRange>();
        if (File.Exists(project.PathOf(ProjectLayout.MotionFile)))
            ranges.AddRange((await JsonDefaults.ReadAsync<MotionTrack>(project.PathOf(ProjectLayout.MotionFile), cancellationToken))
                .ActionRanges(vision.Value.ActionMotionFactor));
        if (await Analysis.TimelineBuilder.LoadVisionAsync(project, cancellationToken) is { Enabled: true } v)
            ranges.AddRange(v.Events.Where(e => e.Importance >= 3).Select(e => new TimeRange(Math.Max(0, e.Time - 2), e.Time + 2)));
        return ranges;
    }
}
