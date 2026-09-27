using Highlights.Core.Configuration;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Transcription;

namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Pure clip planning: threshold → padding → word snapping → merging → target length selection.</summary>
internal static class ClipPlanner
{
    public sealed record Settings(
        int MinScore, double? TargetMinutes, double PrePadding, double PostPadding, double MergeGap, double MaxSnap);

    public static Settings From(PostprocessOptions o, int? minScore, double? targetMinutes) => new(
        minScore ?? o.MinScore, targetMinutes ?? o.TargetMinutes, o.PrePaddingSeconds, o.PostPaddingSeconds,
        o.MergeGapSeconds, o.MaxSnapSeconds);

    public static IReadOnlyList<Clip> Plan(
        IReadOnlyList<Moment> moments, IReadOnlyList<TranscriptWord> words, double duration, Settings s)
    {
        // 1-3: candidates with padding, edges moved off the middle of words.
        var candidates = moments
            .Where(m => m.Score >= s.MinScore)
            .Select(m => new Draft(
                SnapStart(Math.Max(0, m.Start - s.PrePadding), words, s.MaxSnap),
                SnapEnd(Math.Min(duration, m.End + s.PostPadding), words, s.MaxSnap),
                [m]))
            .OrderBy(d => d.Start)
            .ToList();

        // 4: merge overlapping / nearly adjacent clips.
        var merged = new List<Draft>();
        foreach (var d in candidates)
        {
            if (merged.Count > 0 && d.Start - merged[^1].End <= s.MergeGap)
                merged[^1] = new Draft(merged[^1].Start, Math.Max(merged[^1].End, d.End), [.. merged[^1].Moments, .. d.Moments]);
            else
                merged.Add(d);
        }

        // 5: best clips first until the target is reached (skipping ones that don't fit), then chronological.
        var selected = merged;
        if (s.TargetMinutes is { } target and > 0)
        {
            var budget = target * 60;
            selected = [];
            foreach (var d in merged.OrderByDescending(d => d.Best.Score).ThenByDescending(d => d.Moments.Sum(m => m.Score)))
            {
                if (selected.Sum(x => x.Duration) + d.Duration > budget)
                    continue;
                selected.Add(d);
            }
        }

        return selected
            .OrderBy(d => d.Start)
            .Select((d, i) => new Clip
            {
                Id = $"c{i + 1:00}",
                Start = Math.Round(d.Start, 2),
                End = Math.Round(d.End, 2),
                Title = d.Best.Title,
                Category = d.Best.Category,
                Score = d.Best.Score,
                MomentIds = d.Moments.Select(m => m.Id).ToList(),
            })
            .ToList();
    }

    /// <summary>If <paramref name="t"/> falls inside a word, move back to the word's start.</summary>
    internal static double SnapStart(double t, IReadOnlyList<TranscriptWord> words, double maxSnap)
    {
        var w = WordAt(t, words);
        return w is not null && t - w.Start <= maxSnap ? w.Start : t;
    }

    /// <summary>If <paramref name="t"/> falls inside a word, move forward to the word's end.</summary>
    internal static double SnapEnd(double t, IReadOnlyList<TranscriptWord> words, double maxSnap)
    {
        var w = WordAt(t, words);
        return w is not null && w.End - t <= maxSnap ? w.End : t;
    }

    private static TranscriptWord? WordAt(double t, IReadOnlyList<TranscriptWord> words)
    {
        // Words are chronological: binary search for the last word starting at or before t.
        int lo = 0, hi = words.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (words[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 && words[found].End > t ? words[found] : null;
    }

    private sealed record Draft(double Start, double End, IReadOnlyList<Moment> Moments)
    {
        public double Duration => End - Start;
        public Moment Best => Moments.MaxBy(m => m.Score)!;
    }
}
