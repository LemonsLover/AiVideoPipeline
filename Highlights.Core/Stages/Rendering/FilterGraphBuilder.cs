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
    /// <summary>How an on-screen text looks and where it goes.</summary>
    public sealed record TextStyle(int FontSize, double Seconds, string Position, string Color, double BoxOpacity);

    /// <param name="TitleFiles">Per clip: relative path of a UTF-8 file with the title, or null.</param>
    /// <param name="CaptionFiles">Per clip: relative path of a UTF-8 file with the context caption, or null.</param>
    /// <param name="ClipTransition">xfade transition between clips.</param>
    /// <param name="InnerTransition">xfade transition at jump cuts inside a clip.</param>
    public sealed record Settings(
        int Width, int Height, double FrameRate, IReadOnlyList<int> AudioTracks,
        IReadOnlyList<string?> TitleFiles, IReadOnlyList<string?> CaptionFiles, string? FontFile,
        TextStyle Title, TextStyle Caption,
        string ClipTransition, string InnerTransition, double FadeInSeconds, double FadeOutSeconds);

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
                        sb.Append(',').Append(DrawText(caption, s.FontFile, s.Caption, delay: 0.2));
                    if (s.TitleFiles[c] is { } title)
                        sb.Append(',').Append(DrawText(title, s.FontFile, s.Title, delay: 0.3));
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
                var transition = p.SegmentIndex == 0 ? s.ClipTransition : s.InnerTransition;
                sb.Append($"[{video}][p{n}v]xfade=transition={transition}:duration={F(p.FadeIn)}:offset={F(p.OutputStart)}[vx{n}];\n");
                sb.Append($"[{audio}][p{n}a]acrossfade=d={F(p.FadeIn)}:c1=tri:c2=tri[ax{n}];\n");
            }
            else
            {
                sb.Append($"[{video}][{audio}][p{n}v][p{n}a]concat=n=2:v=1:a=1[vx{n}][ax{n}];\n");
            }
            (video, audio) = ($"vx{n}", $"ax{n}");
        }

        // Fade in from / out to black and silence; xfade may switch to 4:4:4, players and "high" profile need 4:2:0.
        var fadeIn = Math.Min(s.FadeInSeconds, plan.TotalSeconds / 4);
        var fadeOut = Math.Min(s.FadeOutSeconds, plan.TotalSeconds / 4);
        sb.Append($"[{video}]");
        if (fadeIn > 0)
            sb.Append($"fade=t=in:st=0:d={F(fadeIn)},");
        if (fadeOut > 0)
            sb.Append($"fade=t=out:st={F(plan.TotalSeconds - fadeOut)}:d={F(fadeOut)},");
        sb.Append($"format=yuv420p[{VideoOut}];\n");

        sb.Append($"[{audio}]");
        if (fadeIn > 0)
            sb.Append($"afade=t=in:st=0:d={F(fadeIn)},");
        if (fadeOut > 0)
            sb.Append($"afade=t=out:st={F(plan.TotalSeconds - fadeOut)}:d={F(fadeOut)},");
        sb.Append($"anull[{AudioOut}]");
        return sb.ToString();
    }

    private static int Index(RenderPlan plan, RenderPiece piece)
    {
        for (var i = 0; i < plan.Pieces.Count; i++)
            if (ReferenceEquals(plan.Pieces[i], piece))
                return i;
        throw new InvalidOperationException("Piece is not part of the plan.");
    }

    /// <summary>drawtext x/y for a named position (margins scale with the frame).</summary>
    internal static string Position(string position) => position switch
    {
        "center" => "x=(w-tw)/2:y=(h-th)/2",
        "bottom" => "x=(w-tw)/2:y=h-th-h*0.12",
        "top-left" => "x=w*0.04:y=h*0.08",
        "bottom-left" => "x=w*0.04:y=h-th-h*0.14",
        _ => "x=(w-tw)/2:y=h*0.08", // top
    };

    /// <summary>Text that fades in, stays for Seconds, and fades out, over a translucent box.</summary>
    private static string DrawText(string file, string font, TextStyle style, double delay)
    {
        const double fade = 0.4;
        var t0 = delay;
        var t1 = delay + style.Seconds;
        var alpha = $"if(lt(t,{F(t0)}),0,if(lt(t,{F(t0 + fade)}),(t-{F(t0)})/{F(fade)},if(lt(t,{F(t1 - fade)}),1,if(lt(t,{F(t1)}),({F(t1)}-t)/{F(fade)},0))))";
        var box = style.BoxOpacity > 0 ? $"box=1:boxcolor=black@{F(Math.Clamp(style.BoxOpacity, 0, 1))}:boxborderw=20:" : "box=0:borderw=3:bordercolor=black:";
        return $"drawtext=fontfile='{font}':textfile='{file}':fontsize={style.FontSize}:fontcolor={Color(style.Color)}:" +
               $"{box}line_spacing=8:{Position(style.Position)}:alpha='{alpha}'";
    }

    /// <summary>Color names or #RRGGBB; anything unexpected falls back to white (keeps the filtergraph valid).</summary>
    private static string Color(string color)
    {
        var c = color.Trim();
        if (c.Length == 7 && c[0] == '#' && c[1..].All(Uri.IsHexDigit))
            return "0x" + c[1..];
        return c.All(char.IsAsciiLetter) && c.Length > 0 ? c : "white";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
