namespace Highlights.Core.Media;

public sealed record MediaInfo
{
    public required string Container { get; init; }
    public required double DurationSeconds { get; init; }
    public long? SizeBytes { get; init; }
    public VideoStreamInfo? Video { get; init; }
    public required IReadOnlyList<AudioTrackInfo> AudioTracks { get; init; }
}

public sealed record VideoStreamInfo
{
    public required int StreamIndex { get; init; }
    public required string Codec { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public double? FrameRate { get; init; }
}

public sealed record AudioTrackInfo
{
    /// <summary>0-based index among audio streams (ffmpeg <c>-map 0:a:N</c>).</summary>
    public required int Index { get; init; }

    /// <summary>Absolute stream index in the container.</summary>
    public required int StreamIndex { get; init; }

    public required string Codec { get; init; }
    public int Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int SampleRate { get; init; }
    public long? BitRate { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsDefault { get; init; }
}
