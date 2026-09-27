using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Planning;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Turns plan.json into clips.json: exact source segments per clip (padding, snapping, silence trimming).</summary>
public sealed class PostprocessStage(EditModeStore modes, IOptions<PostprocessOptions> options) : IPipelineStage
{
    public string Name => StageNames.Postprocess;
    public IReadOnlyList<string> DependsOn => [StageNames.Plan];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.ClipsFile];

    public string DescribeInputs(HighlightsProject project) =>
        ClipPlanner.From(modes.Load(modes.ResolveId(project)), options.Value).ToString();

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

        var clips = ClipPlanner.Build(plan, moments.Moments.ToDictionary(m => m.Id), words, signals.Events, duration,
            ClipPlanner.From(mode, options.Value));
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
}
