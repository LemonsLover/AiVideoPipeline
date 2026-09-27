using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Planning;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;

namespace Highlights.Core.Stages.Postprocessing;

/// <summary>
/// Deterministic clip building from the LLM plan: padding → plan cuts → word snapping → silence trimming
/// (jump cuts over pauses without audio events) → merging clips that ended up adjacent.
/// </summary>
internal static class ClipPlanner
{
    public sealed record Settings(
        double PrePadding, double PostPadding, double MaxSilence, double KeepSilence,
        double MergeGap, double MaxSnap, double MinSegment);

    public static Settings From(EditMode mode, Configuration.PostprocessOptions o) => new(
        mode.PrePaddingSeconds, mode.PostPaddingSeconds, mode.MaxSilenceSeconds, mode.KeepSilenceSeconds,
        o.MergeGapSeconds, o.MaxSnapSeconds, o.MinSegmentSeconds);

    public static IReadOnlyList<Clip> Build(
        PlanDocument plan, IReadOnlyDictionary<string, Moment> moments, IReadOnlyList<TranscriptWord> words,
        IReadOnlyList<SignalEvent> events, double duration, Settings s)
    {
        var drafts = new List<Draft>();
        foreach (var p in plan.Clips.OrderBy(c => c.Start))
        {
            var outer = new TimeRange(Math.Max(0, p.Start - s.PrePadding), Math.Min(duration, p.End + s.PostPadding));
            var segments = TimeRange.Subtract(outer, p.Cuts)
                .Select(r => new TimeRange(SnapStart(r.Start, words, s.MaxSnap), SnapEnd(r.End, words, s.MaxSnap)))
                .SelectMany(r => TrimSilence(r, words, events, s))
                .Where(r => r.Duration >= s.MinSegment)
                .ToList();
            if (segments.Count == 0)
                continue;

            var beats = p.MomentIds.Where(moments.ContainsKey).Select(id => moments[id]).ToList();
            var best = beats.MaxBy(b => b.Score);
            var draft = new Draft(segments, p.Title, p.Caption, best?.Category ?? "story", best?.Score ?? 0,
                p.MomentIds, p.MomentIds.FirstOrDefault() ?? p.Id);

            // Padding can make neighbouring clips touch or overlap: then they are one continuous clip.
            if (drafts.Count > 0 && draft.Segments[0].Start - drafts[^1].Segments[^1].End <= s.MergeGap)
                drafts[^1] = Merge(drafts[^1], draft);
            else
                drafts.Add(draft);
        }

        return drafts.Select((d, i) => new Clip
        {
            Id = $"c{i + 1:00}",
            Start = Math.Round(d.Segments[0].Start, 2),
            End = Math.Round(d.Segments[^1].End, 2),
            Segments = d.Segments.Select(r => new TimeRange(Math.Round(r.Start, 2), Math.Round(r.End, 2))).ToList(),
            Title = d.Title,
            Caption = d.Caption,
            Category = d.Category,
            Score = d.Score,
            MomentIds = d.MomentIds,
            Key = d.Key,
        }).ToList();
    }

    /// <summary>
    /// Removes pauses in speech longer than MaxSilence, keeping KeepSilence on each side. Pauses that contain an
    /// audio event (laughter, yelling, a loud game moment) are kept — something is happening there.
    /// </summary>
    internal static IEnumerable<TimeRange> TrimSilence(
        TimeRange range, IReadOnlyList<TranscriptWord> words, IReadOnlyList<SignalEvent> events, Settings s)
    {
        if (s.MaxSilence <= 0)
        {
            yield return range;
            yield break;
        }

        // "Active" = speech or an audio event; everything else is a pause candidate.
        var active = words.Where(w => w.End > range.Start && w.Start < range.End).Select(w => new TimeRange(w.Start, w.End))
            .Concat(events.Where(e => e.End > range.Start && e.Start < range.End).Select(e => new TimeRange(e.Start, e.End)))
            .OrderBy(r => r.Start)
            .ToList();
        if (active.Count == 0)
        {
            yield return range; // no speech at all: leave the plan's decision alone
            yield break;
        }

        var cursor = range.Start;
        var lastActiveEnd = range.Start;
        foreach (var a in active)
        {
            var gap = a.Start - lastActiveEnd;
            if (gap > s.MaxSilence && lastActiveEnd > range.Start)
            {
                var cutFrom = lastActiveEnd + s.KeepSilence;
                var cutTo = a.Start - s.KeepSilence;
                if (cutTo - cutFrom > 0.3)
                {
                    yield return new TimeRange(cursor, cutFrom);
                    cursor = cutTo;
                }
            }
            lastActiveEnd = Math.Max(lastActiveEnd, a.End);
        }
        yield return new TimeRange(cursor, range.End);
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

    private static Draft Merge(Draft a, Draft b)
    {
        var segments = a.Segments.ToList();
        foreach (var seg in b.Segments)
        {
            if (seg.Start <= segments[^1].End + 0.05)
                segments[^1] = new TimeRange(segments[^1].Start, Math.Max(segments[^1].End, seg.End));
            else
                segments.Add(seg);
        }
        var best = b.Score > a.Score ? b : a;
        return new Draft(segments, best.Title, a.Caption ?? b.Caption, best.Category, best.Score,
            [.. a.MomentIds, .. b.MomentIds], a.Key);
    }

    private sealed record Draft(
        IReadOnlyList<TimeRange> Segments, string Title, string? Caption, string Category, int Score,
        IReadOnlyList<string> MomentIds, string Key);
}
