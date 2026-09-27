using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Rendering;

/// <summary>Where each clip lands in the output video once crossfades overlap neighbouring clips.</summary>
public sealed record RenderPlan(IReadOnlyList<Clip> Clips, double Crossfade, IReadOnlyList<double> OutputStarts, double TotalSeconds)
{
    public static RenderPlan Create(IReadOnlyList<Clip> clips, double crossfade)
    {
        // A fade can't be longer than half of the shortest clip (it overlaps both of its ends).
        var fade = clips.Count < 2 ? 0 : Math.Max(0, Math.Min(crossfade, clips.Min(c => c.Duration) / 2));
        var starts = new List<double>();
        var t = 0.0;
        foreach (var clip in clips)
        {
            starts.Add(Math.Round(t, 3));
            t += clip.Duration - fade;
        }
        return new RenderPlan(clips, fade, starts, Math.Round(t + fade, 3));
    }
}
