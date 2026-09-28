namespace Highlights.Core.Configuration;

public sealed class RenderOptions
{
    public const string SectionName = "Render";

    /// <summary>ffmpeg xfade transitions offered in the settings (see ffmpeg's xfade filter for more).</summary>
    public static readonly string[] Transitions =
    [
        "fade", "dissolve", "fadeblack", "fadewhite", "fadegrays", "smoothleft", "smoothright", "slideleft", "slideright",
        "wipeleft", "wiperight", "circleopen", "circleclose", "radial", "pixelize", "hblur", "zoomin", "distance",
    ];

    public static readonly string[] TextPositions = ["top", "center", "bottom", "top-left", "bottom-left"];

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>Output frame rate; null = source frame rate (capped at 60).</summary>
    public double? FrameRate { get; set; }

    /// <summary>Transition between clips (xfade name) and its length; 0 s = hard cut.</summary>
    public string ClipTransition { get; set; } = "fade";
    public double CrossfadeSeconds { get; set; } = 0.5;

    /// <summary>Transition at jump cuts inside a clip (removed pauses and plan cuts) and its length; 0 s = hard cut.</summary>
    public string InnerTransition { get; set; } = "fade";
    public double InnerCrossfadeSeconds { get; set; } = 0.2;

    /// <summary>Fade from/to black (and silence) at the very start and end of the video; 0 = none.</summary>
    public double FadeInSeconds { get; set; } = 0.5;
    public double FadeOutSeconds { get; set; } = 1;

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

    /// <summary>Font for titles and captions.</summary>
    public string TitleFont { get; set; } = @"C:\Windows\Fonts\segoeuib.ttf";

    /// <summary>On-screen title at the start of each clip (per project: the "Title captions" switch).</summary>
    public bool Titles { get; set; }
    public double TitleSeconds { get; set; } = 3;
    public int TitleFontSize { get; set; } = 56;
    public string TitlePosition { get; set; } = "bottom-left";
    public string TitleColor { get; set; } = "white";
    public double TitleBoxOpacity { get; set; } = 0.55;

    /// <summary>Context captions written by the plan; off = never drawn, even if the plan wrote them.</summary>
    public bool Captions { get; set; } = true;
    public double CaptionSeconds { get; set; } = 4;
    public int CaptionFontSize { get; set; } = 44;
    public string CaptionPosition { get; set; } = "top";
    public string CaptionColor { get; set; } = "white";
    public double CaptionBoxOpacity { get; set; } = 0.6;

    /// <summary>Longer captions/titles are wrapped to several lines at word boundaries.</summary>
    public int MaxLineChars { get; set; } = 55;

    public IReadOnlyList<int> EffectiveAudioTracks => AudioTracks is { Length: > 0 } ? AudioTracks : [0];
}
