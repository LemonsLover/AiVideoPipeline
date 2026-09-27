namespace Highlights.Core.Configuration;

public sealed class PostprocessOptions
{
    public const string SectionName = "Postprocess";

    /// <summary>Context added before each moment (setup).</summary>
    public double PrePaddingSeconds { get; set; } = 6;

    /// <summary>Context added after each moment (reaction tail).</summary>
    public double PostPaddingSeconds { get; set; } = 2.5;

    /// <summary>Moments below this score are dropped. Overridable per project (--min-score).</summary>
    public int MinScore { get; set; } = 6;

    /// <summary>Target video length in minutes; null = take every moment above the threshold (--target).</summary>
    public double? TargetMinutes { get; set; }

    /// <summary>Clips closer than this are merged into one (avoids a jump cut to almost the same place).</summary>
    public double MergeGapSeconds { get; set; } = 2;

    /// <summary>Move clip edges off the middle of a word, by at most this much (0 disables).</summary>
    public double MaxSnapSeconds { get; set; } = 1.5;
}
