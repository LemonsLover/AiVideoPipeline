using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Rendering;

/// <summary>One continuous piece of source video in the output (a clip segment).</summary>
/// <param name="FadeIn">Crossfade with the previous piece (0 for the first piece).</param>
public sealed record RenderPiece(int ClipIndex, int SegmentIndex, TimeRange Source, double FadeIn, double OutputStart);

/// <summary>
/// Where every clip segment lands in the output. Segments of one clip are joined with a short crossfade
/// (jump cut), clips with a longer one; each fade overlaps the neighbouring pieces.
/// </summary>
public sealed record RenderPlan(IReadOnlyList<Clip> Clips, IReadOnlyList<RenderPiece> Pieces, double TotalSeconds)
{
    /// <summary>Output time where each clip starts (chapters).</summary>
    public IReadOnlyList<double> ClipStarts => Clips.Select((_, i) => Pieces.First(p => p.ClipIndex == i).OutputStart).ToList();

    public static RenderPlan Create(IReadOnlyList<Clip> clips, double clipCrossfade, double innerCrossfade)
    {
        var pieces = new List<RenderPiece>();
        var t = 0.0;
        TimeRange? previous = null;
        for (var c = 0; c < clips.Count; c++)
        {
            for (var s = 0; s < clips[c].Segments.Count; s++)
            {
                var source = clips[c].Segments[s];
                var wanted = previous is null ? 0 : s == 0 ? clipCrossfade : innerCrossfade;
                // A fade can't be longer than half of either piece it overlaps.
                var fade = previous is null ? 0 : Math.Round(Math.Max(0, Math.Min(wanted, Math.Min(previous.Duration, source.Duration) / 2)), 3);
                t -= fade;
                pieces.Add(new RenderPiece(c, s, source, fade, Math.Round(t, 3)));
                t += source.Duration;
                previous = source;
            }
        }
        return new RenderPlan(clips, pieces, Math.Round(t, 3));
    }
}
