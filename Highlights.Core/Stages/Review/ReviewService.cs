using System.Security.Cryptography;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Review;

/// <summary>Loads/saves review.json and applies it to planned clips. Shared by the CLI and (later) WPF.</summary>
public sealed class ReviewService
{
    public const double MinClipSeconds = 1;

    public static string KeyOf(Clip clip) => clip.MomentIds[0];

    public async Task<ReviewDocument> LoadAsync(HighlightsProject project, CancellationToken cancellationToken = default)
    {
        var path = project.PathOf(ProjectLayout.ReviewFile);
        var review = File.Exists(path) ? await JsonDefaults.ReadAsync<ReviewDocument>(path, cancellationToken) : new ReviewDocument();
        if (review.MomentsHash != MomentsHash(project))
            review = new ReviewDocument { MomentsHash = MomentsHash(project) }; // made for other moments: start over
        return review;
    }

    public Task SaveAsync(HighlightsProject project, ReviewDocument review, CancellationToken cancellationToken = default)
    {
        review.MomentsHash = MomentsHash(project);
        foreach (var key in review.Clips.Where(c => c.Value.IsDefault).Select(c => c.Key).ToList())
            review.Clips.Remove(key);
        return JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.ReviewFile), review, cancellationToken);
    }

    /// <summary>Applies edits; returns every clip with its edit and the resulting clip (null when excluded).</summary>
    public static IReadOnlyList<(Clip Planned, ClipEdit Edit, Clip? Final)> Apply(
        IReadOnlyList<Clip> clips, ReviewDocument review, double videoDuration)
    {
        var result = new List<(Clip, ClipEdit, Clip?)>();
        foreach (var clip in clips)
        {
            var edit = review.Clips.GetValueOrDefault(KeyOf(clip)) ?? new ClipEdit();
            Clip? final = null;
            if (edit.Included)
            {
                var start = Math.Clamp(clip.Start + edit.StartOffset, 0, videoDuration);
                var end = Math.Clamp(clip.End + edit.EndOffset, 0, videoDuration);
                if (end - start < MinClipSeconds)
                    end = Math.Min(videoDuration, start + MinClipSeconds);
                final = clip with
                {
                    Start = Math.Round(start, 2),
                    End = Math.Round(end, 2),
                    Title = string.IsNullOrWhiteSpace(edit.Title) ? clip.Title : edit.Title.Trim(),
                };
            }
            result.Add((clip, edit, final));
        }
        return result;
    }

    public static string MomentsHash(HighlightsProject project)
    {
        var path = project.PathOf(ProjectLayout.MomentsFile);
        return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16] : "";
    }
}
