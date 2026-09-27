using System.Globalization;
using System.Security.Cryptography;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Review;

/// <summary>Applies the user's review.json to clips.json → edit.json (what gets rendered).</summary>
public sealed class ReviewStage : IPipelineStage
{
    public string Name => StageNames.Review;
    public IReadOnlyList<string> DependsOn => [StageNames.Postprocess];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.EditFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var path = project.PathOf(ProjectLayout.ReviewFile);
        return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16] : "no-review";
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var clips = await JsonDefaults.ReadAsync<ClipsDocument>(project.PathOf(ProjectLayout.ClipsFile), cancellationToken);
        if (clips.Clips.Count == 0)
            throw new PipelineException("The plan produced no clips. Try another mode or a longer target, then re-run the plan.");

        var reviewPath = project.PathOf(ProjectLayout.ReviewFile);
        var review = File.Exists(reviewPath)
            ? await JsonDefaults.ReadAsync<ReviewDocument>(reviewPath, cancellationToken)
            : new ReviewDocument();

        var ignored = review.Clips.Count > 0 && review.MomentsHash != ReviewService.MomentsHash(project);
        if (ignored)
            review = new ReviewDocument();

        var duration = project.Media?.DurationSeconds ?? double.MaxValue;
        var final = ReviewService.Apply(clips.Clips, review, duration)
            .Where(x => x.Final is not null)
            .Select(x => x.Final!)
            .OrderBy(c => c.Start)
            .ToList();
        if (final.Count == 0)
            throw new PipelineException("Every clip is excluded — nothing to render. Include some with 'highlights review'.");

        var total = Math.Round(final.Sum(c => c.Duration), 1);
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.EditFile),
            new EditDocument { TotalSeconds = total, Clips = final, ReviewIgnored = ignored }, cancellationToken);

        progress.Report(new StageProgress(Name, 1, ignored ? "review.json is outdated and was ignored" : $"{final.Count} clips"));
        return new Dictionary<string, string>
        {
            ["clips"] = final.Count.ToString(CultureInfo.InvariantCulture),
            ["totalSeconds"] = total.ToString(CultureInfo.InvariantCulture),
            ["reviewIgnored"] = ignored.ToString(),
        };
    }
}
