namespace Highlights.Core.Configuration;

/// <summary>Deterministic clip building after the plan. Padding and silence trimming come from the editing mode.</summary>
public sealed class PostprocessOptions
{
    public const string SectionName = "Postprocess";

    /// <summary>Planned clips closer than this are merged into one (avoids a jump to almost the same place).</summary>
    public double MergeGapSeconds { get; set; } = 1;

    /// <summary>Move segment edges off the middle of a word, by at most this much (0 disables).</summary>
    public double MaxSnapSeconds { get; set; } = 1.5;

    /// <summary>Pieces shorter than this after cuts and silence trimming are dropped.</summary>
    public double MinSegmentSeconds { get; set; } = 0.8;
}
