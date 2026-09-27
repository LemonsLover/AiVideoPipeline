namespace Highlights.Core.Configuration;

public sealed class RenderOptions
{
    public const string SectionName = "Render";

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>Output frame rate; null = source frame rate (capped at 60).</summary>
    public double? FrameRate { get; set; }

    /// <summary>Video + audio crossfade between clips; 0 = hard cuts.</summary>
    public double CrossfadeSeconds { get; set; } = 0.5;

    /// <summary>Crossfade at jump cuts inside a clip (removed pauses and plan cuts); 0 = hard cuts.</summary>
    public double InnerCrossfadeSeconds { get; set; } = 0.2;

    /// <summary>"auto" (NVENC if it works, else libx264), "nvenc" or "x264".</summary>
    public string Encoder { get; set; } = "auto";

    public string NvencPreset { get; set; } = "p5";
    public int NvencCq { get; set; } = 19;
    public string X264Preset { get; set; } = "medium";
    public int X264Crf { get; set; } = 18;
    public string AudioBitrate { get; set; } = "192k";

    /// <summary>Loudness normalization target (YouTube normalizes to about -14 LUFS); null disables it.</summary>
    public double? LoudnessLufs { get; set; } = -14;
    public double TruePeakDb { get; set; } = -1.5;

    /// <summary>Source audio tracks (0-based) mixed into the video; null = the first track.</summary>
    public int[]? AudioTracks { get; set; }

    /// <summary>On-screen title caption at the start of each clip (per project: render --titles).</summary>
    public bool Titles { get; set; }
    public double TitleSeconds { get; set; } = 3;
    /// <summary>Font for titles and captions.</summary>
    public string TitleFont { get; set; } = @"C:\Windows\Fonts\segoeuib.ttf";
    public int TitleFontSize { get; set; } = 56;

    /// <summary>Context captions (from the plan) at the start of a clip.</summary>
    public double CaptionSeconds { get; set; } = 4;
    public int CaptionFontSize { get; set; } = 44;

    public IReadOnlyList<int> EffectiveAudioTracks => AudioTracks is { Length: > 0 } ? AudioTracks : [0];
}
