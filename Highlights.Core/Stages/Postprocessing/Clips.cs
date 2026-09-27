namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Contents of clips.json: the edit decision list built from the plan — which source ranges go into the video.</summary>
public sealed record ClipsDocument
{
    public required string Mode { get; init; }
    public required double TargetMinutes { get; init; }
    public required double TotalSeconds { get; init; }
    public required IReadOnlyList<Clip> Clips { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Clip
{
    public required string Id { get; init; }

    /// <summary>Outer source range (first segment start … last segment end).</summary>
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Pieces of source video actually used, in order; gaps between them are cut out (jump cuts).</summary>
    public required IReadOnlyList<TimeRange> Segments { get; init; }

    /// <summary>Chapter title / optional on-screen title.</summary>
    public required string Title { get; init; }

    /// <summary>On-screen context line at the start of the clip, if any.</summary>
    public string? Caption { get; init; }

    public required string Category { get; init; }
    public required int Score { get; init; }

    /// <summary>Beats (moments.json ids) the clip shows; used as the stable key for review edits.</summary>
    public required IReadOnlyList<string> MomentIds { get; init; }

    /// <summary>Key for review edits: the first beat id, or the plan clip id for pure bridge clips.</summary>
    public required string Key { get; init; }

    public double Duration => Segments.Sum(s => s.Duration);
}
