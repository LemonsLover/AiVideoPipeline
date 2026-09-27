namespace Highlights.Core.Stages.Postprocessing;

/// <summary>Contents of clips.json: the edit decision list — which parts of the video go into the highlights.</summary>
public sealed record ClipsDocument
{
    public required int MinScore { get; init; }
    public double? TargetMinutes { get; init; }
    public required double TotalSeconds { get; init; }
    public required IReadOnlyList<Clip> Clips { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Clip
{
    public required string Id { get; init; }

    /// <summary>Source video times, padding included.</summary>
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Title of the best moment in the clip (on-screen caption / YouTube chapter).</summary>
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required int Score { get; init; }

    /// <summary>Moments (moments.json ids) this clip covers; several when merged.</summary>
    public required IReadOnlyList<string> MomentIds { get; init; }

    public double Duration => End - Start;
}
