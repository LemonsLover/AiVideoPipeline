using System.Globalization;
using System.Text;

namespace Highlights.Core.Stages.Rendering;

/// <summary>
/// Builds the ffmpeg filtergraph: every clip is its own input (-ss/-t on the source), normalized to the output
/// format, optionally captioned, then chained with xfade/acrossfade. Loudness is normalized afterwards (two-pass).
/// </summary>
internal static class FilterGraphBuilder
{
    public sealed record Settings(
        int Width, int Height, double FrameRate, IReadOnlyList<int> AudioTracks,
        /* Per-clip caption: relative path of a UTF-8 text file, or null. */ IReadOnlyList<string?> TitleFiles,
        string? FontFile, int FontSize, double TitleSeconds);

    public const string VideoOut = "vout";
    public const string AudioOut = "aout";

    public static string Build(RenderPlan plan, Settings s)
    {
        var sb = new StringBuilder();
        var n = plan.Clips.Count;

        for (var i = 0; i < n; i++)
        {
            var d = F(plan.Clips[i].Duration);
            sb.Append($"[{i}:v:0]trim=duration={d},setpts=PTS-STARTPTS,fps={F(s.FrameRate)},")
              .Append($"scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease,")
              .Append($"pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p");
            if (s.TitleFiles[i] is { } titleFile && s.FontFile is not null)
                sb.Append(',').Append(Caption(titleFile, s));
            sb.Append($"[v{i}];\n");

            // Mix the selected source tracks, then force an exact clip length for sample-accurate crossfades.
            sb.Append(string.Concat(s.AudioTracks.Select(t => $"[{i}:a:{t}]")));
            if (s.AudioTracks.Count > 1)
                sb.Append($"amix=inputs={s.AudioTracks.Count}:normalize=0,");
            sb.Append("aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
              .Append($"atrim=duration={d},apad=whole_dur={d},asetpts=PTS-STARTPTS[a{i}];\n");
        }

        var video = "v0";
        var audio = "a0";
        for (var i = 1; i < n; i++)
        {
            if (plan.Crossfade > 0)
            {
                sb.Append($"[{video}][v{i}]xfade=transition=fade:duration={F(plan.Crossfade)}:offset={F(plan.OutputStarts[i])}[vx{i}];\n");
                sb.Append($"[{audio}][a{i}]acrossfade=d={F(plan.Crossfade)}:c1=tri:c2=tri[ax{i}];\n");
            }
            else
            {
                sb.Append($"[{video}][{audio}][v{i}][a{i}]concat=n=2:v=1:a=1[vx{i}][ax{i}];\n");
            }
            (video, audio) = ($"vx{i}", $"ax{i}");
        }

        // xfade may switch to 4:4:4 internally; players and the "high" profile need 4:2:0.
        sb.Append($"[{video}]format=yuv420p[{VideoOut}];\n");
        sb.Append($"[{audio}]anull[{AudioOut}]");
        return sb.ToString();
    }

    /// <summary>Lower-third caption that fades in and out over the first TitleSeconds of the clip.</summary>
    private static string Caption(string titleFile, Settings s)
    {
        const double fade = 0.4, delay = 0.3;
        var t0 = delay;
        var t1 = delay + s.TitleSeconds;
        var alpha = $"if(lt(t,{F(t0)}),0,if(lt(t,{F(t0 + fade)}),(t-{F(t0)})/{F(fade)},if(lt(t,{F(t1 - fade)}),1,if(lt(t,{F(t1)}),({F(t1)}-t)/{F(fade)},0))))";
        return $"drawtext=fontfile='{s.FontFile}':textfile='{titleFile}':fontsize={s.FontSize}:fontcolor=white:" +
               $"box=1:boxcolor=black@0.55:boxborderw=22:x=80:y=h-th-150:alpha='{alpha}'";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
