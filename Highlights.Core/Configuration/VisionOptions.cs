namespace Highlights.Core.Configuration;

/// <summary>What happens on screen: frame extraction, motion, and a vision LLM that watches the frames.</summary>
public sealed class VisionOptions
{
    public const string SectionName = "Vision";

    /// <summary>Off = no frames are sent to an LLM (motion and game sounds are still used).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Vision-capable models, primary first.</summary>
    public string[]? Models { get; set; }

    /// <summary>Frames kept on disk for the vision passes (1 per second, this wide).</summary>
    public int FrameWidth { get; set; } = 512;

    /// <summary>ffmpeg JPEG quality (2 = best … 31 = worst).</summary>
    public int JpegQuality { get; set; } = 6;

    /// <summary>Overview pass: one frame every N seconds of the whole video.</summary>
    public int OverviewIntervalSeconds { get; set; } = 5;

    /// <summary>Overview pass: frames per request (60 × 5 s = 5 minutes of video).</summary>
    public int OverviewBatchFrames { get; set; } = 60;

    /// <summary>
    /// Overview frames at low detail: ~4× cheaper; kills, deaths and rounds are still recognized, but small text
    /// (nicknames, exact score) is often misread. Refinement always uses full detail.
    /// </summary>
    public bool OverviewLowDetail { get; set; } = true;

    /// <summary>Overview requests sent at the same time.</summary>
    public int MaxParallelRequests { get; set; } = 3;

    /// <summary>Refine pass: seconds of footage before a clip's in point the model looks at (1 frame/s).</summary>
    public int RefineLeadInSeconds { get; set; } = 20;

    /// <summary>Refine pass: seconds after the out point the model looks at.</summary>
    public int RefineTailSeconds { get; set; } = 6;

    /// <summary>Motion above this multiple of the median counts as on-screen action (protects it from pause trimming).</summary>
    public double ActionMotionFactor { get; set; } = 2.5;

    public IReadOnlyList<string> EffectiveModels => Models is { Length: > 0 }
        ? Models
        : ["google/gemini-3.8-flash", "anthropic/claude-sonnet-5"];
}
