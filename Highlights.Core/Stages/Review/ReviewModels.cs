using Highlights.Core.Stages.Postprocessing;

namespace Highlights.Core.Stages.Review;

/// <summary>
/// Contents of review.json: the user's edits to the planned clips. Edits are keyed by Clip.Key (the clip's first beat id),
/// so they survive re-running the plan or postprocess; they're ignored when moments.json changes.
/// </summary>
public sealed class ReviewDocument
{
    /// <summary>Hash of the moments.json the edits were made against.</summary>
    public string? MomentsHash { get; set; }

    public Dictionary<string, ClipEdit> Clips { get; set; } = [];
}

public sealed class ClipEdit
{
    /// <summary>False = cut from the video.</summary>
    public bool Included { get; set; } = true;

    /// <summary>Seconds added to the start (negative = start earlier).</summary>
    public double StartOffset { get; set; }

    /// <summary>Seconds added to the end (positive = end later).</summary>
    public double EndOffset { get; set; }

    /// <summary>Replacement title (captions, chapters).</summary>
    public string? Title { get; set; }

    /// <summary>Replacement context caption; "" removes the planned one, null keeps it.</summary>
    public string? Caption { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => Included && StartOffset == 0 && EndOffset == 0 && Title is null && Caption is null;
}

/// <summary>Contents of edit.json: the final clip list that gets rendered.</summary>
public sealed record EditDocument
{
    public required double TotalSeconds { get; init; }
    public required IReadOnlyList<Clip> Clips { get; init; }

    /// <summary>True when review.json was made for a different moments.json and was ignored.</summary>
    public bool ReviewIgnored { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
