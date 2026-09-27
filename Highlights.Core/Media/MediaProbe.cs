using System.Globalization;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Media;

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Reads container/stream information via <c>ffprobe -print_format json</c>.</summary>
public sealed class MediaProbe(ToolLocator tools) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap(tools.Ffprobe)
            .WithArguments(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
            throw new PipelineException($"ffprobe failed for {path}: {result.StandardError.Trim()}");

        using var doc = JsonDocument.Parse(result.StandardOutput);
        return Parse(doc.RootElement);
    }

    internal static MediaInfo Parse(JsonElement root)
    {
        var format = root.GetProperty("format");
        VideoStreamInfo? video = null;
        var audio = new List<AudioTrackInfo>();

        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            switch (Str(s, "codec_type"))
            {
                case "video" when video is null && !IsAttachedPicture(s):
                    video = new VideoStreamInfo
                    {
                        StreamIndex = s.GetProperty("index").GetInt32(),
                        Codec = Str(s, "codec_name") ?? "unknown",
                        Width = Int(s, "width") ?? 0,
                        Height = Int(s, "height") ?? 0,
                        FrameRate = Rational(Str(s, "avg_frame_rate")) ?? Rational(Str(s, "r_frame_rate")),
                    };
                    break;

                case "audio":
                    audio.Add(new AudioTrackInfo
                    {
                        Index = audio.Count,
                        StreamIndex = s.GetProperty("index").GetInt32(),
                        Codec = Str(s, "codec_name") ?? "unknown",
                        Channels = Int(s, "channels") ?? 0,
                        ChannelLayout = Str(s, "channel_layout"),
                        SampleRate = Int(s, "sample_rate") ?? 0,
                        BitRate = Long(s, "bit_rate"),
                        Language = Tag(s, "language") is { } lang && lang != "und" ? lang : null,
                        Title = Tag(s, "title") ?? Tag(s, "handler_name"),
                        DurationSeconds = Double(s, "duration") ?? TimeTag(Tag(s, "DURATION")),
                        IsDefault = s.TryGetProperty("disposition", out var d) && Int(d, "default") == 1,
                    });
                    break;
            }
        }

        return new MediaInfo
        {
            Container = Str(format, "format_name") ?? "unknown",
            DurationSeconds = Double(format, "duration") ?? 0,
            SizeBytes = Long(format, "size"),
            Video = video,
            AudioTracks = audio,
        };
    }

    private static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static string? Tag(JsonElement s, string name)
    {
        if (!s.TryGetProperty("tags", out var tags))
            return null;
        // Tag case differs between containers (e.g. "DURATION" in mkv, "title" vs "TITLE").
        foreach (var p in tags.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString();
        return null;
    }

    private static int? Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Double(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Rational(string? value)
    {
        var parts = value?.Split('/');
        if (parts is not [var n, var d]
            || !double.TryParse(n, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(d, CultureInfo.InvariantCulture, out var den)
            || den == 0)
            return null;
        return num / den;
    }

    /// <summary>Parses mkv "00:41:30.154000000" (TimeSpan only accepts 7 fractional digits).</summary>
    private static double? TimeTag(string? value)
    {
        if (value?.Split(':') is not [var h, var m, var s])
            return null;
        return int.TryParse(h, CultureInfo.InvariantCulture, out var hh)
               && int.TryParse(m, CultureInfo.InvariantCulture, out var mm)
               && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ss)
            ? hh * 3600 + mm * 60 + ss
            : null;
    }
}
