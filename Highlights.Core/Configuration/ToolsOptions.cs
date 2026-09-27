namespace Highlights.Core.Configuration;

/// <summary>Paths to external binaries. Empty values mean "look up in PATH".</summary>
public sealed class ToolsOptions
{
    public const string SectionName = "Tools";

    /// <summary>Path to ffmpeg.exe or to a directory that contains it.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Path to ffprobe.exe or to a directory that contains it. Defaults to the ffmpeg directory.</summary>
    public string? FfprobePath { get; set; }
}
