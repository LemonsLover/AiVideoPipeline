using System.Security.Cryptography;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Review;

/// <summary>Loads/saves review.json and applies it to planned clips. Shared by the CLI and (later) WPF.</summary>
public sealed class ReviewService
{
    public const double MinClipSeconds = 1;

    public static string KeyOf(Clip clip) => clip.Key;

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
            result.Add((clip, edit, edit.Included ? ApplyEdit(clip, edit, videoDuration) : null));
        }
        return result;
    }

    /// <summary>Start/end offsets move the outer edges, i.e. the first segment's start and the last segment's end.</summary>
    public static Clip ApplyEdit(Clip clip, ClipEdit edit, double videoDuration)
    {
        var segments = clip.Segments.ToList();
        var first = segments[0];
        var start = Math.Clamp(first.Start + edit.StartOffset, 0, first.End - MinClipSeconds);
        segments[0] = new TimeRange(Math.Round(start, 2), first.End);

        var last = segments[^1];
        var end = Math.Clamp(last.End + edit.EndOffset, last.Start + MinClipSeconds, videoDuration);
        segments[^1] = new TimeRange(last.Start, Math.Round(end, 2));

        return clip with
        {
            Start = segments[0].Start,
            End = segments[^1].End,
            Segments = segments,
            Title = string.IsNullOrWhiteSpace(edit.Title) ? clip.Title : edit.Title.Trim(),
            Caption = edit.Caption is null ? clip.Caption : edit.Caption.Trim() is { Length: > 0 } c ? c : null,
        };
    }

    /// <summary>Hash of edit.json's clip list only (its createdAt changes on every review run).</summary>
    public static string EditHash(HighlightsProject project)
    {
        var path = project.PathOf(ProjectLayout.EditFile);
        if (!File.Exists(path))
            return "";
        try
        {
            var edit = System.Text.Json.JsonSerializer.Deserialize<EditDocument>(File.ReadAllText(path), JsonDefaults.Options);
            var clips = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(edit?.Clips ?? [], JsonDefaults.Options);
            return Convert.ToHexStringLower(SHA256.HashData(clips))[..16];
        }
        catch (System.Text.Json.JsonException)
        {
            return "unreadable"; // older format: the review stage will rewrite it
        }
    }

    public static string MomentsHash(HighlightsProject project)
    {
        var path = project.PathOf(ProjectLayout.MomentsFile);
        return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16] : "";
    }
}
