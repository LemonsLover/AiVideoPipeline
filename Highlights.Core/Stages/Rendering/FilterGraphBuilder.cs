using System.Globalization;
using System.Text;

namespace Highlights.Core.Stages.Rendering;

/// <summary>
/// Builds the ffmpeg filtergraph. Every clip is one input (-ss/-t over its outer range), decoded once, normalized to
/// the output format and split into its segments; all pieces are then chained with xfade/acrossfade.
/// Loudness is normalized afterwards (two-pass).
/// </summary>
internal static class FilterGraphBuilder
{
    /// <param name="TitleFiles">Per clip: relative path of a UTF-8 file with the lower-third title, or null.</param>
    /// <param name="CaptionFiles">Per clip: relative path of a UTF-8 file with the context caption, or null.</param>
    public sealed record Settings(
        int Width, int Height, double FrameRate, IReadOnlyList<int> AudioTracks,
        IReadOnlyList<string?> TitleFiles, IReadOnlyList<string?> CaptionFiles, string? FontFile,
        int TitleFontSize, double TitleSeconds, int CaptionFontSize, double CaptionSeconds);

    public const string VideoOut = "vout";
    public const string AudioOut = "aout";

    public static string Build(RenderPlan plan, Settings s)
    {
        var sb = new StringBuilder();

        for (var c = 0; c < plan.Clips.Count; c++)
        {
            var clip = plan.Clips[c];
            var pieces = plan.Pieces.Where(p => p.ClipIndex == c).ToList();
            var k = pieces.Count;

            sb.Append($"[{c}:v:0]fps={F(s.FrameRate)},scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease,")
              .Append($"pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,")
              .Append(k > 1 ? $"split={k}" : "null")
              .Append(string.Concat(pieces.Select(p => $"[c{c}v{p.SegmentIndex}]"))).Append(";\n");

            // Mix the selected source tracks, one stream per clip, split per segment.
            sb.Append(string.Concat(s.AudioTracks.Select(t => $"[{c}:a:{t}]")));
            if (s.AudioTracks.Count > 1)
                sb.Append($"amix=inputs={s.AudioTracks.Count}:normalize=0,");
            sb.Append("aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
              .Append(k > 1 ? $"asplit={k}" : "anull")
              .Append(string.Concat(pieces.Select(p => $"[c{c}a{p.SegmentIndex}]"))).Append(";\n");

            foreach (var p in pieces)
            {
                // Segment times relative to the clip input, which starts at clip.Start.
                var from = F(p.Source.Start - clip.Start);
                var to = F(p.Source.End - clip.Start);
                var d = F(p.Source.Duration);
                var n = Index(plan, p);

                sb.Append($"[c{c}v{p.SegmentIndex}]trim=start={from}:end={to},setpts=PTS-STARTPTS");
                if (p.SegmentIndex == 0 && s.FontFile is not null)
                {
                    if (s.CaptionFiles[c] is { } caption)
                        sb.Append(',').Append(Caption(caption, s));
                    if (s.TitleFiles[c] is { } title)
                        sb.Append(',').Append(Title(title, s));
                }
                sb.Append($"[p{n}v];\n");

                // Exact length for sample-accurate crossfades.
                sb.Append($"[c{c}a{p.SegmentIndex}]atrim=start={from}:end={to},asetpts=PTS-STARTPTS,")
                  .Append($"apad=whole_dur={d},atrim=duration={d}[p{n}a];\n");
            }
        }

        var video = "p0v";
        var audio = "p0a";
        for (var n = 1; n < plan.Pieces.Count; n++)
        {
            var p = plan.Pieces[n];
            if (p.FadeIn > 0)
            {
                sb.Append($"[{video}][p{n}v]xfade=transition=fade:duration={F(p.FadeIn)}:offset={F(p.OutputStart)}[vx{n}];\n");
                sb.Append($"[{audio}][p{n}a]acrossfade=d={F(p.FadeIn)}:c1=tri:c2=tri[ax{n}];\n");
            }
            else
            {
                sb.Append($"[{video}][{audio}][p{n}v][p{n}a]concat=n=2:v=1:a=1[vx{n}][ax{n}];\n");
            }
            (video, audio) = ($"vx{n}", $"ax{n}");
        }

        // xfade may switch to 4:4:4 internally; players and the "high" profile need 4:2:0.
        sb.Append($"[{video}]format=yuv420p[{VideoOut}];\n");
        sb.Append($"[{audio}]anull[{AudioOut}]");
        return sb.ToString();
    }

    private static int Index(RenderPlan plan, RenderPiece piece)
    {
        for (var i = 0; i < plan.Pieces.Count; i++)
            if (ReferenceEquals(plan.Pieces[i], piece))
                return i;
        throw new InvalidOperationException("Piece is not part of the plan.");
    }

    /// <summary>Lower-third title that fades in and out over the first TitleSeconds of the clip.</summary>
    private static string Title(string file, Settings s) =>
        DrawText(file, s, s.TitleFontSize, s.TitleSeconds, "x=80:y=h-th-150", delay: 0.3);

    /// <summary>Context caption: top centre, shown first, so the viewer knows what's going on before the action.</summary>
    private static string Caption(string file, Settings s) =>
        DrawText(file, s, s.CaptionFontSize, s.CaptionSeconds, "x=(w-tw)/2:y=90", delay: 0.2);

    private static string DrawText(string file, Settings s, int fontSize, double seconds, string position, double delay)
    {
        const double fade = 0.4;
        var t0 = delay;
        var t1 = delay + seconds;
        var alpha = $"if(lt(t,{F(t0)}),0,if(lt(t,{F(t0 + fade)}),(t-{F(t0)})/{F(fade)},if(lt(t,{F(t1 - fade)}),1,if(lt(t,{F(t1)}),({F(t1)}-t)/{F(fade)},0))))";
        return $"drawtext=fontfile='{s.FontFile}':textfile='{file}':fontsize={fontSize}:fontcolor=white:" +
               $"box=1:boxcolor=black@0.6:boxborderw=20:line_spacing=8:{position}:alpha='{alpha}'";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
